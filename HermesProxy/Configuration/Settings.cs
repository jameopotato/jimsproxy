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

// JimsProxy (rtt-prefire): GCD-boundary chaining strategy under Low-Latency mode. See the
// RttPrefire setting below for semantics.
public enum RttPrefireMode { Off, Timer, Knocker }

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
    // JimsProxy (stuck-logout-stun): counter Kronos's incomplete reconnect cleanup. A fast
    // same-character relogin after an abrupt in-combat disconnect re-attaches the session to
    // the player object that lingered in-world; Kronos clears the logout root but leaves the
    // aura-less "artificial" UNIT_FLAG_STUNNED, so the client logs in unable to cast or turn
    // ("You can't do that while stunned") until a character switch. Detection fires once per
    // login, on the first self create-block, only when the stunned flag arrives with zero
    // debuff auras (every real vanilla stun occupies a debuff slot in the same block).
    // CancelFix synthesizes a legacy CMSG_LOGOUT_CANCEL — the lineage server handler that
    // removes the artificial stun. ClientStrip clears the bit from the forwarded create
    // block so input isn't locked client-side even if the server ignores the cancel.
    public static bool StuckLogoutStunCancelFix;
    public static bool StuckLogoutStunClientStrip;
    // JimsProxy (#382 MC-cap BG FPS drop): strip UNIT_FLAG_PET_IN_COMBAT (0x800) from a
    // PLAYER for exactly the duration of a player-on-player charm (Gnomish MC Cap 13181,
    // priest MC 605). Vanilla cores set that flag on the charmed unit itself; modern
    // servers set it on the CHARMER — so charm state + 0x800 on one player is a hybrid no
    // modern server produces, and the 1.14 client FPS-locking on charmed players (victim
    // AND bystanders, BG-scale, native 1.12 clients unaffected) is suspected to be its
    // cost. Includes a charm-edge flags re-sync for pet-class victims whose legitimate
    // pet-owner 0x800 predates the charm (a charm apply can carry no flags write at all).
    // NPC charmers (raid MC — Lucifron) are untouched. Default ON; key = kill switch.
    // Default-init true so paths that bypass LoadAndVerifyFrom (tests) get the fix.
    public static bool Charm382StripPetInCombat = true;
    // JimsProxy (clean handshake teardown): bound the realmd auth handshake. If realmd accepts
    // the TCP connection but never sends LOGON_CHALLENGE (half-accepting login server, load-shed,
    // firewall), the login thread would otherwise block forever ("stuck on Connecting..."). On
    // expiry the login fails cleanly — the client shows an error and can retry — instead of
    // hanging. Clamped to 1000..60000 in LoadAndVerifyFrom.
    public static int AuthHandshakeTimeoutMs;
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
    // JimsProxy (rtt-prefire): chain casts across the GCD boundary under Low-Latency mode.
    //   Off     — pure forward-everything (the plain LL behavior; default).
    //   Timer   — a press landing inside the last RttPrefireTimerWindowMs (fixed 400 ms) of
    //             the GCD is held in
    //             the existing hold slot and released by the BeginGcd timer at (estimated
    //             expiry − early-fire offset); SpellCastEarlyFireOffsetMs / OverrideRtt govern
    //             the offset exactly as in queue mode. Public advanced toggle.
    //   Knocker — SugarProxy-parity: forward immediately, then re-send the same CMSG every
    //             20ms (max 10 knocks, ≤200ms) until the cast starts/resolves or a newer
    //             press supersedes it. Server-rough (each early knock bounces NOT_READY) —
    //             experimental; the launcher only offers it in Dev Mode.
    // Read only while LowLatencyMode is on; queue mode ignores it entirely.
    public static RttPrefireMode RttPrefire;
    // Effective gates: the sub-mode is live only under LowLatencyMode (mirrors
    // IdentityPinnedCastIdsActive).
    public static bool RttPrefireTimerActive => LowLatencyMode && RttPrefire == RttPrefireMode.Timer;
    public static bool RttPrefireKnockerActive => LowLatencyMode && RttPrefire == RttPrefireMode.Knocker;
    // JimsProxy (rtt-prefire): Timer's hold-admission window is FIXED at the retail-accurate
    // 400 ms and deliberately does not read SpellQueueWindowMs. The launcher greys the
    // spell-queue controls under Low-Latency mode, so their stored value must not silently
    // govern Timer (2026-07-15: a stored 1000 stretched Timer holds to ~500 ms and correlated
    // with elevated stuck-lit button reports).
    public const int RttPrefireTimerWindowMs = 400;
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
    // JimsProxy (RefireSpellGo): an instant cast forwards SPELL_START+SPELL_GO in the same client
    // frame; the 1.14 client can drop the coalesced SPELL_GO, so the cast never closes — a stuck
    // cast pose + looping cast sound + lit action button that persists until logout (survives
    // /reload). When set, on a local instant's first SPELL_GO re-fire a stripped duplicate SPELL_GO
    // (~8ms later, in a clean frame, visual suppressed, no targets/log) so the client processes it
    // and closes the cast. No-op on clean casts. Independent of LowLatencyMode. Default OFF — opt-in.
    public static bool RefireSpellGo;
    // JimsProxy (#379 form-exit): the 1.14 client auto-shifts out of a form to cast
    // (CMSG_CANCEL_AURA + CMSG_CAST_SPELL ~1ms apart), but the 1.12 server emits the cast's
    // SMSG_SPELL_START ~20ms BEFORE the form-removal SMSG_UPDATE_OBJECT. The cast's visual kit
    // then starts on the still-shifted model and is dropped/orphaned during the model swap —
    // looping cast sound (cast-time spells) or stuck transform sound (instant / form→form) until
    // /reload. Fix: after a local form-cancel, defer the next local SPELL_START by this many ms
    // (bounded — never crosses its own GO) so the form-removal update lands and the model swap
    // renders first. Covers the client-side model-swap render time (frame-rate dependent, ~30ms
    // on a fast machine), NOT latency — do not tune per-connection. 0 disables.
    public static int FormExitStartDeferMs;
    // JimsProxy (#313): width (ms) of the spell-queue hold window. A press arriving in the
    // last SpellQueueWindowMs of an active GCD or cast bar is held and fired at expiry; earlier
    // presses are forwarded and the server arbitrates (NOT_READY / SpellInProgress). Mirrors the
    // 1.14 SpellQueueWindow contract. The launcher exposes 400 (retail-accurate, the default)
    // / 1000 / 1300 (smoothest, closest to the old full-hold); lower values are allowed,
    // capped at 1300 ms. Ignored by RTT Pre-Fire Timer, which uses the fixed
    // RttPrefireTimerWindowMs instead.
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
        StuckLogoutStunCancelFix = config.GetBoolean("StuckLogoutStunCancelFix", true);
        StuckLogoutStunClientStrip = config.GetBoolean("StuckLogoutStunClientStrip", true);
        Charm382StripPetInCombat = config.GetBoolean("Charm382StripPetInCombat", true);
        AuthHandshakeTimeoutMs = Math.Clamp(config.GetInt("AuthHandshakeTimeoutMs", 15000), 1000, 60000);
        EnablePallyPowerInterop = config.GetBoolean("EnablePallyPowerInterop", true);
        LowLatencyMode = config.GetBoolean("LowLatencyMode", false);
        SuppressSpellCastErrors = config.GetBoolean("SuppressSpellCastErrors", false);
        IdentityPinnedCastIds = config.GetBoolean("IdentityPinnedCastIds", false);
        RefireSpellGo = config.GetBoolean("RefireSpellGo", false);
        var rttPrefireStr = config.GetString("RttPrefire", "off");
        RttPrefire = rttPrefireStr.Equals("timer", StringComparison.OrdinalIgnoreCase) ? RttPrefireMode.Timer
            : rttPrefireStr.Equals("knocker", StringComparison.OrdinalIgnoreCase) ? RttPrefireMode.Knocker
            : RttPrefireMode.Off;
        FormExitStartDeferMs = Math.Clamp(config.GetInt("FormExitStartDeferMs", 100), 0, 300);
        SpellQueueWindowMs = Math.Clamp(config.GetInt("SpellQueueWindowMs", 400), 0, 1300);
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
