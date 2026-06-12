using System;
using System.Collections.Generic;
using System.Linq;

namespace AsetLauncher.Services
{
    public static class LauncherLogService
    {
        private const int MaxEntries = 5000;
        private static readonly object Sync = new object();
        private static readonly List<string> Entries = new List<string>(1024);

        public static event Action<string> LineAppended;
        public static event Action LogsCleared;

        public static void Info(string message)
        {
            Write("INFO", message);
        }

        public static void Warn(string message)
        {
            Write("WARN", message);
        }

        public static void Warning(string message)
        {
            Warn(message);
        }

        public static void Error(string message)
        {
            Write("ERROR", message);
        }

        public static void Exception(string context, Exception ex)
        {
            if (ex == null)
            {
                Error(context);
                return;
            }

            Error(context + " :: " + ex);
        }

        public static void Write(string scope, string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            var line = $"{DateTime.Now:HH:mm:ss.fff} [{scope}] {message}";

            lock (Sync)
            {
                Entries.Add(line);
                if (Entries.Count > MaxEntries)
                {
                    Entries.RemoveRange(0, Entries.Count - MaxEntries);
                }
            }

            LineAppended?.Invoke(line);
        }

        public static string GetSnapshotText()
        {
            lock (Sync)
            {
                return string.Join(Environment.NewLine, Entries);
            }
        }

        public static string[] GetSnapshotLines()
        {
            lock (Sync)
            {
                return Entries.ToArray();
            }
        }

        public static void Clear()
        {
            lock (Sync)
            {
                Entries.Clear();
            }

            LogsCleared?.Invoke();
        }
    }
}
