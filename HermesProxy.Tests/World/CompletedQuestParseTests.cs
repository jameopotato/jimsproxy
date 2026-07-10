using System.Collections.Generic;
using HermesProxy.World.Server;
using Xunit;

namespace HermesProxy.Tests.World;

// Pins the defensive parsing of completed_quests.csv. The old code called uint.Parse on every
// line's first field, so a single corrupt line (a crash/kill mid-append can leave NUL bytes)
// threw FormatException from inside HandlePlayerLogin and terminated the whole proxy on login.
// The parser now skips corrupt lines and collapses duplicate quest IDs (repeatable turn-ins,
// e.g. Argent Dawn Scourgestone quests, append a row every completion).
public class CompletedQuestParseTests
{
    [Fact]
    public void ParseCompletedQuestLines_NulByteLine_SkippedAndFlaggedForRewrite()
    {
        var lines = new List<string> { "411,1780220603", "435,1780245929", new string('\0', 17) };

        var ids = AccountMetaDataManager.ParseCompletedQuestLines(lines, out var compact, out var needsRewrite);

        Assert.Equal(new uint[] { 411, 435 }, ids);
        Assert.Equal(new[] { "411,1780220603", "435,1780245929" }, compact);
        Assert.True(needsRewrite);
    }

    [Fact]
    public void ParseCompletedQuestLines_AllValidDistinct_NoRewrite()
    {
        var lines = new List<string> { "411,1", "435,2" };

        var ids = AccountMetaDataManager.ParseCompletedQuestLines(lines, out var compact, out var needsRewrite);

        Assert.Equal(new uint[] { 411, 435 }, ids);
        Assert.Equal(2, compact.Count);
        Assert.False(needsRewrite);
    }

    [Fact]
    public void ParseCompletedQuestLines_Duplicates_CollapsedToFirstSeen()
    {
        // Repeatable turn-in (Argent Dawn "Minion's Scourgestones" 5510) appended many times.
        var lines = new List<string> { "5510,100", "5510,101", "5510,102", "5508,103", "5510,104" };

        var ids = AccountMetaDataManager.ParseCompletedQuestLines(lines, out var compact, out var needsRewrite);

        Assert.Equal(new uint[] { 5510, 5508 }, ids);
        Assert.Equal(new[] { "5510,100", "5508,103" }, compact);
        Assert.True(needsRewrite);
    }

    [Fact]
    public void ParseCompletedQuestLines_BlankLines_NotTreatedAsCorruption()
    {
        var lines = new List<string> { "411,1", "", "  " };

        var ids = AccountMetaDataManager.ParseCompletedQuestLines(lines, out _, out var needsRewrite);

        Assert.Equal(new uint[] { 411 }, ids);
        Assert.False(needsRewrite);
    }
}
