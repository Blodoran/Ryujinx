using ARMeilleure.Diagnostics.EventSources;
using ARMeilleure.State;
using System;
using System.Collections.Generic;
using System.Threading;

namespace ARMeilleure.Translation
{
    /// <summary>
    /// Represents a queue of <see cref="RejitRequest"/>.
    /// </summary>
    /// <remarks>
    /// This does not necessarily behave like a queue, i.e: a FIFO collection.
    /// </remarks>
    sealed class TranslatorQueue : IDisposable
    {
        private bool _disposed;
        private readonly Stack<RejitRequest> _requests;
        private readonly HashSet<ulong> _requestAddresses;
        private readonly CountdownEvent _threadCountdownEvent;
        private readonly ManualResetEventSlim _notEmptyEvent;

        /// <summary>
        /// Gets the object used to synchronize access to the <see cref="TranslatorQueue"/>.
        /// </summary>
        public object Sync { get; }

        /// <summary>
        /// Gets the number of requests in the <see cref="TranslatorQueue"/>.
        /// </summary>
        public int Count => _requests.Count;

        /// <summary>
        /// Initializes a new instance of the <see cref="TranslatorQueue"/> class.
        /// </summary>
        public TranslatorQueue()
        {
            Sync = new object();

            _requests = new Stack<RejitRequest>();
            _requestAddresses = new HashSet<ulong>();
            _notEmptyEvent = new ManualResetEventSlim(false);

            // This event counts the number of threads in `TryDequeue`. By making the initial count 1, we make it
            // impossible for the event to be set unless we call an extra `Signal()` on it (because we cannot have -1
            // thread).
            _threadCountdownEvent = new CountdownEvent(1);
        }

        /// <summary>
        /// Enqueues a request with the specified <paramref name="address"/> and <paramref name="mode"/>.
        /// </summary>
        /// <param name="address">Address of request</param>
        /// <param name="mode"><see cref="ExecutionMode"/> of request</param>
        public void Enqueue(ulong address, ExecutionMode mode)
        {
            lock (Sync)
            {
                if (_requestAddresses.Add(address))
                {
                    _requests.Push(new RejitRequest(address, mode));
                    _notEmptyEvent.Set();

                    TranslatorEventSource.Log.RejitQueueAdd(1);
                }
            }
        }

        /// <summary>
        /// Tries to dequeue a <see cref="RejitRequest"/>. This will block the thread until a <see cref="RejitRequest"/>
        /// is enqueued or the <see cref="TranslatorQueue"/> is disposed.
        /// </summary>
        /// <param name="result"><see cref="RejitRequest"/> dequeued</param>
        /// <returns><see langword="true"/> on success; otherwise <see langword="false"/></returns>
        public bool TryDequeue(out RejitRequest result)
        {
            _threadCountdownEvent.AddCount();

            while (!_disposed)
            {
                _notEmptyEvent.Wait();

                lock (Sync)
                {
                    if (_requests.TryPop(out result))
                    {
                        _requestAddresses.Remove(result.Address);

                        TranslatorEventSource.Log.RejitQueueAdd(-1);

                        _threadCountdownEvent.Signal();

                        return true;
                    }
                    else if (!_disposed)
                    {
                        _notEmptyEvent.Reset();
                    }
                }
            }

            result = default;

            _threadCountdownEvent.Signal();

            return false;
        }

        /// <summary>
        /// Clears the <see cref="TranslatorQueue"/>.
        /// </summary>
        public void Clear()
        {
            lock (Sync)
            {
                TranslatorEventSource.Log.RejitQueueAdd(-_requests.Count);

                _requests.Clear();
                _requestAddresses.Clear();
            }
        }

        /// <summary>
        /// Releases all resources used by the <see cref="TranslatorQueue"/> instance.
        /// </summary>
        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;

                lock (Sync)
                {
                    Clear();

                    // Unblock threads and allow them to exit `TryDequeue`. This is inside the lock to ensure that a
                    // reset on the event does not happen after this.
                    _notEmptyEvent.Set();
                }

                // Wait for all threads to exit `TryDequeue` before disposing the event.
                _threadCountdownEvent.Signal();
                _threadCountdownEvent.Wait();

                _threadCountdownEvent.Dispose();
                _notEmptyEvent.Dispose();
            }
        }
    }
}
