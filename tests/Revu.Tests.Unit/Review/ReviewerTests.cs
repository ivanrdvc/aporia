using Revu.Review;

namespace Revu.Tests.Unit.Review;

public class ReviewerTests
{
    [Theory]
    [InlineData("src/Foo.cs", "src/Foo.cs")]
    [InlineData("/src/Foo.cs", "src/Foo.cs")]
    [InlineData("///src/Foo.cs", "src/Foo.cs")]
    public void NormalizePath_LeadingSlashes_Stripped(string input, string expected)
    {
        Assert.Equal(expected, Reviewer.NormalizePath(input));
    }

    [Theory]
    [InlineData("a/src/Foo.cs", "src/Foo.cs")]
    [InlineData("b/src/Foo.cs", "src/Foo.cs")]
    [InlineData("U/src/Foo.cs", "src/Foo.cs")]
    public void NormalizePath_GitDiffPrefix_Stripped(string input, string expected)
    {
        Assert.Equal(expected, Reviewer.NormalizePath(input));
    }

    [Fact]
    public void NormalizePath_Spaces_Removed()
    {
        Assert.Equal("src/Foo.cs", Reviewer.NormalizePath(" src / Foo.cs "));
    }

    [Fact]
    public void NormalizePath_ShortPath_NotTruncated()
    {
        Assert.Equal("a", Reviewer.NormalizePath("a"));
    }

    [Fact]
    public void NormalizePath_TwoCharPath_NotTruncated()
    {
        Assert.Equal("ab", Reviewer.NormalizePath("ab"));
    }
}
