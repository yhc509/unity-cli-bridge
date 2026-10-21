using System;
using System.Collections.Generic;
using System.Linq;
using UnityCli.Protocol;
using UnityEngine;

namespace UnityCliBridge.Bridge.Editor
{
    internal static class ConsoleLogBuffer
    {
        private static readonly object _sync = new();
        private static readonly Queue<ConsoleLogEntry> _entries = new();
        private const int MaxEntries = 512;

        public static void Start()
        {
            Application.logMessageReceivedThreaded -= OnLogReceived;
            Application.logMessageReceivedThreaded += OnLogReceived;
        }

        public static void Stop()
        {
            Application.logMessageReceivedThreaded -= OnLogReceived;
            lock (_sync)
            {
                _entries.Clear();
            }
        }

        public static ConsoleLogEntry[] Read(int limit, string type)
        {
            lock (_sync)
            {
                IEnumerable<ConsoleLogEntry> query = _entries;
                if (!string.IsNullOrWhiteSpace(type))
                {
                    query = query.Where(entry => ConsoleLogProjectionUtility.MatchesType(entry.type, type));
                }

                return query.TakeLast(Math.Max(1, limit)).ToArray();
            }
        }

        private static void OnLogReceived(string condition, string stackTrace, LogType type)
        {
            lock (_sync)
            {
                _entries.Enqueue(new ConsoleLogEntry
                {
                    timestampUtc = DateTimeOffset.UtcNow.ToString("O"),
                    type = type.ToString(),
                    message = condition,
                    stackTrace = stackTrace,
                });

                while (_entries.Count > MaxEntries)
                {
                    _entries.Dequeue();
                }
            }
        }
    }

}
