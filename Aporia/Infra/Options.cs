namespace Aporia.Infra;

public class AporiaOptions
{
    public const string SectionName = "Aporia";
    public bool EnableIncrementalReviews { get; init; }
    public bool EnableCodeGraph { get; init; } = true;
    public bool EnablePostComments { get; init; } = true;
    public bool EnableChat { get; init; }
    public bool EnableWorkItems { get; init; }
}

public static class SessionKeys
{
    public const string ConversationId = "aporia:conversationId";
    public const string PrContext = "aporia:prContext";
    public const string ProjectConfig = "aporia:projectConfig";
}
