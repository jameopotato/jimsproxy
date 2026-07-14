using HermesProxy.Enums;
using HermesProxy.World.Enums;
using HermesProxy.World.Server;
using System;
using System.Globalization;

namespace HermesProxy.World;

// JimsProxy KTM (KLHThreatMeter) threat interop — origination half.
//
// The rewrite half (KtmThreatBridge.RewriteOutbound) makes our number win when
// the local client ALSO runs a KTM addon: it rewrites that addon's outbound
// KLHTM "t <n>" to our value. But the launcher disables KTM addons when the
// threat engine is on, so the common case is a 1.14 player with our engine and
// NO KTM addon — nothing on the wire for the rewrite to catch, and 1.12
// KLHThreatMeter raiders would see nothing from that player.
//
// This originator fills that gap: when our engine has a current-target threat
// value and the local client is NOT emitting its own KLHTM, we broadcast the
// KLHTM "t <n>" ourselves so 1.12 raiders see our number. Exactly one KLHTM
// stream per player either way (rewrite XOR originate), carrying our data.
//
// Cadence mirrors KTMClassic's LibThreatClassic2 fork: current-target threat,
// floor()'d, CHANGE-GATED, throttled to 2s (1s for warriors — its warrior /
// tanking-module x0.5). Rather than a heartbeat timer, we drive it off the
// engine's own EmitDirty flush (ThreatTracker calls MaybeBroadcast after each
// threat recompute): during combat that fires many times a second, so the
// throttle paces real sends to the KTM cadence, and when combat stops the sends
// stop too — matching KTMClassic (change-gated) and letting the 1.12 receiver
// age the entry out on its own.
public sealed class KtmThreatOriginator
{
    // KTMClassic GetPublishInterval: base 2s, x0.5 (1s) for warrior / tanking.
    // "Tanking module" is addon-internal and unobservable from the wire, so we
    // approximate it with warrior class — the dominant vanilla main tank.
    private const long BaseIntervalMs = 2000;
    private const long WarriorIntervalMs = 1000;

    // How long after the local client's last own KLHTM message we treat it as
    // "running a KTM addon" and suppress origination (the rewrite owns that
    // stream). KTMClassic publishes every 1-2s, so a 5s window comfortably spans
    // its cadence without latching stale after the addon goes away.
    private const long ClientKtmActiveWindowMs = 5000;

    private readonly GlobalSessionData _session;

    private long _lastEmitMs;                                  // Environment.TickCount64 of our last KLHTM send (0 = none yet)
    private long _lastEmittedValue = -1;
    private long _lastClientKtmMs = -ClientKtmActiveWindowMs;  // "never seen" sentinel — starts outside the window

    public KtmThreatOriginator(GlobalSessionData session)
    {
        _session = session;
    }

    // Stamp: the local client just sent its OWN KLHTM message (it runs a KTM
    // addon). While recent, MaybeBroadcast suppresses origination so we don't
    // double up on the rewrite. Called from the HandleAddonMessage chokepoint.
    public void NoteClientKtmActivity()
    {
        _lastClientKtmMs = Environment.TickCount64;
    }

    // Clear per-character throttle / change state (relog / character switch), so
    // the next fight broadcasts fresh. Called alongside ThreatTracker.Reset.
    public void Reset()
    {
        _lastEmitMs = 0;
        _lastEmittedValue = -1;
        _lastClientKtmMs = -ClientKtmActiveWindowMs;
    }

    // Called from ThreatTracker.EmitDirty after each threat flush. Broadcasts our
    // current-target threat on KLHTM when: we have a world connection, we're
    // grouped, the local client isn't already emitting its own KLHTM, and the
    // value is positive, changed, and past the per-class throttle. Any miss is a
    // cheap no-op — this runs on the combat hot path.
    public void MaybeBroadcast()
    {
        if (_session.WorldClient == null)
            return;

        // Solo → no RAID/PARTY channel to broadcast on (and threat is non-zero
        // solo too). Skip rather than have the server reject "no group".
        if (!TryGetGroupContext(out bool isRaid))
            return;

        long threat = _session.ThreatTracker.GetKtmBroadcastThreat();
        long now = Environment.TickCount64;
        long interval = LocalPlayerIsWarrior() ? WarriorIntervalMs : BaseIntervalMs;

        if (!ShouldEmit(threat, now, _lastClientKtmMs, _lastEmittedValue, _lastEmitMs, interval))
            return;

        Emit(threat, isRaid);
        _lastEmittedValue = threat;
        _lastEmitMs = now;
    }

    // Pure decision — the change-gate / throttle / gap-suppress logic, split out
    // so it's unit-testable without a GlobalSessionData graph. Grouped-ness and
    // the world connection are checked by the caller (they need session state).
    internal static bool ShouldEmit(long threat, long now, long lastClientKtmMs,
        long lastEmittedValue, long lastEmitMs, long interval)
    {
        // Local client runs a KTM addon → its (rewritten) stream already carries
        // our number; don't add a second one.
        if (now - lastClientKtmMs < ClientKtmActiveWindowMs)
            return false;
        // No data (engine off / in BG / no target / untracked). Never broadcast a
        // 0 — a 1.12 receiver would show us dropped off the meter.
        if (threat <= 0)
            return false;
        // Change-gated: KTMClassic only publishes when the value differs.
        if (threat == lastEmittedValue)
            return false;
        // Throttle to the KTM cadence (1s warrior / 2s otherwise). The first emit
        // (lastEmitMs == 0) bypasses the throttle so a fresh fight broadcasts at once.
        if (lastEmitMs != 0 && now - lastEmitMs < interval)
            return false;
        return true;
    }

    // Reuse HealCommBridge's group read: CurrentGroups[0] is the home group; raid
    // vs party is encoded in its PartyFlags, not which slot is populated. Returns
    // false when ungrouped.
    private bool TryGetGroupContext(out bool isRaid)
    {
        isRaid = false;
        var groups = _session.GameState.CurrentGroups;
        if (groups == null || groups.Length == 0)
            return false;
        var home = groups[0];
        if (home == null)
            return false;
        isRaid = (home.PartyFlags & GroupFlags.MaskBgRaid) != 0;
        return true;
    }

    private bool LocalPlayerIsWarrior()
    {
        var guid = _session.GameState.CurrentPlayerGuid;
        if (guid.IsEmpty())
            return false;
        if (_session.GameState.CachedPlayers.TryGetValue(guid, out var cached))
            return cached.ClassId == Class.Warrior;
        return false;
    }

    // Emit our KLHTM broadcast to the legacy server, mirroring HealCommBridge's
    // EmitToServer: prefix/body joined by '\t', Language.Addon, RAID or PARTY.
    private void Emit(long threat, bool isRaid)
    {
        var worldClient = _session.WorldClient;
        if (worldClient == null)
            return;

        string text = KtmThreatBridge.KtmPrefix + '\t' + "t " + threat.ToString(CultureInfo.InvariantCulture);
        uint addonLanguage = (uint)Language.Addon;

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
        {
            ChatMessageTypeWotLK chatType = isRaid ? ChatMessageTypeWotLK.Raid : ChatMessageTypeWotLK.Party;
            worldClient.SendMessageChatWotLK(chatType, addonLanguage, text, "", "");
        }
        else
        {
            ChatMessageTypeVanilla chatType = isRaid ? ChatMessageTypeVanilla.Raid : ChatMessageTypeVanilla.Party;
            worldClient.SendMessageChatVanilla(chatType, addonLanguage, text, "", "");
        }
    }
}
