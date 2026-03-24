using System.Net;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.WebJobs.Extensions.OpenApi.Core.Attributes;
using Microsoft.Azure.WebJobs.Extensions.OpenApi.Core.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi.Models;

using Aporia.DocWatch;
using Aporia.Git;
using Aporia.Infra.Cosmos;

namespace Aporia.Functions;

public class DocWatchFunction(
    IDocWatchStore store,
    IServiceProvider sp,
    DocWatcher watcher,
    ILogger<DocWatchFunction> logger)
{
    [Function("RegisterDocWatch")]
    [OpenApiOperation(operationId: "RegisterDocWatch", tags: ["Admin"],
        Summary = "Register a doc watch project",
        Description = "Registers a docs repo and the source repos it watches.")]
    [OpenApiSecurity("function_key", SecuritySchemeType.ApiKey, Name = "x-functions-key", In = OpenApiSecurityLocationType.Header)]
    [OpenApiRequestBody("application/json", typeof(RegisterDocWatchRequest), Description = "Doc watch project to register.")]
    [OpenApiResponseWithBody(HttpStatusCode.OK, "application/json", typeof(DocWatchProject), Description = "Registered project")]
    public async Task<IActionResult> Register(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "manage/docwatch")] HttpRequest req)
    {
        var body = await req.ReadFromJsonAsync<RegisterDocWatchRequest>();

        if (string.IsNullOrWhiteSpace(body?.DocsRepo) || body.SourceRepos is not { Count: > 0 })
            return new BadRequestObjectResult("docsRepo and at least one sourceRepo are required.");

        if (!Enum.TryParse<GitProvider>(body.Provider, ignoreCase: true, out var provider))
            return new BadRequestObjectResult($"Unknown provider '{body.Provider}'. Valid values: ado, github.");

        var project = new DocWatchProject
        {
            Id = body.DocsRepo.Replace("/", "__"),
            SourceRepos = body.SourceRepos.Select(r => r.Replace("/", "__")).ToList(),
            Provider = provider,
            Organization = body.Organization ?? "",
            Enabled = true,
            CreatedAt = DateTimeOffset.UtcNow
        };

        await store.SaveAsync(project);

        return new OkObjectResult(project);
    }

    [Function("DeleteDocWatch")]
    [OpenApiOperation(operationId: "DeleteDocWatch", tags: ["Admin"],
        Summary = "Delete a doc watch project",
        Description = "Removes a doc watch project registration.")]
    [OpenApiSecurity("function_key", SecuritySchemeType.ApiKey, Name = "x-functions-key", In = OpenApiSecurityLocationType.Header)]
    public async Task<IActionResult> Delete(
        [HttpTrigger(AuthorizationLevel.Function, "delete", Route = "manage/docwatch/{docsRepo}")] HttpRequest req,
        string docsRepo)
    {
        await store.DeleteAsync(docsRepo.Replace("/", "__"));
        return new OkResult();
    }

    [Function("DocWatchProcessor")]
    public async Task Run([QueueTrigger("docwatch-queue")] DocWatchRequest req)
    {
        logger.LogInformation("Doc watch: processing PR #{PrId} from {Source} for docs repo {Docs}",
            req.PullRequestId, req.SourceRepo, req.DocsRepo);

        var git = sp.GetRequiredKeyedService<IGitConnector>(req.Provider);
        var publisher = sp.GetRequiredKeyedService<IDocPublisher>(req.Provider);

        var outcome = await watcher.Process(git, publisher, req);

        switch (outcome)
        {
            case DocWatchOutcome.Created c:
                logger.LogInformation("Doc watch: created PR #{DocPr} in {Docs}", c.PrNumber, req.DocsRepo);
                break;
            case DocWatchOutcome.Updated u:
                logger.LogInformation("Doc watch: updated PR #{DocPr} in {Docs}", u.PrNumber, req.DocsRepo);
                break;
            default:
                logger.LogInformation("Doc watch: no docs update needed for PR #{PrId}", req.PullRequestId);
                break;
        }
    }

    public record RegisterDocWatchRequest(
        string? DocsRepo,
        List<string>? SourceRepos,
        string? Provider,
        string? Organization = null);
}
