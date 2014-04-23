﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using Gridsum.DataflowEx.Exceptions;
using Gridsum.DataflowEx.PatternMatch;

namespace Gridsum.DataflowEx
{
    /// <summary>
    /// Core concept of DataflowEx. Represents a reusable dataflow component with its processing logic, which
    /// may contain one or multiple blocks. Inheritors should call RegisterBlock in their constructors.
    /// </summary>
    public abstract class BlockContainer : IBlockContainer
    {
        private static ConcurrentDictionary<string, IntHolder> s_nameDict = new ConcurrentDictionary<string, IntHolder>();
        protected readonly BlockContainerOptions m_containerOptions;
        protected readonly DataflowLinkOptions m_defaultLinkOption;
        protected Lazy<Task> m_completionTask;
        protected ImmutableList<IChildMeta> m_children = ImmutableList.Create<IChildMeta>();
        protected string m_defaultName;

        public BlockContainer(BlockContainerOptions containerOptions)
        {
            m_containerOptions = containerOptions;
            m_defaultLinkOption = new DataflowLinkOptions() { PropagateCompletion = true };
            m_completionTask = new Lazy<Task>(GetCompletionTask, LazyThreadSafetyMode.ExecutionAndPublication);

            string friendlyName = Utils.GetFriendlyName(this.GetType());
            int count = s_nameDict.GetOrAdd(friendlyName, new IntHolder()).Increment();
            m_defaultName = friendlyName + count;
            
            if (m_containerOptions.ContainerMonitorEnabled || m_containerOptions.BlockMonitorEnabled)
            {
                StartPerformanceMonitorAsync();
            }
        }

        /// <summary>
        /// Display name of the container
        /// </summary>
        public virtual string Name
        {
            get { return m_defaultName; }
        }
        
        /// <summary>
        /// Register this block to block meta. Also make sure the container will fail if the registered block fails.
        /// </summary>
        protected void RegisterChild(IDataflowBlock block, Action<Task> blockCompletionCallback = null)
        {
            if (block == null)
            {
                throw new ArgumentNullException("block");
            }

            if (m_children.Any(m => m.Blocks.Contains(block)))
            {
                throw new ArgumentException("Duplicate block registered in " + this.Name);
            }

            var wrappedCompletion = WrapUnitCompletion(block.Completion, Utils.GetFriendlyName(block.GetType()),blockCompletionCallback);
            m_children = m_children.Add(new BlockMeta(block, wrappedCompletion));
        }

        protected void RegisterChild(BlockContainer childContainer, Action<Task> containerCompletionCallback = null)
        {
            if (childContainer == null)
            {
                throw new ArgumentNullException("childContainer");
            }
            
            //todo: duplicate block container check?

            var wrappedCompletion = WrapUnitCompletion(childContainer.CompletionTask, childContainer.Name, containerCompletionCallback);
            m_children = m_children.Add(new BlockContainerMeta(childContainer, wrappedCompletion));
        }

        /// <summary>
        /// The Wrapping does 2 things:
        /// (1) propagate error to other units of the block container
        /// (2) call completion back of the unit
        /// </summary>
        protected Task WrapUnitCompletion(Task unitCompletion, string unitName, Action<Task> completionCallback)
        {
            var tcs = new TaskCompletionSource<object>();

            unitCompletion.ContinueWith(task =>
            {
                if (task.Status == TaskStatus.Faulted)
                {
                    var exception = TaskEx.UnwrapWithPriority(task.Exception);
                    tcs.SetException(exception);

                    if (!(exception is PropagatedException))
                    {
                        this.Fault(exception); //fault other blocks if this is an original exception
                    }
                }
                else if (task.Status == TaskStatus.Canceled)
                {
                    tcs.SetCanceled();
                    this.Fault(new TaskCanceledException());
                }
                else //success
                {
                    try
                    {
                        //call callback
                        if (completionCallback != null)
                        {
                            completionCallback(task);
                        }
                        tcs.SetResult(string.Empty);
                    }
                    catch (Exception e)
                    {
                        LogHelper.Logger.Error(h => h("[{0}] Error when callback {1} on its completion", this.Name, unitName), e);
                        tcs.SetException(e);
                        this.Fault(e);
                    }
                }
            });

            return tcs.Task;
        }
        
        //todo: add completion condition and cancellation token support
        private async Task StartPerformanceMonitorAsync()
        {
            while (true)
            {
                await Task.Delay(m_containerOptions.MonitorInterval ?? TimeSpan.FromSeconds(10));

                if (m_containerOptions.ContainerMonitorEnabled)
                {
                    int count = this.BufferedCount;

                    if (count != 0 || m_containerOptions.PerformanceMonitorMode == BlockContainerOptions.PerformanceLogMode.Verbose)
                    {
                        LogHelper.Logger.Debug(h => h("[{0}] has {1} todo items at this moment.", this.Name, count));
                    }
                }

                if (m_containerOptions.BlockMonitorEnabled)
                {
                    foreach (BlockMeta bm in m_children)
                    {
                        IDataflowBlock block = bm.Block;
                        var count = bm.BufferCount;

                        if (count != 0 || m_containerOptions.PerformanceMonitorMode == BlockContainerOptions.PerformanceLogMode.Verbose)
                        {
                            LogHelper.Logger.Debug(h => h("[{0}->{1}] has {2} todo items at this moment.", this.Name, Utils.GetFriendlyName(block.GetType()), count));
                        }
                    }
                }
            }
        }

        protected virtual async Task GetCompletionTask()
        {
            if (m_children.Count == 0)
            {
                throw new NoChildRegisteredException(this);
            }

            ImmutableList<IChildMeta> childrenSnapShot;

            do
            {
                childrenSnapShot = m_children;
                await TaskEx.AwaitableWhenAll(childrenSnapShot.Select(b => b.ChildCompletion).ToArray());
            } while (!object.ReferenceEquals(m_children, childrenSnapShot));

            this.CleanUp();
        }

        protected virtual void CleanUp()
        {
            //
        }

        /// <summary>
        /// Represents the completion of the whole container
        /// </summary>
        public Task CompletionTask
        {
            get
            {
                return m_completionTask.Value;
            }
        }

        public virtual IEnumerable<IDataflowBlock> Blocks { get { return m_children.SelectMany(bm => bm.Blocks); } }

        public virtual void Fault(Exception exception)
        {
            LogHelper.Logger.ErrorFormat("<{0}> Exception occur. Shutting down my working blocks...", exception, this.Name);

            foreach (var dataflowBlock in Blocks)
            {
                if (!dataflowBlock.Completion.IsCompleted)
                {
                    string msg = string.Format("<{0}> Shutting down {1}", this.Name, Utils.GetFriendlyName(dataflowBlock.GetType()));
                    LogHelper.Logger.Error(msg);

                    //just pass on PropagatedException (do not use original exception here)
                    if (exception is PropagatedException)
                    {
                        dataflowBlock.Fault(exception);
                    }
                    else if (exception is TaskCanceledException)
                    {
                        dataflowBlock.Fault(new SiblingUnitCanceledException());
                    }
                    else
                    {
                        dataflowBlock.Fault(new SiblingUnitFailedException());
                    }
                }
            }
        }

        /// <summary>
        /// Sum of the buffer size of all blocks in the container
        /// </summary>
        public virtual int BufferedCount
        {
            get
            {
                return m_children.Sum(bm => bm.BufferCount);
            }
        }
    }

    public abstract class BlockContainer<TIn> : BlockContainer, IBlockContainer<TIn>
    {
        protected BlockContainer(BlockContainerOptions containerOptions) : base(containerOptions)
        {
        }

        public abstract ITargetBlock<TIn> InputBlock { get; }
        
        /// <summary>
        /// Helper method to read from a text reader and post everything in the text reader to the pipeline
        /// </summary>
        public void PullFrom(IEnumerable<TIn> reader)
        {
            long count = 0;
            foreach(var item in reader)
            {
                InputBlock.SafePost(item);
                count++;
            }

            LogHelper.Logger.Info(h => h("<{0}> Pulled and posted {1} {2}s to the input block {3}.", 
                this.Name, 
                count, 
                Utils.GetFriendlyName(typeof(TIn)), 
                Utils.GetFriendlyName(this.InputBlock.GetType())
                ));
        }

        public void LinkFrom(ISourceBlock<TIn> block)
        {
            block.LinkTo(this.InputBlock, m_defaultLinkOption);
        }
    }

    public abstract class BlockContainer<TIn, TOut> : BlockContainer<TIn>, IBlockContainer<TIn, TOut>
    {
        protected List<Predicate<TOut>> m_conditions = new List<Predicate<TOut>>();
        protected StatisticsRecorder GarbageRecorder { get; private set; }

        protected BlockContainer(BlockContainerOptions containerOptions) : base(containerOptions)
        {
            this.GarbageRecorder = new StatisticsRecorder();
        }

        public abstract ISourceBlock<TOut> OutputBlock { get; }
        
        protected void LinkBlockToContainer<T>(ISourceBlock<T> block, IBlockContainer<T> otherBlockContainer)
        {
            block.LinkTo(otherBlockContainer.InputBlock, new DataflowLinkOptions { PropagateCompletion = false });

            //manullay handle inter-container problem
            //we use WhenAll here to make sure this container fails before propogating to other container
            Task.WhenAll(block.Completion, this.CompletionTask).ContinueWith(whenAllTask => 
                {
                    if (!otherBlockContainer.CompletionTask.IsCompleted)
                    {
                        if (whenAllTask.IsFaulted)
                        {
                            otherBlockContainer.Fault(new OtherBlockContainerFailedException());
                        }
                        else if (whenAllTask.IsCanceled)
                        {
                            otherBlockContainer.Fault(new OtherBlockContainerCanceledException());
                        }
                        else
                        {
                            otherBlockContainer.InputBlock.Complete();
                        }
                    }
                });

            //Make sure other container also fails me
            otherBlockContainer.CompletionTask.ContinueWith(otherTask =>
                {
                    if (this.CompletionTask.IsCompleted)
                    {
                        return;
                    }

                    if (otherTask.IsFaulted)
                    {
                        LogHelper.Logger.InfoFormat("<{0}>Downstream block container faulted before I am done. Fault myself.", this.Name);
                        this.Fault(new OtherBlockContainerFailedException());
                    }
                    else if (otherTask.IsCanceled)
                    {
                        LogHelper.Logger.InfoFormat("<{0}>Downstream block container canceled before I am done. Cancel myself.", this.Name);
                        this.Fault(new OtherBlockContainerCanceledException());
                    }
                });
        }

        public void LinkTo(IBlockContainer<TOut> other)
        {
            LinkBlockToContainer(this.OutputBlock, other);
        }

        public void TransformAndLink<TTarget>(IBlockContainer<TTarget> other, Func<TOut, TTarget> transform, IMatchCondition<TOut> condition)
        {
            this.TransformAndLink(other, transform, new Predicate<TOut>(condition.Matches));
        }

        public void TransformAndLink<TTarget>(IBlockContainer<TTarget> other, Func<TOut, TTarget> transform, Predicate<TOut> predicate)
        {
            m_conditions.Add(predicate);
            var converter = new TransformBlock<TOut, TTarget>(transform);
            this.OutputBlock.LinkTo(converter, m_defaultLinkOption, predicate);
            
            LinkBlockToContainer(converter, other);            
        }

        public void TransformAndLink<TTarget>(IBlockContainer<TTarget> other, Func<TOut, TTarget> transform)
        {
            this.TransformAndLink(other, transform, @out => true);
        }

        public void TransformAndLink<TTarget>(IBlockContainer<TTarget> other) where TTarget : TOut
        {
            this.TransformAndLink(other, @out => { return ((TTarget)@out); }, @out => @out is TTarget);
        }

        public void TransformAndLink<TTarget, TOutSubType>(IBlockContainer<TTarget> other, Func<TOutSubType, TTarget> transform) where TOutSubType : TOut
        {
            this.TransformAndLink(other, @out => { return transform(((TOutSubType)@out)); }, @out => @out is TOutSubType);
        }

        public void LinkLeftToNull()
        {
            var left = new Predicate<TOut>(@out =>
                {
                    if (m_conditions.All(condition => !condition(@out)))
                    {
                        OnOutputToNull(@out);
                        return true;
                    }
                    else
                    {
                        return false;
                    }
                }
                );
            this.OutputBlock.LinkTo(DataflowBlock.NullTarget<TOut>(), m_defaultLinkOption, left);
        }

        protected virtual void OnOutputToNull(TOut output)
        {
            this.GarbageRecorder.RecordType(output.GetType());
        }
    }
}