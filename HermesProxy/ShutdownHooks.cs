using System;
using System.Runtime.InteropServices;
using System.Threading;
using Framework.Logging;

namespace HermesProxy;

// JimsProxy: catches every termination path the OS gives us a chance on, emits a
// final structured event, and flushes the JSONL writer before the runtime tears
// down. Without this the log file is truncated mid-stream on console-close, Ctrl+C,
// or unhandled exceptions, making post-mortem diagnosis impossible.
internal static class ShutdownHooks
{
    private static int _installed;
    private static int _finalEventEmitted;

    // Rooted to keep the unmanaged callback alive for the lifetime of the process.
    private static ConsoleCtrlDelegate? _consoleCtrlHandler;

    public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) != 0)
            return;

        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        Console.CancelKeyPress += OnCancelKeyPress;

        if (OperatingSystem.IsWindows())
        {
            _consoleCtrlHandler = OnConsoleCtrl;
            SetConsoleCtrlHandler(_consoleCtrlHandler, add: true);
        }
        else
        {
            // Linux/macOS: SIGTERM (kill, container stop) and SIGHUP (terminal closed).
            // SIGINT is already covered by Console.CancelKeyPress.
            PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => OnPosixSignal(ctx, "sigterm"));
            PosixSignalRegistration.Create(PosixSignal.SIGHUP, ctx => OnPosixSignal(ctx, "sighup"));
        }
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception;
        EmitFinalEvent("process.crash", new
        {
            is_terminating = e.IsTerminating,
            exception_type = ex?.GetType().FullName,
            message = ex?.Message,
            stack = ex?.ToString(),
        });
    }

    private static void OnProcessExit(object? sender, EventArgs e)
    {
        // Fires for normal exits and after UnhandledException. Idempotent — if the
        // crash hook already emitted, we just flush again to be sure.
        EmitFinalEvent("process.exit", new { reason = "processexit" });
    }

    private static void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        // Don't let the runtime kill us before we flush. We re-exit ourselves below
        // so ProcessExit still fires and the parent shell sees a normal exit code.
        e.Cancel = true;
        EmitFinalEvent("process.signal", new
        {
            reason = e.SpecialKey == ConsoleSpecialKey.ControlBreak ? "ctrl_break" : "ctrl_c",
        });
        // Give the rest of the runtime a beat to settle, then exit cleanly.
        Environment.Exit(0);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool OnConsoleCtrl(uint ctrlType)
    {
        // Windows gives us ~5s on CTRL_CLOSE_EVENT and up to ~20s on logoff/shutdown
        // before SIGKILL-equivalent. Plenty of time to flush, but don't block here
        // beyond what we need — the OS is waiting on us.
        string reason = ctrlType switch
        {
            CTRL_C_EVENT => "ctrl_c",
            CTRL_BREAK_EVENT => "ctrl_break",
            CTRL_CLOSE_EVENT => "console_close",
            CTRL_LOGOFF_EVENT => "logoff",
            CTRL_SHUTDOWN_EVENT => "shutdown",
            _ => $"ctrl_{ctrlType}",
        };

        EmitFinalEvent("process.signal", new { reason, ctrl_type = ctrlType });

        // For CTRL_C / CTRL_BREAK, return false so .NET's CancelKeyPress chain still
        // runs (it'll re-emit, but EmitFinalEvent is idempotent). For close/logoff/
        // shutdown, return true and let the OS terminate us — there's no recovery
        // from those anyway.
        return ctrlType is CTRL_CLOSE_EVENT or CTRL_LOGOFF_EVENT or CTRL_SHUTDOWN_EVENT;
    }

    private static void OnPosixSignal(PosixSignalContext ctx, string reason)
    {
        ctx.Cancel = true; // Suppress runtime default action (which is to kill us).
        EmitFinalEvent("process.signal", new { reason });
        Environment.Exit(0);
    }

    private static void EmitFinalEvent(string eventType, object payload)
    {
        if (Interlocked.Exchange(ref _finalEventEmitted, 1) != 0)
        {
            // Already emitted — just make sure the file is flushed.
            Log.FlushAndCloseStructuredLog();
            return;
        }

        try
        {
            Log.Event(eventType, payload);
        }
        catch
        {
            // Never let logging throw out of a shutdown hook.
        }
        finally
        {
            Log.FlushAndCloseStructuredLog();
        }
    }

    private const uint CTRL_C_EVENT = 0;
    private const uint CTRL_BREAK_EVENT = 1;
    private const uint CTRL_CLOSE_EVENT = 2;
    private const uint CTRL_LOGOFF_EVENT = 5;
    private const uint CTRL_SHUTDOWN_EVENT = 6;

    private delegate bool ConsoleCtrlDelegate(uint ctrlType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleCtrlHandler(ConsoleCtrlDelegate? handler, [MarshalAs(UnmanagedType.Bool)] bool add);
}
