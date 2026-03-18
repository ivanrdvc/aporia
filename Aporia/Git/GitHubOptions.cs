using System.ComponentModel.DataAnnotations;

namespace Aporia.Git;

public class GitHubOptions : IValidatableObject
{
    public const string SectionName = "GitHub";

    public string? WebhookSecret { get; init; }

    /// PAT-based auth (fallback). Required if App fields are not set.
    public string? Token { get; init; }

    /// GitHub App auth. Both must be set to use App identity.
    /// InstallationId comes from the webhook payload per-request, not config.
    public long? AppId { get; init; }
    public string? PrivateKey { get; init; }

    public bool UseApp => AppId is not null && !string.IsNullOrWhiteSpace(PrivateKey);

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!UseApp && string.IsNullOrWhiteSpace(Token))
            yield return new ValidationResult(
                "Either Token (PAT) or GitHub App fields (AppId, PrivateKey) must be configured.");
    }
}
