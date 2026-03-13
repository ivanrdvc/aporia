using System.Net;
using System.Text.Json;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.WebJobs.Extensions.OpenApi.Core.Attributes;
using Microsoft.Azure.WebJobs.Extensions.OpenApi.Core.Enums;
using Microsoft.OpenApi.Models;

using Revu.Git;
using Revu.Infra.Cosmos;

namespace Revu.Functions;

public class WebhookFunction(IRepoStore repoStore)
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
}

public class WebhookResponse
{
    [HttpResult]
    public IActionResult Result { get; set; } = new OkResult();

    [QueueOutput("review-queue")]
    public string? QueueMessage { get; set; }
}
