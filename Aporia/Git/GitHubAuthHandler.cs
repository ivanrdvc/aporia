using System.Net.Http.Headers;

using Microsoft.Extensions.Options;

namespace Aporia.Git;

/// <summary>
/// HTTP pipeline handler that authenticates GitHub API requests.
/// PAT mode: sets a static Bearer token. App mode: delegates to <see cref="GitHubTokenService"/>
/// for a cached installation token using the InstallationId from request options.
/// </summary>
public class GitHubAuthHandler(
    GitHubTokenService tokenService,
    IOptions<GitHubOptions> options) : DelegatingHandler
{
    internal static readonly HttpRequestOptionsKey<long> InstallationIdKey = new("GitHub.InstallationId");

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var config = options.Value;

        if (config.UseApp && request.Options.TryGetValue(InstallationIdKey, out var installationId))
        {
            var token = await tokenService.GetInstallationTokenAsync(config, installationId);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        else if (!string.IsNullOrWhiteSpace(config.Token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.Token);
        }
        else
        {
            throw new InvalidOperationException(
                "No GitHub auth available. Provide InstallationId (App mode) or configure a PAT.");
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
