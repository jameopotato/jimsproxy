using System;
using HermesProxy.World.Enums;

namespace HermesProxy.World.Client;

/// <summary>
/// JimsProxy (a resisted or immune cast left open, 2026-10-18): decides how many miss entries a
/// 1.12 SMSG_SPELL_GO really carries.
///
/// The miss-count byte Kronos sends does not always match the entries that follow it. With no
/// miss the byte is often 1 and no entry follows (the "phantom" byte the parser already skipped).
/// With one miss the byte is 2 and exactly one entry follows: three captured packets, a resisted
/// Mind Control on a player (2026-10-18), an immune Mind Control on a creature (2026-10-13) and a
/// resisted Drain Soul (2026-08-21). The old rule zeroed any count whose entries would not fit, so
/// the one real entry was then read as the target section, the parse failed and the GO was dropped:
/// the cast never completed on the client and the caster stayed in the casting pose.
///
/// Rule: try the declared count, then one fewer, down to zero, and take the first count whose miss
/// entries (GUID u64, miss type u8 no higher than Reflect, plus the reflect byte for Reflect) and
/// the vanilla target section after them (u16 flags, the packed GUIDs, locations and string they
/// name, then the ammo block when the cast flags carry Projectile) consume the remaining bytes
/// exactly. -1 when no count fits, so the caller keeps its previous behaviour.
/// </summary>
public static class VanillaSpellGoMissCount
{
    private const SpellCastTargetFlags GuidTargets = SpellCastTargetFlags.Unit | SpellCastTargetFlags.CorpseEnemy |
        SpellCastTargetFlags.GameObject | SpellCastTargetFlags.CorpseAlly | SpellCastTargetFlags.UnitMinipet;
    private const SpellCastTargetFlags ItemTargets = SpellCastTargetFlags.Item | SpellCastTargetFlags.TradeItem;

    /// <param name="rest">The packet bytes right after the miss-count byte.</param>
    /// <param name="declared">The miss-count byte as sent.</param>
    /// <param name="castFlags">The legacy cast flags of this GO.</param>
    public static int Resolve(ReadOnlySpan<byte> rest, int declared, uint castFlags)
    {
        for (int n = declared; n >= 0; n--)
            if (Fits(rest, n, castFlags))
                return n;
        return -1;
    }

    internal static bool Fits(ReadOnlySpan<byte> b, int misses, uint castFlags)
    {
        int p = 0;
        for (int i = 0; i < misses; i++)
        {
            if (p + 9 > b.Length)
                return false;
            byte type = b[p + 8];
            if (type > (byte)SpellMissInfo.Reflect)
                return false;
            p += 9;
            if (type == (byte)SpellMissInfo.Reflect)
            {
                if (p + 1 > b.Length)
                    return false;
                p += 1;
            }
        }

        if (p + 2 > b.Length)
            return false;
        var flags = (SpellCastTargetFlags)(b[p] | (b[p + 1] << 8));
        p += 2;
        if ((flags & GuidTargets) != 0 && !SkipPackedGuid(b, ref p))
            return false;
        if ((flags & ItemTargets) != 0 && !SkipPackedGuid(b, ref p))
            return false;
        if ((flags & SpellCastTargetFlags.SourceLocation) != 0)
            p += 12;
        if ((flags & SpellCastTargetFlags.DestLocation) != 0)
            p += 12;
        if ((flags & SpellCastTargetFlags.String) != 0)
        {
            if (p > b.Length)
                return false;
            int nul = b.Slice(p).IndexOf((byte)0);
            if (nul < 0)
                return false;
            p += nul + 1;
        }
        if ((castFlags & (uint)CastFlag.Projectile) != 0)
            p += 8;
        return p == b.Length;
    }

    private static bool SkipPackedGuid(ReadOnlySpan<byte> b, ref int p)
    {
        if (p + 1 > b.Length)
            return false;
        int mask = b[p];
        int bytes = 0;
        for (int bit = 0; bit < 8; bit++)
            if ((mask & (1 << bit)) != 0)
                bytes++;
        p += 1 + bytes;
        return p <= b.Length;
    }
}
