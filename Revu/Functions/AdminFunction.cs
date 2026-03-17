using System.Net;
using System.Text.Json;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.WebJobs.Extensions.OpenApi.Core.Attributes;
using Microsoft.Azure.WebJobs.Extensions.OpenApi.Core.Enums;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;

using Revu.CodeGraph;
using Revu.Git;
using Revu.Infra;
using Revu.Infra.Cosmos;

namespace Revu.Functions;

public class AdminFunction(IRepoStore repoStore, IOptions<RevuOptions> options)
{
    [Function("RegisterRepo")]
    [OpenApiOperation(operationId: "RegisterRepo", tags: ["Admin"],
        Summary = "Register a repository",
        Description = "Registers a repository so Revu reviews its pull requests.")]
    [OpenApiSecurity("function_key", SecuritySchemeType.ApiKey, Name = "x-functions-key", In = OpenApiSecurityLocationType.Header)]
    [OpenApiRequestBody("application/json", typeof(RegisterRepoRequest), Description = "Repository to register. provider: 'ado' or 'github'.")]
    [OpenApiResponseWithBody(HttpStatusCode.OK, "application/json", typeof(Repository), Description = "Registered repository")]
    [OpenApiResponseWithBody(HttpStatusCode.BadRequest, "application/json", typeof(string), Description = "Validation error")]
    public async Task<RegisterRepoResponse> RegisterRepo(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "manage/repos")] HttpRequest req)
    {
        var body = await req.ReadFromJsonAsync<RegisterRepoRequest>();

        if (string.IsNullOrWhiteSpace(body?.RepositoryId) || string.IsNullOrWhiteSpace(body.Provider))
            return new RegisterRepoResponse { Result = new BadRequestObjectResult("repositoryId and provider are required.") };

        if (!Enum.TryParse<GitProvider>(body.Provider, ignoreCase: true, out var provider))
            return new RegisterRepoResponse { Result = new BadRequestObjectResult($"Unknown provider '{body.Provider}'. Valid values: ado, github.") };

        var repo = new Repository
        {
            Id = body.RepositoryId,
            Provider = provider,
            Enabled = true,
            Name = body.Name,
            Url = body.Url,
            Organization = body.Organization,
            CreatedAt = DateTimeOffset.UtcNow
        };

        var existing = await repoStore.GetAsync(body.RepositoryId);
        await repoStore.SaveAsync(repo);

        var branch = body.DefaultBranch ?? "refs/heads/main";
        var needsIndex = options.Value.EnableCodeGraph
            && (existing is null || existing.Provider != provider || body.ForceReindex == true);

        return new RegisterRepoResponse
        {
            Result = new OkObjectResult(repo),
            IndexMessage = needsIndex
                ? JsonSerializer.Serialize(new IndexRequest(repo.Id, body.Project ?? "", provider, branch))
                : null
        };
    }

    public record RegisterRepoRequest(
        string? RepositoryId,
        string? Provider,
        string? Name = null,
        string? Url = null,
        string? Organization = null,
        string? Project = null,
        string? DefaultBranch = null,
        bool? ForceReindex = null);
}

public class RegisterRepoResponse
{
    [HttpResult]
    public IActionResult Result { get; set; } = new OkResult();

    [QueueOutput("index-queue")]
    public string? IndexMessage { get; set; }
}
