// This file is subject to the terms and conditions defined
// in file 'LICENSE', which is part of this source code package.

using System;
using System.Threading;
using Spectre.Console;

namespace DepotDownloader;

// Renders a single sticky progress bar + transfer speed on the last printed
// line while a download is active. This intentionally knows nothing about
// chunks, files, or MaxDownloads - it just periodically samples whatever
// (downloaded, total) snapshot it's given, the same way Ansi.Progress()
// (the Windows taskbar indicator) already does from the same counters.
//
// All console output that may happen while a download is active should go
// through WriteLine() instead of Console.WriteLine() directly, so it scrolls
// above the bar instead of being overwritten by it. When no download is
// active (or the terminal doesn't support ANSI), WriteLine() is a transparent
// passthrough to Console.WriteLine().
static class ProgressDisplay
{
    private static readonly object sync = new();

    private static Func<(ulong downloaded, ulong total)> getProgress;
    private static Timer timer;

    private static bool enabled;
    private static bool active;

    private static long lastSampleTimestamp;
    private static ulong lastSampleBytes;
    private static double smoothedBytesPerSecond;

    private static string lastBarText = "";

    private const int RedrawIntervalMs = 150;
    private const double SmoothingFactor = 0.25; // weight given to each new speed sample
    private const int BarWidth = 28;

    // Begin showing the sticky bar. getProgress is called periodically (off
    // the calling thread) and should return a consistent (downloaded, total)
    // snapshot - take whatever lock you already use around those counters.
    public static void Start(Func<(ulong downloaded, ulong total)> progressSnapshot)
    {
        getProgress = progressSnapshot;

        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            enabled = false;
            return;
        }

        var (supportsAnsi, legacyConsole) = AnsiDetector.Detect(stdError: false, upgrade: true);
        enabled = supportsAnsi && !legacyConsole;

        if (!enabled)
        {
            return;
        }

        lock (sync)
        {
            active = true;
            lastSampleTimestamp = Environment.TickCount64;
            lastSampleBytes = 0;
            smoothedBytesPerSecond = 0;
            lastBarText = "";

            Console.Write("\x1B[?25l"); // hide cursor
            Redraw();
        }

        timer = new Timer(_ => Tick(), null, RedrawIntervalMs, RedrawIntervalMs);
    }

    // Stop and clean up. Safe to call even if Start() was never called or the
    // bar was never enabled. Always call this from a finally block.
    public static void Stop()
    {
        if (!enabled)
        {
            return;
        }

        if (timer != null)
        {
            using var stopped = new ManualResetEvent(false);
            timer.Dispose(stopped);
            stopped.WaitOne();
            timer = null;
        }

        lock (sync)
        {
            if (active)
            {
                Console.Write("\r\x1B[2K"); // clear the bar line
                Console.Write("\x1B[?25h"); // show cursor
                active = false;
            }
        }

        enabled = false;
    }

    public static void WriteLine()
    {
        WriteLine(string.Empty);
    }

    public static void WriteLine(string message)
    {
        if (!enabled || !active)
        {
            Console.WriteLine(message);
            return;
        }

        lock (sync)
        {
            Console.Write("\r\x1B[2K");   // clear the bar line
            Console.WriteLine(message);    // real log line takes its place
            Console.Write(lastBarText);    // redraw the bar on the new last line
        }
    }

    public static void WriteLine(string format, params object[] args)
    {
        WriteLine(string.Format(format, args));
    }

    private static void Tick()
    {
        lock (sync)
        {
            if (!active)
            {
                return;
            }

            Redraw();
        }
    }

    // Caller must hold sync.
    private static void Redraw()
    {
        var (downloaded, total) = getProgress();

        var now = Environment.TickCount64;
        var elapsedMs = now - lastSampleTimestamp;

        if (elapsedMs > 0)
        {
            var deltaBytes = downloaded > lastSampleBytes ? downloaded - lastSampleBytes : 0UL;
            var instantBytesPerSecond = deltaBytes / (elapsedMs / 1000.0);

            smoothedBytesPerSecond = smoothedBytesPerSecond <= 0
                ? instantBytesPerSecond
                : (SmoothingFactor * instantBytesPerSecond) + ((1 - SmoothingFactor) * smoothedBytesPerSecond);

            lastSampleBytes = downloaded;
            lastSampleTimestamp = now;
        }

        var percent = total > 0 ? Math.Clamp(downloaded / (float)total * 100f, 0f, 100f) : 100f;
        var filled = (int)(percent / 100f * BarWidth);

        var bar = "[" + new string('#', filled) + new string('-', BarWidth - filled) + "]";
        var text = $"{bar} {percent,6:0.00}%  {FormatBytes(downloaded)}/{FormatBytes(total)}  {FormatBytes((ulong)smoothedBytesPerSecond)}/s";

        lastBarText = text;
        Console.Write("\r\x1B[2K" + text);
    }

    private static string FormatBytes(ulong bytes)
    {
        ReadOnlySpan<string> units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        var unit = 0;

        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size,6:0.0} {units[unit]}";
    }
}
