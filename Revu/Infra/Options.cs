namespace Revu.Infra;

public class RevuOptions
{
    public const string SectionName = "Revu";
    public bool IncrementalReviews { get; init; }
}

public static class SessionKeys
{
    public const string ConversationId = "revu:conversationId";
    public const string PrContext = "revu:prContext";
    public const string ProjectConfig = "revu:projectConfig";
}
