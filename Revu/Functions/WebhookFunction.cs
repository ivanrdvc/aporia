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

using Revu.Git;
using Revu.Infra.Cosmos;

namespace Revu.Functions;

public class WebhookFunction(IRepoStore repoStore, IOptions<GitHubOptions> gitHubOptions)
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

        if (webhook?.ToRequest() is not { } request)
            return new WebhookResponse();

        var repo = await repoStore.GetAsync(request.RepositoryId);

        if (repo is not { Enabled: true, Organization: not null })
            return new WebhookResponse();

        request = request with { Organization = repo.Organization };

        return new WebhookResponse { QueueMessage = JsonSerializer.Serialize(request) };
    }

    [Function("WebhookGitHub")]
    [OpenApiOperation(operationId: "WebhookGitHub", tags: ["Webhooks"],
        Summary = "GitHub webhook",
        Description = "Receives pull_request events from GitHub and enqueues a review.")]
    [OpenApiResponseWithoutBody(HttpStatusCode.OK, Description = "Accepted (review enqueued or ignored)")]
    public async Task<WebhookResponse> RunGitHub([HttpTrigger(AuthorizationLevel.Function, "post", Route = "webhook/github")] HttpRequest req)
    {
        if (!req.Headers.TryGetValue("X-GitHub-Event", out var eventHeader) || eventHeader != "pull_request")
            return new WebhookResponse();

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
                return new WebhookResponse { Result = new UnauthorizedResult() };
            }

            var expected = Convert.ToHexStringLower(
                HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body)));

            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(expected),
                    Encoding.UTF8.GetBytes(sig["sha256=".Length..])))
            {
                return new WebhookResponse { Result = new UnauthorizedResult() };
            }
        }

        var webhook = JsonSerializer.Deserialize<GitHubWebhook>(body, JsonSerializerOptions.Web);

        if (webhook?.ToRequest() is not { } request)
            return new WebhookResponse();

        var repo = await repoStore.GetAsync(request.RepositoryId);

        if (repo is not { Enabled: true, Organization: not null })
            return new WebhookResponse();

        request = request with { Organization = repo.Organization };

        return new WebhookResponse { QueueMessage = JsonSerializer.Serialize(request) };
    }
}

public class WebhookResponse
{
    [HttpResult]
    public IActionResult Result { get; set; } = new OkResult();

    [QueueOutput("review-queue")]
    public string? QueueMessage { get; set; }
}
