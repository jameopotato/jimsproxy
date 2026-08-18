using System.Collections.Frozen;
using System.Collections.Generic;
using HermesProxy.World.Enums;
using HermesProxy.World.Objects;

namespace HermesProxy.World.Client;

/// <summary>
/// JimsProxy (feared-while-sitting, issue #479): pure decision logic behind the
/// synthesized stand-up for a seated local player targeted by fear.
///
/// Vanilla-lineage servers never stand a seated player when a non-damaging fear
/// lands (only damage and stun do), and the 1.14 client surrenders all input the
/// moment control is removed — it cannot stand itself, so a player feared while
/// drinking stays seated for the whole fear and every fear-break press (PvP
/// insignia) dies on NOT_STANDING, server- or client-side. Kronos additionally
/// generates no flee movement for player-cast fears (#479), so nothing else ever
/// gets the player out of the chair.
///
/// Two triggers, both synthesizing a legacy CMSG_STAND_STATE_CHANGE(STAND):
///  1. Incoming-cast pre-stand: a fear-aura cast with a cast time STARTS against
///     the seated local player. The synth reaches the server while the player
///     still has control, so it is honored unconditionally; the fear then lands
///     on a standing player. Covers warlock Fear — the reported PvP case.
///  2. CC-onset fallback: the local player's UNIT_FIELD_FLAGS gain Fleeing or
///     Confused while seated (instant fears — Psychic Scream, many mob fears —
///     never produce a victim-side SPELL_START). Whether the lineage server
///     honors a stand-state change mid-fear is untested (asked in #479); if
///     ignored, the synth is one harmless packet.
/// </summary>
public static class FearStandSynth
{
    // UNIT_FLAG_FLEEING | UNIT_FLAG_CONFUSED — the CC classes that leave a seated
    // player seated. Stun is excluded: every vanilla lineage stands the player
    // server-side on stun apply, so a synth there would be redundant.
    public const uint FearConfuseUnitFlagsMask = (uint)(UnitFlagsVanilla.Fleeing | UnitFlagsVanilla.Confused);

    // Every 1.12.1 spell carrying SPELL_AURA_MOD_FEAR (aura type 7), extracted from
    // CSV/LossOfControlSpells1.csv of PR #440 (vmangos 1.12.1 spell_template rows
    // cross-verified against cmangos classic-db's direct Spell.dbc conversion).
    // NPC fears included — mobs fear drinking players too. 74 spells.
    public static readonly FrozenSet<uint> FearAuraSpellIds = new HashSet<uint>
    {
        16, 1513, 2878, 5134, 5246, 5484, 5543, 5627, 5782, 6213, 6215, 6243,
        6576, 6605, 6614, 6669, 6789, 7093, 7399, 8122, 8124, 8225, 8715, 8817,
        9458, 10326, 10888, 10890, 12096, 12542, 12613, 12730, 13488, 13704,
        14100, 14326, 14327, 16508, 17925, 17926, 17928, 18431, 19134, 19408,
        19725, 20672, 21330, 21869, 21898, 22678, 22686, 22884, 23275, 25260,
        25815, 26042, 26044, 26049, 26070, 26580, 26641, 27610, 27641, 27990,
        28315, 28412, 29111, 29124, 29168, 29419, 29685, 30001, 30002, 31365,
    }.ToFrozenSet();

    // Trigger 1 gate: fear-aura cast STARTING against the seated local player.
    internal static bool ShouldPreStandOnIncomingFear(bool leverOn, uint spellId, WowGuid128? targetUnit, WowGuid128? localPlayer, uint localStandState)
    {
        if (!leverOn || localStandState == 0)
            return false;
        if (targetUnit is not WowGuid128 target || localPlayer is not WowGuid128 self)
            return false;
        if (self.IsEmpty() || target != self)
            return false;
        return FearAuraSpellIds.Contains(spellId);
    }

    // Trigger 2 gate: Fleeing/Confused rising edge on the seated local player.
    // Edge-gated so FLAGS re-writes during a held fear (combat bit churn) cost nothing.
    internal static bool ShouldStandOnCcOnset(bool leverOn, uint previousCcFlags, uint newCcFlags, uint localStandState)
    {
        return leverOn && localStandState != 0 &&
               (newCcFlags & FearConfuseUnitFlagsMask) != 0 &&
               (previousCcFlags & FearConfuseUnitFlagsMask) == 0;
    }
}
