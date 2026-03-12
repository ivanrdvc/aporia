namespace Revu.Tests.Unit;

public class ProjectConfigTests
{
    [Fact]
    public void Parse_Null_ReturnsDefault()
    {
        var settings = ProjectConfig.Parse(null);

        Assert.Same(ProjectConfig.Default, settings);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_EmptyOrWhitespace_ReturnsDefault(string raw)
    {
        var settings = ProjectConfig.Parse(raw);

        Assert.Same(ProjectConfig.Default, settings);
    }

    [Fact]
    public void Parse_EmptyJson_ReturnsDefault()
    {
        var settings = ProjectConfig.Parse("{}");

        Assert.Equal(ProjectConfig.Default.Review.Strategy, settings.Review.Strategy);
        Assert.Equal(ProjectConfig.Default.Review.MaxComments, settings.Review.MaxComments);
        Assert.Equal(ProjectConfig.Default.Files.AllowedExtensions, settings.Files.AllowedExtensions);
        Assert.Equal(ProjectConfig.Default.Files.Ignore, settings.Files.Ignore);
    }

    [Fact]
    public void Parse_OverridesStrategy()
    {
        var settings = ProjectConfig.Parse("""{ "review": { "strategy": "advanced" } }""");

        Assert.Equal("advanced", settings.Review.Strategy);
    }

    [Fact]
    public void Parse_OverridesMaxComments()
    {
        var settings = ProjectConfig.Parse("""{ "review": { "maxComments": 10 } }""");

        Assert.Equal(10, settings.Review.MaxComments);
    }

    [Fact]
    public void Parse_OverridesContext()
    {
        var settings = ProjectConfig.Parse("""{ "context": "This is a web API" }""");

        Assert.Equal("This is a web API", settings.Context);
    }

    [Fact]
    public void Parse_PartialOverride_PreservesOtherDefaults()
    {
        var settings = ProjectConfig.Parse("""{ "review": { "strategy": "advanced" } }""");

        Assert.Equal("advanced", settings.Review.Strategy);
        Assert.Equal(ProjectConfig.Default.Review.MaxComments, settings.Review.MaxComments);
        Assert.Equal(ProjectConfig.Default.Files.AllowedExtensions, settings.Files.AllowedExtensions);
    }

    [Fact]
    public void Parse_AllowedExtensions_MergesWithDefaults()
    {
        var settings = ProjectConfig.Parse("""{ "files": { "allowedExtensions": [".rs", ".rb"] } }""");

        var defaults = ProjectConfig.Default.Files.AllowedExtensions;
        Assert.Contains(".rs", settings.Files.AllowedExtensions);
        Assert.Contains(".rb", settings.Files.AllowedExtensions);
        foreach (var ext in defaults)
            Assert.Contains(ext, settings.Files.AllowedExtensions);
    }

    [Fact]
    public void Parse_Ignore_MergesWithDefaults()
    {
        var settings = ProjectConfig.Parse("""{ "files": { "ignore": ["**/dist/**"] } }""");

        var defaults = ProjectConfig.Default.Files.Ignore;
        Assert.Contains("**/dist/**", settings.Files.Ignore);
        foreach (var pattern in defaults)
            Assert.Contains(pattern, settings.Files.Ignore);
    }

    [Fact]
    public void Parse_Rules_MergesWithDefaults()
    {
        var settings = ProjectConfig.Parse("""{ "rules": ["no-magic-numbers", "prefer-const"] }""");

        Assert.Contains("no-magic-numbers", settings.Rules);
        Assert.Contains("prefer-const", settings.Rules);
    }

    [Fact]
    public void Parse_DuplicateExtensions_Deduplicated()
    {
        var settings = ProjectConfig.Parse("""{ "files": { "allowedExtensions": [".cs", ".rs"] } }""");

        var csCount = settings.Files.AllowedExtensions.Count(e => e == ".cs");
        Assert.Equal(1, csCount);
    }

    [Fact]
    public void Parse_InvalidJson_ReturnsDefault()
    {
        var settings = ProjectConfig.Parse("not json at all {{{");

        Assert.Same(ProjectConfig.Default, settings);
    }

    [Fact]
    public void Parse_CaseInsensitive()
    {
        var settings = ProjectConfig.Parse("""{ "Review": { "Strategy": "advanced" } }""");

        Assert.Equal("advanced", settings.Review.Strategy);
    }

    [Fact]
    public void Parse_AllowsTrailingCommas()
    {
        var settings = ProjectConfig.Parse("""{ "review": { "strategy": "advanced", }, }""");

        Assert.Equal("advanced", settings.Review.Strategy);
    }

    [Fact]
    public void Parse_AllowsComments()
    {
        var json = """
                   {
                       // this is a comment
                       "review": { "strategy": "advanced" }
                   }
                   """;
        var settings = ProjectConfig.Parse(json);

        Assert.Equal("advanced", settings.Review.Strategy);
    }

    [Fact]
    public void Default_HasExpectedValues()
    {
        var d = ProjectConfig.Default;

        Assert.Null(d.Review.Strategy);
        Assert.Equal(5, d.Review.MaxComments);
        Assert.NotEmpty(d.Files.AllowedExtensions);
        Assert.NotEmpty(d.Files.Ignore);
        Assert.Empty(d.Rules);
        Assert.Null(d.Context);
    }

    [Theory]
    [InlineData("src/Foo.cs")]
    [InlineData("src/Bar.ts")]
    [InlineData("src/components/App.tsx")]
    public void ShouldInclude_AllowedExtension_ReturnsTrue(string path)
    {
        Assert.True(ProjectConfig.Default.Files.ShouldInclude(path));
    }

    [Theory]
    [InlineData("src/readme.md")]
    [InlineData("src/image.png")]
    [InlineData("src/data.json")]
    public void ShouldInclude_DisallowedExtension_ReturnsFalse(string path)
    {
        Assert.False(ProjectConfig.Default.Files.ShouldInclude(path));
    }

    [Theory]
    [InlineData("src/bin/Debug/App.cs")]
    [InlineData("src/obj/Release/Generated.cs")]
    [InlineData("node_modules/package/index.js")]
    [InlineData("src/Foo.generated.cs")]
    [InlineData("vendor/lib/helper.js")]
    [InlineData("dist/bundle.min.js")]
    public void ShouldInclude_IgnoredPath_ReturnsFalse(string path)
    {
        Assert.False(ProjectConfig.Default.Files.ShouldInclude(path));
    }

    [Fact]
    public void ShouldInclude_NoIgnorePatterns_SkipsGlobCheck()
    {
        var files = new FileConfig
        {
            AllowedExtensions = [".cs"],
            Ignore = []
        };

        Assert.True(files.ShouldInclude("anything/Foo.cs"));
    }

    [Fact]
    public void ShouldInclude_CustomIgnore_Applies()
    {
        var files = new FileConfig
        {
            AllowedExtensions = [".cs"],
            Ignore = ["**/test/**"]
        };

        Assert.False(files.ShouldInclude("src/test/Foo.cs"));
        Assert.True(files.ShouldInclude("src/main/Foo.cs"));
    }
}
