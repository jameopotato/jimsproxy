using HermesProxy.World.Server.Packets;

namespace HermesProxy.World;

// JimsProxy (cast-id breadcrumbs): DebugOutput-gated trail of every cast id we hand the client, so an Esc press names the exact object.
// Ids are written twice: `*cast_id` in the record form every other cast event in the log uses (WowGuid128.ToString(),
// so one grep finds the id across events), and `*cast_id_hex` as one fixed-width high-then-low hex word.
public static class CastIdBreadcrumbs
{
    public static string Hex(WowGuid128 guid) => $"0x{guid.High:X16}{guid.Low:X16}";

    // Returns the cast.wire payload for a local-player cast packet, or null for anything else.
    public static object? Describe(ServerPacket packet, WowGuid128 localPlayer)
    {
        switch (packet)
        {
            case SpellPrepare prepare:
                return new
                {
                    packet = "prepare",
                    client_cast_id = prepare.ClientCastID.ToString(),
                    client_cast_id_hex = Hex(prepare.ClientCastID),
                    cast_id = prepare.ServerCastID.ToString(),
                    cast_id_hex = Hex(prepare.ServerCastID),
                    cast_id_counter = prepare.ServerCastID.GetCounter(),
                };
            case SpellStart start:
                return DescribeCast("start", start.Cast, localPlayer);
            case SpellGo go:
                return DescribeCast("go", go.Cast, localPlayer);
            case CastFailed failed:
                return new
                {
                    packet = "cast_failed",
                    spell_id = failed.SpellID,
                    cast_id = failed.CastID.ToString(),
                    cast_id_hex = Hex(failed.CastID),
                    cast_id_counter = failed.CastID.GetCounter(),
                    reason = failed.Reason,
                };
            case SpellFailure failure:
                if (failure.CasterUnit != localPlayer)
                    return null;
                return new
                {
                    packet = "spell_failure",
                    spell_id = failure.SpellID,
                    cast_id = failure.CastID.ToString(),
                    cast_id_hex = Hex(failure.CastID),
                    cast_id_counter = failure.CastID.GetCounter(),
                    reason = (uint)failure.Reason,
                };
            default:
                return null;
        }
    }

    private static object? DescribeCast(string phase, SpellCastData cast, WowGuid128 localPlayer)
    {
        if (cast.CasterUnit != localPlayer && cast.CasterGUID != localPlayer)
            return null;
        return new
        {
            packet = phase,
            spell_id = cast.SpellID,
            cast_id = cast.CastID.ToString(),
            cast_id_hex = Hex(cast.CastID),
            cast_id_counter = cast.CastID.GetCounter(),
            original_cast_id = cast.OriginalCastID.IsEmpty() ? null : cast.OriginalCastID.ToString(),
            original_cast_id_hex = cast.OriginalCastID.IsEmpty() ? null : Hex(cast.OriginalCastID),
            cast_flags = cast.CastFlags,
        };
    }
}
