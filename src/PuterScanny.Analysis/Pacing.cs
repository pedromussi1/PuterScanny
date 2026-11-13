using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PuterScanny.Analysis;

/// <summary>
/// Spaces out work evenly so no more than a set number of files are opened per second, without letting
/// a backlog build up into a burst (a slow file doesn't earn the next ones a free sprint).
/// </summary>
public sealed class RateLimiter(int perSecond)
{
    private readonly object _lock = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly double _interval = perSecond > 0 ? 1.0 / perSecond : 0;
    private double _next;

    public void Wait(CancellationToken ct)
    {
        if (_interval == 0) return;
        double due;
        lock (_lock)
        {
            double now = _clock.Elapsed.TotalSeconds;
            due = Math.Max(_next, now);
            _next = due + _interval;
        }
        double wait = due - _clock.Elapsed.TotalSeconds;
        if (wait > 0.001) ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(wait));
        ct.ThrowIfCancellationRequested();
    }
}

/// <summary>Windows "background mode" for a worker thread: very low disk and memory priority, so other programs (and antivirus) always come first.</summary>
internal static class BackgroundThread
{
    private const int THREAD_MODE_BACKGROUND_BEGIN = 0x00010000;
    private const int THREAD_MODE_BACKGROUND_END = 0x00020000;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadPriority(IntPtr hThread, int nPriority);

    public static bool Enter() => SetThreadPriority(GetCurrentThread(), THREAD_MODE_BACKGROUND_BEGIN);

    public static void Leave() => SetThreadPriority(GetCurrentThread(), THREAD_MODE_BACKGROUND_END);
}
