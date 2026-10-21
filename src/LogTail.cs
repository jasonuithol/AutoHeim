using System.Collections.Generic;
using BepInEx.Logging;

namespace AutoHeim
{
    /// <summary>
    /// Keeps the most recent BepInEx log lines (which include Unity's own log)
    /// in memory so the agent can read them through the API with an
    /// incrementing cursor instead of tailing files.
    /// </summary>
    internal sealed class LogTail : ILogListener
    {
        private const int Capacity = 2000;
        private static readonly LinkedList<(long seq, string line)> Lines = new LinkedList<(long, string)>();
        private static long _seq;
        private static readonly object Lock = new object();

        internal static void Install() => Logger.Listeners.Add(new LogTail());

        public void LogEvent(object sender, LogEventArgs e)
        {
            string text = $"[{e.Level}:{e.Source.SourceName}] {e.Data}".TrimEnd();
            lock (Lock)
            {
                Lines.AddLast((++_seq, text));
                while (Lines.Count > Capacity) Lines.RemoveFirst();
            }
        }

        public void Dispose() { }

        /// Lines with sequence number greater than <paramref name="after"/>, newest last.
        internal static (long last, List<string> lines) Since(long after, int max, string contains)
        {
            var result = new List<string>();
            long last = after;
            lock (Lock)
            {
                foreach (var (seq, line) in Lines)
                {
                    if (seq <= after) continue;
                    last = seq;
                    if (contains != null && line.IndexOf(contains, System.StringComparison.OrdinalIgnoreCase) < 0) continue;
                    result.Add(line);
                }
                if (last < _seq) last = _seq;
            }
            if (result.Count > max) result = result.GetRange(result.Count - max, max);
            return (last, result);
        }
    }
}
