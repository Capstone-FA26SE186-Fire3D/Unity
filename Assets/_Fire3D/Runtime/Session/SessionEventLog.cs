using System;
using System.Collections.Generic;
using Fire3D.Contracts;
using Newtonsoft.Json.Linq;

namespace Fire3D.Unity.Session
{
    /// <summary>
    /// Ordered telemetry for one session. Sequence starts after the backend's acknowledged sequence on resume so
    /// replays never reuse a number. Unity only records; the backend scores.
    /// </summary>
    public sealed class SessionEventLog
    {
        private readonly List<SessionEvent> _all = new();
        private readonly Queue<SessionEvent> _unsent = new();
        private readonly Func<double> _elapsedSeconds;
        private long _sequence;

        public SessionEventLog(Func<double> elapsedSeconds, long resumeFromSequence = 0)
        {
            _elapsedSeconds = elapsedSeconds;
            _sequence = Math.Max(0, resumeFromSequence);
        }

        public event Action<SessionEvent> Recorded;

        public long LastSequence => _sequence;
        public IReadOnlyList<SessionEvent> All => _all;
        public int UnsentCount => _unsent.Count;
        public bool Closed { get; private set; }

        public SessionEvent Emit(string type, JObject payload = null)
        {
            if (Closed)
                return null;
            var e = new SessionEvent
            {
                EventId = Guid.NewGuid().ToString("D"),
                Sequence = ++_sequence,
                Type = type,
                OccurredAt = ContractJson.UtcNow(),
                ElapsedMs = (long)Math.Round(Math.Max(0, _elapsedSeconds()) * 1000.0),
                Payload = payload ?? new JObject(),
            };
            _all.Add(e);
            _unsent.Enqueue(e);
            Recorded?.Invoke(e);
            return e;
        }

        public SessionEvent EmitObject(string type, string objectId) => Emit(type, new JObject { ["objectId"] = objectId });

        public int Count(string type)
        {
            var n = 0;
            foreach (var e in _all)
                if (e.Type == type)
                    n++;
            return n;
        }

        /// <summary>Takes up to <paramref name="max"/> unsent events (backend accepts 1–500 per batch).</summary>
        public List<SessionEvent> TakeBatch(int max = 500)
        {
            var batch = new List<SessionEvent>(Math.Min(max, _unsent.Count));
            while (_unsent.Count > 0 && batch.Count < max)
                batch.Add(_unsent.Dequeue());
            return batch;
        }

        /// <summary>No events after completion: the backend rejects sequences past the pinned completion.</summary>
        public void Close() => Closed = true;
    }
}
