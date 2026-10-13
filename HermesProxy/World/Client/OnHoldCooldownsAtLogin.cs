using System;
using System.Collections.Generic;
using HermesProxy.World.Objects;
using HermesProxy.World.Server.Packets;

namespace HermesProxy.World.Client;

/// <summary>
/// JimsProxy (stealth cooldown after a stealthed login): some vanilla auras start their cooldown
/// when the aura ENDS, not when it is cast (Stealth, Prowl, Shadowmeld: 1.12 spell attribute
/// "disabled while active"). A native server keeps such a cooldown "on hold" from the cast and
/// announces the start with SMSG_COOLDOWN_EVENT when the aura is removed; on login it lists the
/// held cooldown in the spell history, OnHold set. The 1.12 server's login cooldown list only
/// carries cooldowns already running, so a character who logs in stealthed has no held entry;
/// the cooldown event that follows the cancel then has nothing to start and the Stealth button
/// shows no sweep, while the server refuses the recast until the real cooldown ends. Field
/// finding 2026-10-12, pre-existing on stock.
///
/// Cure: after the local player's create at login, send one SMSG_SPELL_COOLDOWN listing the
/// aura-active spells of that kind with the on-hold flag and no forced duration, so the client
/// holds the entry with its own duration until the server's cooldown event starts it. Pure
/// functions, unit-tested in <c>OnHoldCooldownsAtLoginTests</c>.
/// </summary>
internal static class OnHoldCooldownsAtLogin
{
    /// <summary>The spell-cooldown packet flag that marks every listed entry as held until a
    /// cooldown event (the same bit a native server sets on a held cooldown).</summary>
    internal const byte SpellCooldownFlagOnHold = 0x08;

    /// <summary>The aura-active spells whose cooldown starts on removal, in slot order, once each.</summary>
    internal static List<uint> Select(IEnumerable<uint> auraSpellIds, Func<uint, bool> cooldownStartsOnRemoval)
    {
        var result = new List<uint>();
        foreach (var id in auraSpellIds)
        {
            if (id == 0 || result.Contains(id))
                continue;
            if (cooldownStartsOnRemoval(id))
                result.Add(id);
        }
        return result;
    }

    /// <summary>One held entry per spell: no forced duration (the client takes the spell's own), rate 1.</summary>
    internal static SpellCooldownPkt BuildPacket(WowGuid128 player, IEnumerable<uint> spellIds)
    {
        var packet = new SpellCooldownPkt
        {
            Caster = player,
            Flags = SpellCooldownFlagOnHold,
        };
        foreach (var id in spellIds)
            packet.SpellCooldowns.Add(new SpellCooldownStruct { SpellID = id, ForcedCooldown = 0, ModRate = 1.0f });
        return packet;
    }
}
