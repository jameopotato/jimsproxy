using HermesProxy.Enums;
using HermesProxy.World;
using HermesProxy.World.Client;
using HermesProxy.World.Enums;
using HermesProxy.World.Server.Packets;
using Xunit;

namespace HermesProxy.Tests.World;

/// <summary>
/// JimsProxy (#505): placement of battleground scoreboard rows the player cache cannot resolve.
/// See <see cref="ScoreboardRowResolver"/>.
///
/// The old miss branch wrote Male/Human/Warrior with the Faction bit false for every unresolved
/// row, and treated a cache entry without a race as a hit (Race None, Faction false). Every
/// placement test below that expects Faction true or Race Orc on a miss, or a Race None entry to
/// be a miss, fails against that behaviour.
/// </summary>
public class ScoreboardRowResolverTests
{
    static ScoreboardRowResolverTests()
    {
        if (global::Framework.Settings.ClientBuild == ClientVersionBuild.Zero)
            global::Framework.Settings.ClientBuild = ClientVersionBuild.V1_14_2_42597;
    }

    private static WowGuid128 Player(ulong counter) => WowGuid128.Create(HighGuidType703.Player, counter);

    private static PartyUpdate Group(params WowGuid128[] members)
    {
        var party = new PartyUpdate();
        foreach (var guid in members)
            party.PlayerList.Add(new PartyPlayerInfo { GUID = guid, Name = "", VoiceStateID = "" });
        return party;
    }

    // --- Resolve: cache hits ---

    [Fact]
    public void CacheHitWithRace_UsesCacheValues_AndFactionFromRace()
    {
        var cache = new PlayerCache { RaceId = Race.Orc, ClassId = Class.Rogue, SexId = Gender.Female };

        // Placement inputs are ignored on a hit: an own-team row of an Alliance player stays Horde.
        var row = ScoreboardRowResolver.Resolve(cache, isOwnTeam: true, localIsAlliance: true);

        Assert.True(row.FromCache);
        Assert.False(row.Faction);
        Assert.Equal(Race.Orc, row.Race);
        Assert.Equal(Gender.Female, row.Sex);
        Assert.Equal(Class.Rogue, row.Class);
    }

    [Fact]
    public void CacheHitWithAllianceRace_IsAlliance_EvenWhenPlacementSaysEnemyOfAlliance()
    {
        var cache = new PlayerCache { RaceId = Race.NightElf, ClassId = Class.Druid, SexId = Gender.Male };

        var row = ScoreboardRowResolver.Resolve(cache, isOwnTeam: false, localIsAlliance: true);

        Assert.True(row.FromCache);
        Assert.True(row.Faction);
        Assert.Equal(Race.NightElf, row.Race);
        Assert.Equal(Class.Druid, row.Class);
    }

    // Group members are cached with name and class but no race; that entry must not decide the
    // faction (the old hit path wrote Race None with Faction false, i.e. Horde).
    [Fact]
    public void CacheHitWithRaceNone_IsTreatedAsMiss_AndKeepsCachedClass()
    {
        var cache = new PlayerCache { Name = "Member", ClassId = Class.Priest };

        var row = ScoreboardRowResolver.Resolve(cache, isOwnTeam: true, localIsAlliance: true);

        Assert.False(row.FromCache);
        Assert.True(row.Faction);
        Assert.Equal(Race.Human, row.Race);
        Assert.Equal(Gender.Male, row.Sex);
        Assert.Equal(Class.Priest, row.Class);
    }

    // --- Resolve: misses placed by team ---

    [Theory]
    [InlineData(true, true, true, Race.Human)]    // own team, local Alliance -> Alliance
    [InlineData(true, false, false, Race.Orc)]    // own team, local Horde    -> Horde
    [InlineData(false, true, false, Race.Orc)]    // enemy,    local Alliance -> Horde
    [InlineData(false, false, true, Race.Human)]  // enemy,    local Horde    -> Alliance
    public void Miss_IsPlacedByTeam(bool isOwnTeam, bool localIsAlliance, bool expectedFaction, Race expectedRace)
    {
        var row = ScoreboardRowResolver.Resolve(null, isOwnTeam, localIsAlliance);

        Assert.False(row.FromCache);
        Assert.Equal(expectedFaction, row.Faction);
        Assert.Equal(expectedRace, row.Race);
        Assert.Equal(Gender.Male, row.Sex);
        Assert.Equal(Class.Warrior, row.Class);
    }

    [Fact]
    public void Miss_WithOutOfRangeCachedClass_FallsBackToWarrior()
    {
        var cache = new PlayerCache { ClassId = Class.Max };

        var row = ScoreboardRowResolver.Resolve(cache, isOwnTeam: false, localIsAlliance: false);

        Assert.Equal(Class.Warrior, row.Class);
    }

    // --- ResolveUnplaced: local faction unknown, or an arena row ---

    [Fact]
    public void ResolveUnplaced_Miss_KeepsPreFixPlaceholder()
    {
        var row = ScoreboardRowResolver.ResolveUnplaced(null);

        Assert.False(row.FromCache);
        Assert.False(row.Faction);
        Assert.Equal(Race.Human, row.Race);
        Assert.Equal(Gender.Male, row.Sex);
        Assert.Equal(Class.Warrior, row.Class);
    }

    [Fact]
    public void ResolveUnplaced_Hit_UsesCacheValues()
    {
        var cache = new PlayerCache { RaceId = Race.Dwarf, ClassId = Class.Paladin, SexId = Gender.Female };

        var row = ScoreboardRowResolver.ResolveUnplaced(cache);

        Assert.True(row.FromCache);
        Assert.True(row.Faction);
        Assert.Equal(Race.Dwarf, row.Race);
        Assert.Equal(Gender.Female, row.Sex);
        Assert.Equal(Class.Paladin, row.Class);
    }

    // --- IsOwnTeam ---

    [Fact]
    public void IsOwnTeam_Self_WithNoGroups()
    {
        var self = Player(1);

        Assert.True(ScoreboardRowResolver.IsOwnTeam(self, self, new PartyUpdate?[2]));
    }

    // The raid sits in slot 0 when its group list arrived before the battleground map was known.
    [Fact]
    public void IsOwnTeam_MemberInGroupSlot0()
    {
        var groups = new PartyUpdate?[] { Group(Player(2), Player(3)), null };

        Assert.True(ScoreboardRowResolver.IsOwnTeam(Player(3), Player(1), groups));
    }

    [Fact]
    public void IsOwnTeam_MemberInGroupSlot1()
    {
        var groups = new PartyUpdate?[] { null, Group(Player(4), Player(5)) };

        Assert.True(ScoreboardRowResolver.IsOwnTeam(Player(5), Player(1), groups));
    }

    [Fact]
    public void IsOwnTeam_NotInAnyGroup_IsEnemy()
    {
        var groups = new PartyUpdate?[] { Group(Player(2)), Group(Player(4)) };

        Assert.False(ScoreboardRowResolver.IsOwnTeam(Player(9), Player(1), groups));
        Assert.False(ScoreboardRowResolver.IsOwnTeam(Player(9), Player(1), new PartyUpdate?[2]));
    }

    // --- CountByFaction ---

    [Fact]
    public void CountByFaction_AllianceIndex1_HordeIndex0()
    {
        var counts = new sbyte[2];

        ScoreboardRowResolver.CountByFaction(counts, faction: true);
        ScoreboardRowResolver.CountByFaction(counts, faction: true);
        ScoreboardRowResolver.CountByFaction(counts, faction: false);

        Assert.Equal(1, counts[0]);
        Assert.Equal(2, counts[1]);
    }

    [Fact]
    public void CountByFaction_SaturatesAtSbyteMax()
    {
        var counts = new sbyte[] { sbyte.MaxValue - 1, 0 };

        ScoreboardRowResolver.CountByFaction(counts, faction: false);
        ScoreboardRowResolver.CountByFaction(counts, faction: false);

        Assert.Equal(sbyte.MaxValue, counts[0]);
        Assert.Equal(0, counts[1]);
    }
}
