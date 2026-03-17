namespace Revu.Tests.Unit.Git;

public class GitHubConnectorTests
{
    [Fact]
    public void Fingerprint_SameFinding_ReturnsSameHash()
    {
        var finding = new Finding("src/Foo.cs", 10, 15, Severity.Critical, "Null ref here");

        var a = Finding.Fingerprint(finding);
        var b = Finding.Fingerprint(finding);

        Assert.Equal(a, b);
    }

    [Fact]
    public void Fingerprint_DifferentLines_ReturnsSameHash()
    {
        var a = new Finding("src/Foo.cs", 10, 15, Severity.Critical, "Null ref here");
        var b = new Finding("src/Foo.cs", 20, 25, Severity.Critical, "Null ref here");

        Assert.Equal(Finding.Fingerprint(a), Finding.Fingerprint(b));
    }

    [Fact]
    public void Fingerprint_DifferentSeverity_ReturnsSameHash()
    {
        var a = new Finding("src/Foo.cs", 10, 15, Severity.Critical, "Null ref here");
        var b = new Finding("src/Foo.cs", 10, 15, Severity.Info, "Null ref here");

        Assert.Equal(Finding.Fingerprint(a), Finding.Fingerprint(b));
    }

    [Fact]
    public void Fingerprint_DifferentMessage_ReturnsDifferentHash()
    {
        var a = new Finding("src/Foo.cs", 10, 15, Severity.Critical, "Null ref here");
        var b = new Finding("src/Foo.cs", 10, 15, Severity.Critical, "Race condition");

        Assert.NotEqual(Finding.Fingerprint(a), Finding.Fingerprint(b));
    }

    [Fact]
    public void Fingerprint_DifferentFile_ReturnsDifferentHash()
    {
        var a = new Finding("src/Foo.cs", 10, 15, Severity.Critical, "Null ref here");
        var b = new Finding("src/Bar.cs", 10, 15, Severity.Critical, "Null ref here");

        Assert.NotEqual(Finding.Fingerprint(a), Finding.Fingerprint(b));
    }

    [Fact]
    public void Fingerprint_CaseInsensitive()
    {
        var a = new Finding("src/Foo.cs", 10, 15, Severity.Critical, "Null Ref Here");
        var b = new Finding("SRC/FOO.CS", 10, 15, Severity.Critical, "null ref here");

        Assert.Equal(Finding.Fingerprint(a), Finding.Fingerprint(b));
    }

    [Fact]
    public void Fingerprint_LeadingSlashIgnored()
    {
        var a = new Finding("/src/Foo.cs", 10, 15, Severity.Critical, "Null ref");
        var b = new Finding("src/Foo.cs", 10, 15, Severity.Critical, "Null ref");

        Assert.Equal(Finding.Fingerprint(a), Finding.Fingerprint(b));
    }

    [Fact]
    public void Fingerprint_WhitespaceTrimmed()
    {
        var a = new Finding("src/Foo.cs", 10, 15, Severity.Critical, "  Null ref  ");
        var b = new Finding("src/Foo.cs", 10, 15, Severity.Critical, "Null ref");

        Assert.Equal(Finding.Fingerprint(a), Finding.Fingerprint(b));
    }

}
