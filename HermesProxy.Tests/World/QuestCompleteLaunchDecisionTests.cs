using HermesProxy.World;
using Xunit;

namespace HermesProxy.Tests.World;

// JimsProxy (quest reward page left open): the launch bits of SMSG_QUEST_GIVER_QUEST_COMPLETE and the
// greeting state they read. A turn-in reached through the NPC's quest list leaves the 1.14 client's
// reward page open when both bits are clear and the list still held another quest; LaunchQuest makes
// the client re-ask the NPC, which refills the page. These tests lock in the decision order and the
// greeting bookkeeping (recorded on the list, cleared on a forwarded hello, scoped to the giver).
public class QuestCompleteLaunchDecisionTests
{
    private static GameSessionData NewState() => WowGuidTestHelper.CreateMockGameSessionData();
    private static WowGuid128 Giver(uint counter) => new WowGuid128(0x2000_0000_0000_0000UL | counter, 1234);

    [Theory]
    [InlineData(true, false, false, true, false)]   // follow-up quest: LaunchQuest, the next details follow
    [InlineData(true, true, true, true, false)]     // follow-up wins over everything
    [InlineData(false, true, false, false, true)]   // gossip NPC: LaunchGossip, the gossip page follows
    [InlineData(false, true, true, false, true)]    // gossip wins over the greeting
    [InlineData(false, false, true, true, false)]   // greeting listed another quest: LaunchQuest, the list follows
    [InlineData(false, false, false, false, false)] // nothing to follow: the client closes the page itself
    public void DecideQuestCompleteLaunch_PriorityOrder(bool followUp, bool gossip, bool greetingOthers, bool expectQuest, bool expectGossip)
    {
        var (launchQuest, launchGossip) = GameSessionData.DecideQuestCompleteLaunch(followUp, gossip, greetingOthers);

        Assert.Equal(expectQuest, launchQuest);
        Assert.Equal(expectGossip, launchGossip);
    }

    [Fact]
    public void QuestGreetingHasOtherEntries_ListWithAnotherQuest_True()
    {
        var state = NewState();
        var giver = Giver(1);
        state.RecordQuestGreeting(giver, new uint[] { 1392, 1389 });

        Assert.True(state.QuestGreetingHasOtherEntries(giver, 1392));
    }

    [Fact]
    public void QuestGreetingHasOtherEntries_OnlyTheCompletedQuest_False()
    {
        var state = NewState();
        var giver = Giver(1);
        state.RecordQuestGreeting(giver, new uint[] { 1392 });

        Assert.False(state.QuestGreetingHasOtherEntries(giver, 1392));
    }

    [Fact]
    public void QuestGreetingHasOtherEntries_NoGreetingRecorded_False()
    {
        var state = NewState();

        Assert.False(state.QuestGreetingHasOtherEntries(Giver(1), 1392));
    }

    [Fact]
    public void QuestGreetingHasOtherEntries_DifferentGiver_False()
    {
        // The list came from another NPC; the turn-in at this one was reached without a greeting.
        var state = NewState();
        state.RecordQuestGreeting(Giver(1), new uint[] { 1392, 1389 });

        Assert.False(state.QuestGreetingHasOtherEntries(Giver(2), 1392));
    }

    [Fact]
    public void QuestGreetingHasOtherEntries_EmptyGiver_False()
    {
        var state = NewState();
        state.RecordQuestGreeting(Giver(1), new uint[] { 1392, 1389 });

        Assert.False(state.QuestGreetingHasOtherEntries(default, 1392));
    }

    [Fact]
    public void ClearQuestGreeting_AfterHello_NoOtherEntries()
    {
        // A new hello starts a new interaction: a single-quest dialog may follow instead of a list.
        var state = NewState();
        var giver = Giver(1);
        state.RecordQuestGreeting(giver, new uint[] { 1392, 1389 });

        state.ClearQuestGreeting();

        Assert.False(state.QuestGreetingHasOtherEntries(giver, 1392));
    }

    [Fact]
    public void RecordQuestGreeting_NewListReplacesOld()
    {
        var state = NewState();
        var giver = Giver(1);
        state.RecordQuestGreeting(giver, new uint[] { 1392, 1389 });

        state.RecordQuestGreeting(giver, new uint[] { 1389 });

        Assert.False(state.QuestGreetingHasOtherEntries(giver, 1389));
        Assert.True(state.QuestGreetingHasOtherEntries(giver, 1392));
    }
}
