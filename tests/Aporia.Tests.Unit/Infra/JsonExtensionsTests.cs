using Aporia.Infra;

namespace Aporia.Tests.Unit.Infra;

public class JsonExtensionsTests
{
    [Fact]
    public void TryParseJson_ValidJson_ReturnsObject()
    {
        var json = """{"Name":"Alice","Value":42}""";

        var result = json.TryParseJson<TestDto>();

        Assert.NotNull(result);
        Assert.Equal("Alice", result.Name);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public void TryParseJson_InvalidJson_ReturnsNull()
    {
        var result = "not json".TryParseJson<TestDto>();

        Assert.Null(result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void TryParseJson_NullOrWhitespace_ReturnsNull(string? input)
    {
        var result = input.TryParseJson<TestDto>();

        Assert.Null(result);
    }

    private record TestDto(string Name, int Value);
}
