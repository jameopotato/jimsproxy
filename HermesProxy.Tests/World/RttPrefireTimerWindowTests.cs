using System;
using HermesProxy;
using Xunit;

namespace HermesProxy.Tests.World;

// JimsProxy (rtt-prefire): admission-width tests for the shared GCD hold gate
// (IsInGcdQueueWindow). Queue mode reads the configurable SpellQueueWindowMs; under
// RTT Pre-Fire Timer the width is the fixed Settings.RttPrefireTimerWindowMs so the
// launcher's greyed-out spell-queue dropdown can't silently govern Timer holds.
// Remaining-time margins are hundreds of ms away from each boundary, far above
// timer-tick coarseness, so the immediate re-read cannot flake.
public class RttPrefireTimerWindowTests
{
    private static GameSessionData NewSessionWithGcdRemaining(long remainingMs)
    {
        var session = GameSessionData.CreateForTesting();
        long now = Environment.TickCount64;
        session.BeginGcd(now + remainingMs, now + remainingMs);
        return session;
    }

    private static (bool lowLatency, global::Framework.RttPrefireMode prefire, int queueWindow) SaveSettings()
        => (global::Framework.Settings.LowLatencyMode,
            global::Framework.Settings.RttPrefire,
            global::Framework.Settings.SpellQueueWindowMs);

    private static void RestoreSettings((bool lowLatency, global::Framework.RttPrefireMode prefire, int queueWindow) saved)
    {
        global::Framework.Settings.LowLatencyMode = saved.lowLatency;
        global::Framework.Settings.RttPrefire = saved.prefire;
        global::Framework.Settings.SpellQueueWindowMs = saved.queueWindow;
    }

    [Fact]
    public void IsInGcdQueueWindow_TimerActive_IgnoresConfiguredQueueWindow()
    {
        var saved = SaveSettings();
        try
        {
            global::Framework.Settings.LowLatencyMode = true;
            global::Framework.Settings.RttPrefire = global::Framework.RttPrefireMode.Timer;
            global::Framework.Settings.SpellQueueWindowMs = 1300;

            // 1200 ms out is inside the configured 1300 but outside Timer's fixed 400:
            // the stored dropdown value must not widen Timer's admission.
            var session = NewSessionWithGcdRemaining(1200);
            Assert.False(session.IsInGcdQueueWindow());
            session.CancelGcdHold();
        }
        finally { RestoreSettings(saved); }
    }

    [Fact]
    public void IsInGcdQueueWindow_TimerActive_AdmitsInsideFixedWindow()
    {
        var saved = SaveSettings();
        try
        {
            global::Framework.Settings.LowLatencyMode = true;
            global::Framework.Settings.RttPrefire = global::Framework.RttPrefireMode.Timer;
            // Deliberately hostile config: even 0 must not disable Timer's fixed window.
            global::Framework.Settings.SpellQueueWindowMs = 0;

            var session = NewSessionWithGcdRemaining(250);
            Assert.True(session.IsInGcdQueueWindow());
            session.CancelGcdHold();
        }
        finally { RestoreSettings(saved); }
    }

    [Fact]
    public void IsInGcdQueueWindow_QueueMode_UsesConfiguredWindow()
    {
        var saved = SaveSettings();
        try
        {
            global::Framework.Settings.LowLatencyMode = false;
            global::Framework.Settings.RttPrefire = global::Framework.RttPrefireMode.Off;
            global::Framework.Settings.SpellQueueWindowMs = 1300;

            var wide = NewSessionWithGcdRemaining(1200);
            Assert.True(wide.IsInGcdQueueWindow());
            wide.CancelGcdHold();

            global::Framework.Settings.SpellQueueWindowMs = 400;
            var narrow = NewSessionWithGcdRemaining(1200);
            Assert.False(narrow.IsInGcdQueueWindow());
            narrow.CancelGcdHold();
        }
        finally { RestoreSettings(saved); }
    }
}
