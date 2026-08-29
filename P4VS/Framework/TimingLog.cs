using System;
using System.Diagnostics;
using System.IO;

namespace Perforce.P4VS
{
    /// <summary>
    /// Ultra-light timestamped profiler for the connect/startup path.
    /// Writes to C:\Windows\Temp\p4vs_timing.txt (always on unless P4VS_TIMING=0).
    /// Never throws.
    /// </summary>
    internal static class TimingLog
    {
        private static readonly object Gate = new object();
        private const string LogPath = @"C:\Windows\Temp\p4vs_timing.txt";

        public static bool Enabled
        {
            get { return Environment.GetEnvironmentVariable("P4VS_TIMING") != "0"; }
        }

        public static void Mark(string message)
        {
            if (!Enabled)
            {
                return;
            }

            try
            {
                string line = string.Format("{0:HH:mm:ss.fff} [pid {1}] {2}\r\n",
                    DateTime.Now, Process.GetCurrentProcess().Id, message);
                lock (Gate)
                {
                    File.AppendAllText(LogPath, line);
                }
            }
            catch
            {
                // never fail because of logging
            }
        }

        public static IDisposable Scope(string name)
        {
            return new ScopeImpl(name);
        }

        private sealed class ScopeImpl : IDisposable
        {
            private readonly string _name;
            private readonly Stopwatch _sw;

            public ScopeImpl(string name)
            {
                _name = name;
                Mark("BEGIN " + name);
                _sw = Stopwatch.StartNew();
            }

            public void Dispose()
            {
                _sw.Stop();
                Mark(string.Format("END   {0}  {1} ms", _name, _sw.ElapsedMilliseconds));
            }
        }
    }
}
