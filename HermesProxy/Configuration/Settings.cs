using HermesProxy.Enums;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net;
using Framework.Logging;
using Framework.Networking;
using HermesProxy;
using HermesProxy.Configuration;

namespace Framework;

public enum ServerFork { Kronos, Generic }

public static class Settings
{
    public static byte[] ClientSeed = null!;
    public static ClientVersionBuild ClientBuild;
    public static ClientVersionBuild ServerBuild;
    public static string ServerAddress = null!;
    public static int ServerPort;
    public static string ReportedOS = null!;
    public static string ReportedPlatform = null!;
    public static string ExternalAddress = null!;
    public static int RestPort;
    public static int BNetPort;
    public static int RealmPort;
    public static int InstancePort;
    public static bool DebugOutput;
    public static bool PacketsLog;
    public static bool SpanStatsLog;
    // JimsProxy: structured JSONL diagnostic logging
    public static bool StructuredLog;
    public static bool VerboseLog;
    // JimsProxy (issue #43): how many ms before the local GCD expiry estimate the proxy
    // releases a held cast. 0 = fire exactly at expiry (cast lands ~RTT late at server).
    // Clamped to 0..50 in LoadAndVerifyFrom.
    public static int SpellCastEarlyFireOffsetMs;
    // JimsProxy (unplanned-dc-auto-reconnect): when the legacy world server forcibly
    // closes the proxy's TCP socket mid-session (anticheat, server crash, transient
    // network reset), attempt one cached-session-key reconnect before giving up. If
    // false, fall straight through to clean DC propagation (close modern InstanceSocket
    // so the user sees "Disconnected" within a second instead of being stuck in a
    // ghost world for tens of seconds).
    public static bool EnableUnplannedReconnect;
    // Hard timeout on the reconnect attempt — beyond this, abandon and propagate DC.
    // Clamped to 1000..30000 in LoadAndVerifyFrom.
    public static int UnplannedReconnectTimeoutMs;
    // JimsProxy (cross-version addon interop): translate PallyPower ASSIGN class
    // index between 1.12 (0-indexed) and 1.14 (1-indexed) at the chat-addon
    // wire boundary so paladins on either client version can party with each
    // other and assign blessings without installing a forked addon. Strictly
    // prefix-gated to "PLPWR" — no other addon traffic is touched. Disable if
    // a future PallyPower protocol bump invalidates the assumption.
    public static bool EnablePallyPowerInterop;
    // JimsProxy (low-latency-mode): forward every CMSG_CAST_SPELL immediately
    // without holding. Eliminates hold-queue race conditions that cause stuck
    // spells for players with <40ms RTT. Most players should leave this OFF.
    public static bool LowLatencyMode;
    // JimsProxy: suppress transient cast errors (NotReady, SpellInProgress) so
    // the client doesn't show red error text during rapid spam. Independent of
    // LowLatencyMode — useful as a companion setting but not required.
    public static bool SuppressSpellCastErrors;
    // JimsProxy (T1 identity-pinned cast correspondence): make the START↔terminating
    // CastID pairing deterministic. Every local-player terminating event
    // (SPELL_GO / CAST_FAILED / SPELL_FAILURE) and the watchdog's synthetic closure
    // is stamped with the CastID recorded at SPELL_START, drawn from a per-spell FIFO,
    // instead of relying on which queue entry the dequeue heuristic picked. Sub-toggle
    // of LowLatencyMode: only the immediate-forward path creates the concurrent
    // same-spell entries this disambiguates, so it has no effect (and the Hold-and-Fire
    // path stays byte-identical) unless BOTH are on. Default OFF — opt-in experiment.
    public static bool IdentityPinnedCastIds;
    // Effective gate for the T1 mechanism: the sub-toggle is live only under LowLatencyMode.
    public static bool IdentityPinnedCastIdsActive => LowLatencyMode && IdentityPinnedCastIds;
    // JimsProxy (Spell Success Kit Reset): an instant cast forwards SPELL_START+SPELL_GO in the same
    // client frame; the 1.14 client can drop the cast kit's stop (SpellVisualEvent end-event 13),
    // leaving the cast-hold kit — which carries the cast pose AND its looping sound — stuck on until
    // logout. When set, on a local instant's SPELL_GO (cast success) re-fire the kit stop
    // (SMSG_CANCEL_SPELL_VISUAL) a few ms later in a clean frame. No-op on clean casts (the kit
    // already stopped). Independent of LowLatencyMode — works in both. Default OFF — opt-in.
    public static bool RefireSpellGo;
    // JimsProxy (#313): width (ms) of the spell-queue hold window. A press arriving in the
    // last SpellQueueWindowMs of an active GCD or cast bar is held and fired at expiry; earlier
    // presses are forwarded and the server arbitrates (NOT_READY / SpellInProgress). Mirrors the
    // 1.14 SpellQueueWindow contract. The launcher exposes 400 (retail-accurate) / 1000 / 1300
    // (smoothest, closest to the old full-hold); lower values are allowed, capped at 1300 ms.
    public static int SpellQueueWindowMs;
    // JimsProxy (PR #228 follow-up): synthesize SMSG_THREAT_UPDATE / HIGHEST /
    // CLEAR so the modern client's native threat APIs (UnitDetailedThreatSituation,
    // UNIT_THREAT_LIST_UPDATE) populate. Default on. Disable for players who
    // don't use threat meters or who want to rule the engine out as the cause
    // of an issue without swapping proxy versions.
    //
    // Gate placement: only blocks ThreatTracker per-event work (damage/heal/
    // energize/spell-cast intake, set-bonus counting, hysteresis, SMSG emission).
    // The talent synthesizer in SpellHandler.cs (SynthesizedTalentRanks reconcile)
    // runs unconditionally — other systems consume that data.
    //
    // Default-init to true so tests / paths that bypass LoadAndVerifyFrom get
    // the safe default; LoadAndVerifyFrom still overrides from config below.
    public static bool ThreatEngine = false;
    // JimsProxy: server fork detection. Different vanilla 1.12 forks (Kronos,
    // vmangos, Twinstar) have subtly different wire formats for some CMSGs.
    // Default Kronos since this launcher is built for Kronos.
    public static ServerFork ServerType = ServerFork.Kronos;

    public static bool LoadAndVerifyFrom(ConfigurationParser config)
    {
        ClientSeed = config.GetByteArray("ClientSeed", "179D3DC3235629D07113A9B3867F97A7".ParseAsByteArray());
        ClientBuild = config.GetEnum("ClientBuild", ClientVersionBuild.V2_5_2_40892);
        var serverBuildStr = config.GetString("ServerBuild", "auto");
        if (serverBuildStr == "auto")
            ServerBuild = VersionChecker.GetBestLegacyVersion(ClientBuild);
        else
            ServerBuild = config.GetEnum("ServerBuild", ClientVersionBuild.Zero);
        ServerAddress = config.GetString("ServerAddress", "127.0.0.1");
        ServerPort = config.GetInt("ServerPort", 3724);
        ReportedOS = config.GetString("ReportedOS", "OSX");
        ReportedPlatform = config.GetString("ReportedPlatform", "x86");
        ExternalAddress = config.GetString("ExternalAddress", "127.0.0.1");
        RestPort = config.GetInt("RestPort", 8081);
        BNetPort = config.GetInt("BNetPort", 1119);
        RealmPort = config.GetInt("RealmPort", 8084);
        InstancePort = config.GetInt("InstancePort", 8086);
        DebugOutput = config.GetBoolean("DebugOutput", false);
        PacketsLog = config.GetBoolean("PacketsLog", true);
        SpanStatsLog = config.GetBoolean("SpanStatsLog", false);
        // JimsProxy: structured logging defaults on; toggle VerboseLog to enable per-packet Verbose console output
        StructuredLog = config.GetBoolean("StructuredLog", true);
        VerboseLog = config.GetBoolean("VerboseLog", false);
        SpellCastEarlyFireOffsetMs = Math.Clamp(config.GetInt("SpellCastEarlyFireOffsetMs", 0), 0, 50);
        EnableUnplannedReconnect = config.GetBoolean("EnableUnplannedReconnect", false);
        UnplannedReconnectTimeoutMs = Math.Clamp(config.GetInt("UnplannedReconnectTimeoutMs", 5000), 1000, 30000);
        EnablePallyPowerInterop = config.GetBoolean("EnablePallyPowerInterop", true);
        LowLatencyMode = config.GetBoolean("LowLatencyMode", false);
        SuppressSpellCastErrors = config.GetBoolean("SuppressSpellCastErrors", false);
        IdentityPinnedCastIds = config.GetBoolean("IdentityPinnedCastIds", false);
        RefireSpellGo = config.GetBoolean("RefireSpellGo", true);
        SpellQueueWindowMs = Math.Clamp(config.GetInt("SpellQueueWindowMs", 1300), 0, 1300);
        ThreatEngine = config.GetBoolean("ThreatEngine", false);
        var serverTypeStr = config.GetString("ServerType", "Kronos");
        ServerType = serverTypeStr.Equals("Generic", StringComparison.OrdinalIgnoreCase)
            ? ServerFork.Generic : ServerFork.Kronos;
        Log.StructuredLogEnabled = StructuredLog;
        Log.VerboseLogEnabled = VerboseLog;
        // Open the JSONL file now so session.start's payload can include the full path.
        // Without this, the first call to Log.Event evaluates payload args (including
        // Log.StructuredLogPath) before EnsureJsonlOpen runs inside Event().
        Log.StartStructuredLog();

        return VerifyConfig();
    }
    
    private static bool VerifyConfig()
    {
        if (ClientSeed.Length != 16)
        {
            Log.Print(LogType.Server, "ClientSeed must have byte length of 16 (32 characters)");
            return false;
        }

        if (!VersionChecker.IsSupportedModernVersion(ClientBuild))
        {
            Log.Print(LogType.Server, $"Unsupported ClientBuild '{ClientBuild}'");
            return false;
        }

        if (!VersionChecker.IsSupportedLegacyVersion(ServerBuild))
        {
            Log.Print(LogType.Server, $"Unsupported ServerBuild '{ServerBuild}', use 'auto' to select best");
            return false;
        }

        if (!IsValidPortNumber(RestPort))
        {
            Log.Print(LogType.Server, $"Specified battle.net port ({RestPort}) out of allowed range (1-65535)");
            return false;
        }

        if (!IsValidPortNumber(ServerPort))
        {
            Log.Print(LogType.Server, $"Specified battle.net port ({BNetPort}) out of allowed range (1-65535)");
            return false;
        }

        if (!IsValidPortNumber(BNetPort))
        {
            Log.Print(LogType.Server, $"Specified battle.net port ({BNetPort}) out of allowed range (1-65535)");
            return false;
        }

        if (!IsValidPortNumber(RealmPort))
        {
            Log.Print(LogType.Server, $"Specified battle.net port ({RealmPort}) out of allowed range (1-65535)");
            return false;
        }

        if (!IsValidPortNumber(InstancePort))
        {
            Log.Print(LogType.Server, $"Specified battle.net port ({InstancePort}) out of allowed range (1-65535)");
            return false;
        }

        bool IsValidPortNumber(int someNumber)
        {
            return someNumber > IPEndPoint.MinPort && someNumber < IPEndPoint.MaxPort;
        }

        return true;
    }
}
