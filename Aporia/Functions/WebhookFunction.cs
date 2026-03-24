using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.WebJobs.Extensions.OpenApi.Core.Attributes;
using Microsoft.Azure.WebJobs.Extensions.OpenApi.Core.Enums;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;

using Aporia.DocWatch;
using Aporia.Git;
using Aporia.Infra;
using Aporia.Infra.Cosmos;

namespace Aporia.Functions;

public class WebhookFunction(IRepoStore repoStore, IDocWatchStore docWatchStore, IOptions<GitHubOptions> gitHubOptions, IOptions<AporiaOptions> aporiaOptions)
{
    [Function("WebhookAdo")]
    [OpenApiOperation(operationId: "WebhookAdo", tags: ["Webhooks"],
        Summary = "Azure DevOps webhook",
        Description = "Receives PR created/updated events from Azure DevOps and enqueues a review.")]
    [OpenApiSecurity("function_key", SecuritySchemeType.ApiKey, Name = "x-functions-key", In = OpenApiSecurityLocationType.Header)]
    [OpenApiRequestBody("application/json", typeof(AdoWebhook), Description = "ADO service hook payload")]
    [OpenApiResponseWithoutBody(HttpStatusCode.OK, Description = "Accepted (review enqueued or ignored)")]
    public async Task<WebhookResponse> Run([HttpTrigger(AuthorizationLevel.Function, "post", Route = "webhook/ado")] HttpRequest req)
    {
        var webhook = await req.ReadFromJsonAsync<AdoWebhook>();

        if (webhook?.ToRequest() is not { } request
            || await GetOrganization(request.RepositoryId) is not { } org)
            return new WebhookResponse();

        request = request with { Organization = org };

        return new WebhookResponse { QueueMessage = JsonSerializer.Serialize(request) };
    }

    [Function("WebhookGitHub")]
    [OpenApiOperation(operationId: "WebhookGitHub", tags: ["Webhooks"],
        Summary = "GitHub webhook",
        Description = "Receives pull_request, comment, and issue_comment events from GitHub.")]
    [OpenApiResponseWithoutBody(HttpStatusCode.OK, Description = "Accepted (enqueued or ignored)")]
    public async Task<GitHubWebhookResponse> RunGitHub([HttpTrigger(AuthorizationLevel.Function, "post", Route = "webhook/github")] HttpRequest req)
    {
        if (!req.Headers.TryGetValue("X-GitHub-Event", out var eventHeader))
            return new GitHubWebhookResponse();

        var eventType = eventHeader.ToString();

        req.EnableBuffering();
        var body = await new StreamReader(req.Body).ReadToEndAsync();
        req.Body.Position = 0;

        // HMAC signature validation (GitHub signs payloads instead of using a function key)
        var secret = gitHubOptions.Value.WebhookSecret;
        if (!string.IsNullOrEmpty(secret))
        {
            if (!req.Headers.TryGetValue("X-Hub-Signature-256", out var signatureHeader)
                || signatureHeader.FirstOrDefault() is not { } sig
                || !sig.StartsWith("sha256="))
            {
                return new GitHubWebhookResponse { Result = new UnauthorizedResult() };
            }

            var expected = Convert.ToHexStringLower(
                HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body)));

            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(expected),
                    Encoding.UTF8.GetBytes(sig["sha256=".Length..])))
            {
                return new GitHubWebhookResponse { Result = new UnauthorizedResult() };
            }
        }

        return eventType switch
        {
            "pull_request" => await HandlePullRequest(body),
            "pull_request_review_comment" or "issue_comment" => await HandleComment(body, eventType),
            _ => new GitHubWebhookResponse()
        };
    }

    private async Task<GitHubWebhookResponse> HandlePullRequest(string body)
    {
        var webhook = JsonSerializer.Deserialize<GitHubWebhook>(body, JsonSerializerOptions.Web);
        if (webhook is null)
            return new GitHubWebhookResponse();

        var response = new GitHubWebhookResponse();

        // Review queue
        if (webhook.ToRequest() is { } request
            && await GetOrganization(request.RepositoryId) is { } org)
        {
            request = request with { Organization = org };
            response.ReviewQueueMessage = JsonSerializer.Serialize(request);
        }

        // Doc watch queue — route merged PRs to any doc watch projects watching this repo
        if (aporiaOptions.Value.EnableDocWatch
            && webhook.Action is "closed"
            && webhook.PullRequest is { Merged: true }
            && !IsDocWatchPr(webhook))
        {
            var repoId = webhook.Repository.FullName.Replace("/", "__");
            var projects = await docWatchStore.GetBySourceAsync(repoId);

            response.DocWatchQueueMessages = projects
                .Select(p => JsonSerializer.Serialize(new DocWatchRequest(
                    Provider: GitProvider.GitHub,
                    SourceRepo: repoId,
                    SourceRepoName: webhook.Repository.Name,
                    PullRequestId: webhook.PullRequest.Number,
                    SourceBranch: webhook.PullRequest.Head.Ref,
                    TargetBranch: webhook.PullRequest.Base.Ref,
                    DocsRepo: p.Id,
                    Organization: p.Organization,
                    InstallationId: webhook.Installation?.Id)))
                .ToList();
        }

        return response;
    }

    private static bool IsDocWatchPr(GitHubWebhook webhook) =>
        webhook.PullRequest.Head.Ref.StartsWith(DocWatchConstants.BranchPrefix)
        || webhook.PullRequest.Title?.StartsWith(DocWatchConstants.TitlePrefix, StringComparison.OrdinalIgnoreCase) == true;

    private async Task<GitHubWebhookResponse> HandleComment(string body, string eventType)
    {
        if (!aporiaOptions.Value.EnableChat)
            return new GitHubWebhookResponse();

        var webhook = JsonSerializer.Deserialize<GitHubCommentWebhook>(body, JsonSerializerOptions.Web);

        if (webhook?.ToChatRequest(eventType) is not { } chatRequest
            || await GetOrganization(chatRequest.Review.RepositoryId) is not { } org)
            return new GitHubWebhookResponse();

        chatRequest = chatRequest with { Review = chatRequest.Review with { Organization = org } };

        return new GitHubWebhookResponse { ChatQueueMessage = JsonSerializer.Serialize(chatRequest) };
    }

    [Function("WebhookAdoComment")]
    public async Task<ChatWebhookResponse> RunAdoComment([HttpTrigger(AuthorizationLevel.Function, "post", Route = "webhook/ado/comment")] HttpRequest req)
    {
        if (!aporiaOptions.Value.EnableChat)
            return new ChatWebhookResponse();

        var webhook = await req.ReadFromJsonAsync<AdoCommentWebhook>(JsonSerializerOptions.Web);

        if (webhook?.ToChatRequest() is not { } chatRequest
            || await GetOrganization(chatRequest.Review.RepositoryId) is not { } org)
            return new ChatWebhookResponse();

        chatRequest = chatRequest with { Review = chatRequest.Review with { Organization = org } };

        return new ChatWebhookResponse { QueueMessage = JsonSerializer.Serialize(chatRequest) };
    }

    private async Task<string?> GetOrganization(string repoId)
    {
        var repo = await repoStore.GetAsync(repoId);
        return repo is { Enabled: true, Organization: not null } ? repo.Organization : null;
    }
}

public class ChatWebhookResponse
{
    [HttpResult]
    public IActionResult Result { get; set; } = new OkResult();

    [QueueOutput("chat-queue")]
    public string? QueueMessage { get; set; }
}

public class GitHubWebhookResponse
{
    [HttpResult]
    public IActionResult Result { get; set; } = new OkResult();

    [QueueOutput("review-queue")]
    public string? ReviewQueueMessage { get; set; }

    [QueueOutput("chat-queue")]
    public string? ChatQueueMessage { get; set; }

    [QueueOutput("docwatch-queue")]
    public List<string>? DocWatchQueueMessages { get; set; }
}

public class WebhookResponse
{
    [HttpResult]
    public IActionResult Result { get; set; } = new OkResult();

    [QueueOutput("review-queue")]
    public string? QueueMessage { get; set; }
}
