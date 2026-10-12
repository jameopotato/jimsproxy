using System.Collections.Generic;
using HermesProxy.World.Enums;
using HermesProxy.World.Server.Packets;

namespace HermesProxy.World.Client;

/// <summary>
/// JimsProxy (#505): pure placement of one battleground scoreboard row, extracted so the truth
/// table is unit-testable (see <c>HermesProxy.Tests/World/ScoreboardRowResolverTests</c>).
///
/// A legacy MSG_PVP_LOG_DATA row carries a GUID and scores only, so the proxy fills
/// Sex/Race/Class/Faction of SMSG_PVP_MATCH_STATISTICS from its player cache. On a miss the old
/// code wrote Male/Human/Warrior and left the Faction bit false, so the stock scoreboard drew the
/// row on the HORDE side. The Faction bit is what places a row in a team band and faction tab
/// (true = Alliance, false = Horde); the placeholder race only has to agree with it.
///
/// In a battleground every player is either in the local player's raid (own faction) or on the
/// enemy team, so a miss is placed by raid membership. The raid may sit in either group slot
/// (slot 0 when its group list arrived before the battleground map was known), so both count.
/// </summary>
internal static class ScoreboardRowResolver
{
    /// <summary>A legacy board never carries more than 80 rows (a 40 against 40 battleground); keep
    /// the forwarded board within that bound whatever the wire says.</summary>
    public const int MaxRows = 80;

    /// <summary>Per-board cap on <c>battleground.scoreboard.row_placeholder</c> events.</summary>
    public const int MaxPlaceholderLogsPerBoard = 5;

    internal readonly record struct ResolvedRow(bool Faction, Race Race, Gender Sex, Class Class, bool FromCache);

    /// <summary>
    /// A cache entry with a known race resolves the row (faction from the race). Anything else is a
    /// placeholder on the side raid membership points to: own team = the local player's faction.
    /// </summary>
    internal static ResolvedRow Resolve(PlayerCache? cache, bool isOwnTeam, bool localIsAlliance)
    {
        if (HasRace(cache))
            return FromCache(cache!);

        bool faction = isOwnTeam ? localIsAlliance : !localIsAlliance;
        return new ResolvedRow(faction, faction ? Race.Human : Race.Orc, Gender.Male, CachedClassOrWarrior(cache), FromCache: false);
    }

    /// <summary>
    /// The pre-#505 row for a miss (Faction false, Male Human), used where there is no side to place
    /// it on: the local player's own race is not cached yet, or an arena board whose faction comes
    /// from the wire.
    /// </summary>
    internal static ResolvedRow ResolveUnplaced(PlayerCache? cache)
    {
        if (HasRace(cache))
            return FromCache(cache!);

        return new ResolvedRow(false, Race.Human, Gender.Male, CachedClassOrWarrior(cache), FromCache: false);
    }

    internal static bool HasRace(PlayerCache? cache) => cache != null && cache.RaceId != Race.None;

    internal static bool IsOwnTeam(WowGuid128 guid, WowGuid128 currentPlayer, IEnumerable<PartyUpdate?> groups)
    {
        if (guid == currentPlayer)
            return true;

        foreach (var group in groups)
        {
            if (group == null)
                continue;

            foreach (var member in group.PlayerList)
            {
                if (member.GUID == guid)
                    return true;
            }
        }
        return false;
    }

    /// <summary>PlayerCount[1] counts Alliance rows and PlayerCount[0] Horde rows; saturates at sbyte.MaxValue.</summary>
    internal static void CountByFaction(sbyte[] playerCount, bool faction)
    {
        ref sbyte slot = ref playerCount[faction ? 1 : 0];
        if (slot < sbyte.MaxValue)
            slot++;
    }

    static ResolvedRow FromCache(PlayerCache cache)
        => new(GameData.IsAllianceRace(cache.RaceId), cache.RaceId, cache.SexId, cache.ClassId, FromCache: true);

    // The placeholder class does not place the row; keep a cached class when there is one (group
    // members are cached with name and class but no race), Warrior as before otherwise.
    static Class CachedClassOrWarrior(PlayerCache? cache)
        => cache != null && cache.ClassId > Class.None && cache.ClassId < Class.Max ? cache.ClassId : Class.Warrior;
}
