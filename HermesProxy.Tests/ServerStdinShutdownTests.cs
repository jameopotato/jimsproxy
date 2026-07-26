using System;
using System.Collections.Generic;
using System.IO;
using HermesProxy;
using Xunit;

namespace HermesProxy.Tests;

// JimsProxy: contract tests for the launcher stdin shutdown handshake. The wire tokens are
// hardcoded literals on purpose — they are a cross-repo contract with the launcher's
// HermesManager::stop (sentinel in on stdin, ack out on stdout); renaming either side
// must break these tests.
public class ServerStdinShutdownTests
{
    [Fact]
    public void SentinelLine_AcksOnStdoutBeforeExitAndStops()
    {
        var stdout = new StringWriter();
        var exitReasons = new List<string>();
        string? stdoutAtExitTime = null;

        Server.ListenForLauncherShutdown(
            new StringReader("__LAUNCHER_SHUTDOWN__\n"),
            stdout,
            reason => { exitReasons.Add(reason); stdoutAtExitTime = stdout.ToString(); });

        Assert.Equal(new[] { "stdin_launcher_shutdown" }, exitReasons);
        // The ack must already be on stdout when the exit path starts — that ordering is
        // what upgrades the launcher's wait from the 250 ms silent-grace to the full
        // 3 s window before its taskkill fallback.
        Assert.Contains("__PROXY_SHUTDOWN_ACK__", stdoutAtExitTime);
        // And it must be a full line of its own, not a fragment.
        Assert.Contains("__PROXY_SHUTDOWN_ACK__" + stdout.NewLine, stdout.ToString());
    }

    [Fact]
    public void SentinelSurroundedByWhitespace_StillAcksAndExits()
    {
        var stdout = new StringWriter();
        int exits = 0;

        Server.ListenForLauncherShutdown(
            new StringReader("   __LAUNCHER_SHUTDOWN__  \n"), stdout, _ => exits++);

        Assert.Equal(1, exits);
        Assert.Contains("__PROXY_SHUTDOWN_ACK__", stdout.ToString());
    }

    [Fact]
    public void NonSentinelLines_AreIgnoredToEof_NoAckNoExit()
    {
        var stdout = new StringWriter();
        int exits = 0;

        // Includes a near-miss token (missing trailing underscores) — must not trigger.
        Server.ListenForLauncherShutdown(
            new StringReader("hello\n__LAUNCHER_SHUTDOWN\nnoise\n"), stdout, _ => exits++);

        Assert.Equal(0, exits);
        Assert.Equal(string.Empty, stdout.ToString());
    }
}
