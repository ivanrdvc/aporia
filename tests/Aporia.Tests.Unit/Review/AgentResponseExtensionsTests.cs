using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

using NSubstitute;

using Aporia.Review;

namespace Aporia.Tests.Unit.Review;

public class AgentResponseExtensionsTests
{
    private readonly ILogger _logger = Substitute.For<ILogger>();

    [Fact]
    public void ExtractResult_ValidJson_ReturnsDeserialized()
    {
        var response = MakeResponse("""{"Name":"test","Value":1}""");

        var result = response.ExtractResult<Dto>(_logger);

        Assert.NotNull(result);
        Assert.Equal("test", result!.Name);
    }

    [Fact]
    public void ExtractResult_EmptyMessages_ReturnsNull()
    {
        var response = new AgentResponse();

        var result = response.ExtractResult<Dto>(_logger);

        Assert.Null(result);
    }

    [Fact]
    public void ExtractResult_InvalidJson_ReturnsNull()
    {
        var response = MakeResponse("not json at all");

        var result = response.ExtractResult<Dto>(_logger);

        Assert.Null(result);
    }

    [Fact]
    public void ExtractResult_UsesLastMessage()
    {
        var response = new AgentResponse();
        response.Messages.Add(new ChatMessage(ChatRole.Assistant, "garbage"));
        response.Messages.Add(new ChatMessage(ChatRole.Assistant, """{"Name":"last","Value":2}"""));

        var result = response.ExtractResult<Dto>(_logger);

        Assert.NotNull(result);
        Assert.Equal("last", result!.Name);
    }

    private static AgentResponse MakeResponse(string text)
    {
        var response = new AgentResponse();
        response.Messages.Add(new ChatMessage(ChatRole.Assistant, text));
        return response;
    }

    private record Dto(string Name, int Value);
}
