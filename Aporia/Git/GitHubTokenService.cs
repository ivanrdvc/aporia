using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Aporia.Git;

/// <summary>
/// Acquires short-lived GitHub installation tokens (cached ~55 min).
/// Shared by <see cref="GitHubAuthHandler"/> (HTTP pipeline) and strategies that need
/// a token outside the HTTP pipeline (e.g. git clone).
/// </summary>
public class GitHubTokenService(IHttpClientFactory httpClientFactory)
{
    internal const string TokenClientName = "github-token";

    private static readonly ConcurrentDictionary<long, CachedToken> TokenCache = new();
    private static readonly TimeSpan TokenTtl = TimeSpan.FromMinutes(55);

    public async Task<string> GetInstallationTokenAsync(GitHubOptions config, long installationId)
    {
        if (TokenCache.TryGetValue(installationId, out var cached) && cached.ExpiresAt > DateTime.UtcNow)
            return cached.Token;

        var jwt = GenerateJwt(config.AppId!.Value, config.PrivateKey!);

        var client = httpClientFactory.CreateClient(TokenClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"app/installations/{installationId}/access_tokens");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        using var response = await client.SendAsync(request);
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

    private record InstallationTokenResponse([property: JsonPropertyName("token")] string Token);
}
