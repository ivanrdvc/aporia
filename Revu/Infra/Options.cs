namespace Revu.Infra;

public class RevuOptions
{
    public const string SectionName = "Revu";
    public bool EnableIncrementalReviews { get; init; }
    public bool EnableCodeGraph { get; init; } = true;
    public bool EnableChat { get; init; }
    public bool EnableDevQueue { get; init; }
}

public static class SessionKeys
{
    public const string ConversationId = "revu:conversationId";
    public const string PrContext = "revu:prContext";
    public const string ProjectConfig = "revu:projectConfig";
}
