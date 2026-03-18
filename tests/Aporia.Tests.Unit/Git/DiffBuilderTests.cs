using Aporia.Git;

namespace Aporia.Tests.Unit.Git;

public class DiffBuilderTests
{
    [Fact]
    public void NewFile_AllLinesMarkedAdded()
    {
        var content = "line one\nline two\nline three";

        var result = DiffBuilder.NewFile(content);

        Assert.Contains("@@ new file @@", result);
        Assert.Contains("+ 1:", result);
        Assert.Contains("+ 2:", result);
        Assert.Contains("+ 3:", result);
    }

    [Fact]
    public void Hunks_NoChanges_ReturnsNoChangesMessage()
    {
        var content = "line one\nline two";

        var result = DiffBuilder.Hunks(content, content);

        Assert.Equal("(no changes detected)", result.Trim());
    }

    [Fact]
    public void Hunks_AddedLine_MarkedWithPlus()
    {
        var base_ = "line one\nline two";
        var target = "line one\nnew line\nline two";

        var result = DiffBuilder.Hunks(base_, target);

        Assert.Contains("+ ", result);
        Assert.Contains("new line", result);
    }

    [Fact]
    public void Hunks_DeletedLine_MarkedWithMinus()
    {
        var base_ = "line one\nremoved line\nline two";
        var target = "line one\nline two";

        var result = DiffBuilder.Hunks(base_, target);

        Assert.Contains("- ", result);
        Assert.Contains("removed line", result);
    }

    [Fact]
    public void Hunks_SummaryLine_ShowsCorrectCounts()
    {
        var base_ = "a\nb\nc";
        var target = "a\nX\nY\nc"; // +2 added, -1 deleted (b → X, Y inserted)

        var result = DiffBuilder.Hunks(base_, target);

        var summary = result.Split('\n')[0];
        Assert.Contains("+2", summary);
        Assert.Contains("-1", summary);
    }

    [Fact]
    public void Hunks_HunkHeader_ContainsStandardFormat()
    {
        var base_ = "a\nb\nc";
        var target = "a\nX\nc";

        var result = DiffBuilder.Hunks(base_, target);

        // Header must match @@ -N,N +N,N @@
        var headerLine = result.Split('\n').First(l => l.StartsWith("@@"));
        Assert.Matches(@"^@@ -\d+,\d+ \+\d+,\d+ @@$", headerLine.Trim());
    }

    [Fact]
    public void Hunks_ContextLines_IncludedAroundChanges()
    {
        var lines = Enumerable.Range(1, 30).Select(i => $"content {i}").ToArray();
        var base_ = string.Join("\n", lines);
        var modified = lines.ToArray();
        modified[14] = "changed";  // line 15 (1-indexed)
        var target = string.Join("\n", modified);

        var result = DiffBuilder.Hunks(base_, target, context: 3);

        // Lines 12-14 and 16-18 should appear as context (±3 around line 15)
        Assert.Contains(" 12: content 12", result);
        Assert.Contains(" 14: content 14", result);
        Assert.Contains("+ 15: changed", result);
        Assert.Contains(" 16: content 16", result);
        Assert.Contains(" 18: content 18", result);
        // Lines far from change should NOT appear
        Assert.DoesNotContain(": content 1\r", result);
        Assert.DoesNotContain(": content 1\n", result);
        Assert.DoesNotContain(": content 30", result);
    }

    [Fact]
    public void Hunks_AdjacentChanges_MergedIntoSingleHunk()
    {
        var lines = Enumerable.Range(1, 50).Select(i => $"line {i}").ToArray();
        var modified = lines.ToArray();
        modified[19] = "changed A";  // line 20
        modified[20] = "changed B";  // line 21
        var base_ = string.Join("\n", lines);
        var target = string.Join("\n", modified);

        var result = DiffBuilder.Hunks(base_, target, context: 2);

        var hunkCount = result.Split('\n').Count(l => l.TrimEnd().StartsWith("@@"));
        Assert.Equal(1, hunkCount);
    }

    [Fact]
    public void Hunks_DistantChanges_ProduceSeparateHunks()
    {
        var lines = Enumerable.Range(1, 100).Select(i => $"line {i}").ToArray();
        var modified = lines.ToArray();
        modified[4] = "changed early";   // line 5
        modified[94] = "changed late";   // line 95
        var base_ = string.Join("\n", lines);
        var target = string.Join("\n", modified);

        var result = DiffBuilder.Hunks(base_, target, context: 2);

        var hunkCount = result.Split('\n').Count(l => l.TrimEnd().StartsWith("@@"));
        Assert.Equal(2, hunkCount);
    }
}
