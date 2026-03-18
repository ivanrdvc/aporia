using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Revu.Git;

/// <summary>
/// HTTP pipeline handler that authenticates GitHub API requests.
/// PAT mode: sets a static Bearer token. App mode: exchanges a JWT for a short-lived
/// installation token (cached ~55 min) using the InstallationId from request options.
/// </summary>
public class GitHubAuthHandler(
    IHttpClientFactory httpClientFactory,
    IOptions<GitHubOptions> options) : DelegatingHandler
{
    internal static readonly HttpRequestOptionsKey<long> InstallationIdKey = new("GitHub.InstallationId");
    internal const string TokenClientName = "github-token";

    private static readonly ConcurrentDictionary<long, CachedToken> TokenCache = new();
    private static readonly TimeSpan TokenTtl = TimeSpan.FromMinutes(55);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var config = options.Value;

        if (config.UseApp && request.Options.TryGetValue(InstallationIdKey, out var installationId))
        {
            var token = await GetInstallationTokenAsync(config, installationId);
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

    private async Task<string> GetInstallationTokenAsync(GitHubOptions config, long installationId)
    {
        if (TokenCache.TryGetValue(installationId, out var cached) && cached.ExpiresAt > DateTime.UtcNow)
            return cached.Token;

        var jwt = GenerateJwt(config.AppId!.Value, config.PrivateKey!);

        var client = httpClientFactory.CreateClient(TokenClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"app/installations/{installationId}/access_tokens");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<InstallationTokenResponse>();
        var token = result!.Token;

        TokenCache[installationId] = new CachedToken(token, DateTime.UtcNow.Add(TokenTtl));
        return token;
    }

    internal static string GenerateJwt(long appId, string privateKeyPem)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);

        var now = DateTime.UtcNow.AddSeconds(-60); // clock drift buffer
        var credentials = new SigningCredentials(new RsaSecurityKey(rsa), SecurityAlgorithms.RsaSha256);

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = appId.ToString(),
            IssuedAt = now,
            Expires = now.AddMinutes(10),
            SigningCredentials = credentials
        });
    }

    private record CachedToken(string Token, DateTime ExpiresAt);

    private record InstallationTokenResponse(
        [property: JsonPropertyName("token")] string Token);
}
