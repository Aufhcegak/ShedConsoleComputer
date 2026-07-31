using System.Diagnostics;
using StardewModdingAPI;

namespace ShedConsoleComputer
{
    /// <summary>极简耗时探针：超过阈值才写日志，避免刷屏。</summary>
    internal static class Perf
    {
        public static void Measure(IMonitor monitor, string label, double thresholdMs, System.Action action)
        {
            Stopwatch sw = Stopwatch.StartNew();
            try
            {
                action();
            }
            finally
            {
                sw.Stop();
                if (sw.Elapsed.TotalMilliseconds >= thresholdMs)
                    monitor.Log($"[PERF] {label} 耗时 {sw.Elapsed.TotalMilliseconds:F1}ms", LogLevel.Warn);
            }
        }
    }
}
