using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;

// Diagnostic-only tracer for the touch -> sensor -> serial pipeline.
// Separate from Logger on purpose: Logger writes synchronously (open/lock/write/close
// per call) with second-resolution timestamps, which is fine for status messages but
// both too coarse and too slow to use on the input hot path. This queues a monotonic,
// sub-millisecond-resolution line and returns immediately; a background thread does the
// actual file I/O, so enabling tracing doesn't itself introduce the kind of delay we're
// trying to diagnose.
//
// Wired to the existing "debug mode" toggle (TouchPanel.SetDebugMode) — turn debug mode
// on, reproduce one bad hit, turn it back off, then read the newest trace_*.log in
// %LocalAppData%\WpfMaiTouchEmulator.
public static class InputTracer
{
    public static volatile bool Enabled = false;

    private static readonly ConcurrentQueue<string> _queue = new();
    private static readonly AutoResetEvent _signal = new(false);
    private static readonly Stopwatch _clock = Stopwatch.StartNew();
    private static Thread? _writerThread;
    private static string? _path;
    private static readonly object _startLock = new();

    public static void Start()
    {
        if (_writerThread != null) return;
        lock (_startLock)
        {
            if (_writerThread != null) return;
            _path = Path.Combine(Logger.GetLogPath(), $"trace_{DateTime.Now:yyyy-MM-dd_HHmmss}.log");
            _writerThread = new Thread(WriterLoop) { IsBackground = true, Priority = ThreadPriority.BelowNormal };
            _writerThread.Start();
        }
    }

    // category examples: "POINTER" (raw WM_POINTER), "SENSOR" (mask press/release edges),
    // "KEY" (RingButtonEmulator keybd_event), "SEND" (serial writes / queue latency)
    public static void Event(string category, string message)
    {
        if (!Enabled) return;
        var ms = _clock.Elapsed.TotalMilliseconds;
        _queue.Enqueue($"{ms:F3} [{category}] {message}");
        _signal.Set();
    }

    private static void WriterLoop()
    {
        while (true)
        {
            _signal.WaitOne(100);
            if (_queue.IsEmpty) continue;
            try
            {
                using var sw = new StreamWriter(_path!, true, Encoding.UTF8);
                while (_queue.TryDequeue(out var line))
                {
                    sw.WriteLine(line);
                }
            }
            catch
            {
            }
        }
    }
}