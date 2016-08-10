﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using SiliconStudio.Core.Collections;

namespace SiliconStudio.Core.Threading
{
    public class Dispatcher
    {
        private static readonly ConcurrentPool<BatchState> loopStatePool = new ConcurrentPool<BatchState>(() => new BatchState());
        private static readonly ConcurrentPool<SortState> sortStatePool = new ConcurrentPool<SortState>(() => new SortState());

        private static readonly int MaxDregreeOfParallelism = Environment.ProcessorCount;

        public delegate void ValueAction<T>(ref T obj);

        public static void For(int fromInclusive, int toExclusive, [Pooled] Action<int> action)
        {
            using (Profile(action))
            {
                if (fromInclusive > toExclusive)
                {
                    var temp = fromInclusive;
                    fromInclusive = toExclusive + 1;
                    toExclusive = temp + 1;
                }

                var count = toExclusive - fromInclusive;
                if (count == 0)
                    return;

                if (MaxDregreeOfParallelism <= 1 || count == 1)
                {
                    ExecuteBatch(fromInclusive, toExclusive, action);
                }
                else
                {
                    var state = loopStatePool.Acquire();
                    state.ActiveWorkerCount = 1;
                    state.StartInclusive = fromInclusive;

                    try
                    {
                        int batchCount = Math.Min(MaxDregreeOfParallelism, count);
                        int batchSize = (count + (batchCount - 1)) / batchCount;

                        // Kick off a worker, then perform work synchronously
                        Fork(toExclusive, batchSize, MaxDregreeOfParallelism, action, state);

                        // Wait for all workers to finish
                        state.Finished.WaitOne();
                    }
                    finally
                    {
                        loopStatePool.Release(state);
                    }
                }
            }
        }

        public static void For<TLocal>(int fromInclusive, int toExclusive, [Pooled] Func<TLocal> initializeLocal, [Pooled] Action<int, TLocal> action, [Pooled] Action<TLocal> finalizeLocal = null)
        {
            using (Profile(action))
            {
                if (fromInclusive > toExclusive)
                {
                    var temp = fromInclusive;
                    fromInclusive = toExclusive + 1;
                    toExclusive = temp + 1;
                }

                var count = toExclusive - fromInclusive;
                if (count == 0)
                    return;

                if (MaxDregreeOfParallelism <= 1 || count == 1)
                {
                    ExecuteBatch(fromInclusive, toExclusive, initializeLocal, action, finalizeLocal);
                }
                else
                {
                    var state = loopStatePool.Acquire();
                    state.ActiveWorkerCount = 1;
                    state.StartInclusive = fromInclusive;

                    try
                    {
                        int batchCount = Math.Min(MaxDregreeOfParallelism, count);
                        int batchSize = (count + (batchCount - 1)) / batchCount;

                        // Kick off a worker, then perform work synchronously
                        Fork(toExclusive, batchSize, MaxDregreeOfParallelism, initializeLocal, action, finalizeLocal, state);

                        // Wait for all workers to finish
                        state.Finished.WaitOne();
                    }
                    finally
                    {
                        loopStatePool.Release(state);
                    }
                }
            }
        }
        
        public static void ForEach<TItem, TLocal>(IReadOnlyList<TItem> collection, [Pooled] Func<TLocal> initializeLocal, [Pooled] Action<TItem, TLocal> action, [Pooled] Action<TLocal> finalizeLocal = null)
        {
            For(0, collection.Count, initializeLocal, (i, local) => action(collection[i], local), finalizeLocal);
        }

        public static void ForEach<T>(IReadOnlyList<T> collection, [Pooled] Action<T> action)
        {
            For(0, collection.Count, i => action(collection[i]));
        }


        public static void ForEach<T>(List<T> collection, [Pooled] Action<T> action)
        {
            For(0, collection.Count, i => action(collection[i]));
        }

        public static void ForEach<TKey, TValue>(Dictionary<TKey, TValue> collection, [Pooled] Action<KeyValuePair<TKey, TValue>> action)
        {
            if (MaxDregreeOfParallelism <= 1 || collection.Count <= 1)
            {
                ExecuteBatch(collection, 0, collection.Count, action);
            }
            else
            {
                var state = loopStatePool.Acquire();
                state.ActiveWorkerCount = 1;
                state.StartInclusive = 0;

                try
                {
                    int batchCount = Math.Min(MaxDregreeOfParallelism, collection.Count);
                    int batchSize = (collection.Count + (batchCount - 1)) / batchCount;

                    // Wait for all workers to finish
                    Fork(collection, batchSize, MaxDregreeOfParallelism, action, state);

                    // Kick off a worker, then perform work synchronously
                    state.Finished.WaitOne();
                }
                finally
                {
                    loopStatePool.Release(state);
                }
            }
        }

        public static void ForEach<T>(FastCollection<T> collection, [Pooled] Action<T> action)
        {
            For(0, collection.Count, i => action(collection[i]));
        }

        public static void ForEach<T>(FastList<T> collection, [Pooled] Action<T> action)
        {
            For(0, collection.Count, i => action(collection.Items[i]));
        }

        public static void ForEach<T>(ConcurrentCollector<T> collection, [Pooled] Action<T> action)
        {
            For(0, collection.Count, i => action(collection.Items[i]));
        }

        public static void ForEach<T>(FastList<T> collection, [Pooled] ValueAction<T> action)
        {
            For(0, collection.Count, i => action(ref collection.Items[i]));
        }

        public static void ForEach<T>(ConcurrentCollector<T> collection, [Pooled] ValueAction<T> action)
        {
            For(0, collection.Count, i => action(ref collection.Items[i]));
        }

        private static void Fork<TKey, TValue>(Dictionary<TKey, TValue> collection, int batchSize, int maxDegreeOfParallelism, [Pooled] Action<KeyValuePair<TKey, TValue>> action, BatchState state)
        {
            // Kick off another worker if there's any work left
            if (maxDegreeOfParallelism > 1 && state.StartInclusive + batchSize < collection.Count)
            {
                Interlocked.Increment(ref state.ActiveWorkerCount);
                ThreadPool.Instance.QueueWorkItem(() => Fork(collection, batchSize, maxDegreeOfParallelism - 1, action, state));
            }

            try
            {
                // Process batches synchronously as long as there are any
                int newStart;
                while ((newStart = Interlocked.Add(ref state.StartInclusive, batchSize)) - batchSize < collection.Count)
                {
                    // TODO: Reuse enumerator when processing multiple batches synchronously
                    ExecuteBatch(collection, newStart - batchSize, Math.Min(collection.Count, newStart), action);
                }
            }
            finally
            {
                // If this was the last batch, signal
                if (Interlocked.Decrement(ref state.ActiveWorkerCount) == 0)
                {
                    state.Finished.Set();
                }
            }
        }

        private static void ExecuteBatch(int fromInclusive, int toExclusive, [Pooled] Action<int> action)
        {
            for (int i = fromInclusive; i < toExclusive; i++)
            {
                action(i);
            }
        }

        private static void ExecuteBatch<TLocal>(int fromInclusive, int toExclusive, [Pooled] Func<TLocal> initializeLocal, [Pooled] Action<int, TLocal> action, [Pooled] Action<TLocal> finalizeLocal)
        {
            TLocal local = default(TLocal);
            try
            {
                if (initializeLocal != null)
                {
                    local = initializeLocal();
                }

                for (int i = fromInclusive; i < toExclusive; i++)
                {
                    action(i, local);
                }
            }
            finally
            {
                finalizeLocal?.Invoke(local);
            }
        }

        private static void Fork(int endExclusive, int batchSize, int maxDegreeOfParallelism, [Pooled] Action<int> action, BatchState state)
        {
            // Kick off another worker if there's any work left
            if (maxDegreeOfParallelism > 1 && state.StartInclusive + batchSize < endExclusive)
            {
                Interlocked.Increment(ref state.ActiveWorkerCount);
                ThreadPool.Instance.QueueWorkItem(() => Fork(endExclusive, batchSize, maxDegreeOfParallelism - 1, action, state));
            }

            try
            {
                // Process batches synchronously as long as there are any
                int newStart;
                while ((newStart = Interlocked.Add(ref state.StartInclusive, batchSize)) - batchSize < endExclusive)
                {
                    ExecuteBatch(newStart - batchSize, Math.Min(endExclusive, newStart), action);
                }
            }
            finally
            {
                // If this was the last batch, signal
                if (Interlocked.Decrement(ref state.ActiveWorkerCount) == 0)
                {
                    state.Finished.Set();
                }
            }
        }

        private static void Fork<TLocal>(int endExclusive, int batchSize, int maxDegreeOfParallelism, [Pooled] Func<TLocal> initializeLocal, [Pooled] Action<int, TLocal> action, [Pooled] Action<TLocal> finalizeLocal, BatchState state)
        {
            // Kick off another worker if there's any work left
            if (maxDegreeOfParallelism > 1 && state.StartInclusive + batchSize < endExclusive)
            {
                Interlocked.Increment(ref state.ActiveWorkerCount);
                ThreadPool.Instance.QueueWorkItem(() => Fork(endExclusive, batchSize, maxDegreeOfParallelism - 1, initializeLocal, action, finalizeLocal, state));
            }

            try
            {
                // Process batches synchronously as long as there are any
                int newStart;
                while ((newStart = Interlocked.Add(ref state.StartInclusive, batchSize)) - batchSize < endExclusive)
                {
                    ExecuteBatch(newStart - batchSize, Math.Min(endExclusive, newStart), initializeLocal, action, finalizeLocal);
                }
            }
            finally
            {
                // If this was the last batch, signal
                if (Interlocked.Decrement(ref state.ActiveWorkerCount) == 0)
                {
                    state.Finished.Set();
                }
            }
        }

        private static void ExecuteBatch<TKey, TValue>(Dictionary<TKey, TValue> dictionary, int offset, int count, [Pooled] Action<KeyValuePair<TKey, TValue>> action)
        {
            var enumerator = dictionary.GetEnumerator();
            int index = 0;

            // Skip to offset
            while (index < offset && enumerator.MoveNext())
            {
                index++;
            }

            // Process batch
            while (index < offset + count && enumerator.MoveNext())
            {
                action(enumerator.Current);
                index++;
            }
        }

        public static void Sort<T>(ConcurrentCollector<T> collection, IComparer<T> comparer)
        {
            Sort(collection.Items, 0, collection.Count, comparer);
        }

        public static void Sort<T>(FastList<T> collection, IComparer<T> comparer)
        {
            Sort(collection.Items, 0, collection.Count, comparer);
        }

        public static void Sort<T>(T[] collection, int index, int length, IComparer<T> comparer)
        {
            if (length <= 0)
                return;

            int depth = 0;
            int degreeOfParallelism = MaxDregreeOfParallelism;

            //while ((degreeOfParallelism >>= 1) != 0)
            //    depth++;

            var state = sortStatePool.Acquire();
            state.ActiveWorkerCount = 1;

            try
            {
                // Initial partition
                state.Partitions.Enqueue(new SortRange(index, length - 1));

                // Sort recursively
                Sort(collection, MaxDregreeOfParallelism, comparer, state);

                // Wait for all work to finish
                state.Finished.WaitOne();
            }
            finally
            {
                sortStatePool.Release(state);
            }
        }

        private static void Sort<T>(T[] collection, int maxDegreeOfParallelism, IComparer<T> comparer, SortState state)
        {
            const int sequentialThreshold = 2048;

            bool hasChild = false;

            try
            {
                SortRange range;
                while (state.Partitions.TryDequeue(out range))
                {
                    if (range.Right - range.Left < sequentialThreshold)
                    {
                        // Sort small collections sequentially
                        Array.Sort(collection, range.Left, range.Right - range.Left + 1, comparer);
                    }
                    else
                    {
                        int pivot = Partition(collection, range.Left, range.Right, comparer);

                        // Add work items
                        if (pivot - 1 > range.Left)
                            state.Partitions.Enqueue(new SortRange(range.Left, pivot - 1));

                        if (range.Right > pivot + 1)
                            state.Partitions.Enqueue(new SortRange(pivot + 1, range.Right));

                        // Add a new worker if necessary
                        if (maxDegreeOfParallelism > 1 && !hasChild)
                        {
                            Interlocked.Increment(ref state.ActiveWorkerCount);
                            Fork(collection, maxDegreeOfParallelism, comparer, state);
                            hasChild = true;
                        }
                    }
                }
            }
            finally
            {
                if (Interlocked.Decrement(ref state.ActiveWorkerCount) == 0)
                {
                    state.Finished.Set();
                }
            }
        }

        private static void Fork<T>(T[] collection, int maxDegreeOfParallelism, IComparer<T> comparer, SortState state)
        {
            ThreadPool.Instance.QueueWorkItem(() => Sort(collection, maxDegreeOfParallelism - 1, comparer, state));
        }

        private static int Partition<T>(T[] collection, int left, int right, IComparer<T> comparer)
        {
            int i = left, j = right;
            int mid = (left + right) / 2;

            if (comparer.Compare(collection[right], collection[left]) < 0)
                Swap(collection, left, right);
            if (comparer.Compare(collection[mid], collection[left]) < 0)
                Swap(collection, left, mid);
            if (comparer.Compare(collection[right], collection[mid]) < 0)
                Swap(collection, mid, right);

            while (i <= j)
            {
                var pivot = collection[mid];

                while (comparer.Compare(collection[i], pivot) < 0)
                {
                    i++;
                }

                while (comparer.Compare(collection[j], pivot) > 0)
                {
                    j--;
                }

                if (i <= j)
                {
                    Swap(collection, i++, j--);
                }
            }

            return mid;
        }

        private static void Swap<T>(T[] collection, int i, int j)
        {
            var temp = collection[i];
            collection[i] = collection[j];
            collection[j] = temp;
        }

        private class BatchState
        {
            public readonly AutoResetEvent Finished = new AutoResetEvent(false);

            public int StartInclusive;

            public int ActiveWorkerCount;
        }

        private struct SortRange
        {
            public readonly int Left;

            public readonly int Right;

            public SortRange(int left, int right)
            {
                Left = left;
                Right = right;
            }
        }

        private class SortState
        {
            public readonly AutoResetEvent Finished = new AutoResetEvent(false);

            public readonly ConcurrentQueue<SortRange> Partitions = new ConcurrentQueue<SortRange>();

            public int ActiveWorkerCount;
        }

        private class DispatcherNode
        {
            public MethodBase Caller;
            public int Count;
            public TimeSpan TotalTime;
        }

        private static ConcurrentDictionary<MethodInfo, DispatcherNode> nodes = new ConcurrentDictionary<MethodInfo, DispatcherNode>();

        private struct ProfilingScope : IDisposable
        {
#if false
            public Stopwatch Stopwatch;
            public Delegate Action;
#endif
            public void Dispose()
            {
#if false
                Stopwatch.Stop();
                var elapsed = Stopwatch.Elapsed;

                DispatcherNode node;
                if (!nodes.TryGetValue(Action.Method, out node))
                {
                    int skipFrames = 1;
                    MethodBase caller = null;

                    do
                    {
                        caller = new StackFrame(skipFrames++, true).GetMethod();
                    }
                    while (caller.DeclaringType == typeof(Dispatcher));
                    
                    node = nodes.GetOrAdd(Action.Method, key => new DispatcherNode());
                    node.Caller = caller;
                }

                node.Count++;
                node.TotalTime += elapsed;

                if (node.Count % 500 == 0)
                {
                    Console.WriteLine($"[{node.Count}] {node.Caller.DeclaringType.Name}.{node.Caller.Name}: {node.TotalTime.TotalMilliseconds / node.Count}");
                }
#endif
            }
        }

        private static ProfilingScope Profile(Delegate action)
        {
            var result = new ProfilingScope();
#if false
            result.Action = action;
            result.Stopwatch = new Stopwatch();
            result.Stopwatch.Start();
#endif
            return result;

        }
    }
}
