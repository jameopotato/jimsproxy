using System;
using System.Collections.Generic;
using System.IO;
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

    // --- Torn-append (glued-line) guard: MarkQuestAsCompleted -> EnsureTrailingNewline ---
    // A kill mid-append (the launcher force-kills the proxy on game close) can leave the file
    // without a trailing newline; the next append then glues onto the partial last line. The glued
    // line's first field still parses, so the second quest id is SILENTLY lost and needsRewrite
    // stays false -- invisible to the self-heal. These pin the vector and the guard.

    [Fact]
    public void ParseCompletedQuestLines_GluedLine_SilentlyLosesSecondId_DocumentsTheVector()
    {
        // "100,1780000000" (no newline) + "200,1780000005" appended -> one glued line.
        var glued = new List<string> { "100,1780000000200,1780000005" };

        var ids = AccountMetaDataManager.ParseCompletedQuestLines(glued, out _, out var needsRewrite);

        Assert.Equal(new uint[] { 100 }, ids);   // 200 is swallowed into the middle field
        Assert.False(needsRewrite);              // and the loss is invisible to the self-heal
    }

    [Fact]
    public void EnsureTrailingNewline_MissingTrailingNewline_PreventsGluedAppend()
    {
        var path = NewTempFile();
        try
        {
            // Simulate a torn prior append: last line has no terminating newline.
            File.WriteAllText(path, "100,1780000000");

            AccountMetaDataManager.EnsureTrailingNewline(path);
            File.AppendAllLines(path, new[] { "200,1780000005" });

            var ids = AccountMetaDataManager.ParseCompletedQuestLines(File.ReadAllLines(path), out _, out _);
            Assert.Equal(new uint[] { 100, 200 }, ids);   // both survive -- no glue
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void EnsureTrailingNewline_AlreadyNewlineTerminated_LeavesContentUnchanged()
    {
        var path = NewTempFile();
        try
        {
            var original = "100,1780000000" + Environment.NewLine;
            File.WriteAllText(path, original);

            AccountMetaDataManager.EnsureTrailingNewline(path);

            Assert.Equal(original, File.ReadAllText(path));   // idempotent: no spurious blank line
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void EnsureTrailingNewline_MissingOrEmptyFile_NoOpNoThrow()
    {
        var missing = NewTempFile();
        AccountMetaDataManager.EnsureTrailingNewline(missing);   // absent -> no-op, no throw
        Assert.False(File.Exists(missing));

        var empty = NewTempFile();
        File.Create(empty).Dispose();                           // 0-byte file
        try
        {
            AccountMetaDataManager.EnsureTrailingNewline(empty);
            Assert.Equal(0, new FileInfo(empty).Length);        // stays empty, no lone newline
        }
        finally { File.Delete(empty); }
    }

    private static string NewTempFile()
        => Path.Combine(Path.GetTempPath(), $"jimsproxy_cq_{Guid.NewGuid():N}.csv");
}
