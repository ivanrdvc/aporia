using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

using Aporia.Git;

namespace Aporia.Cli;

internal static partial class PrRecreator
{
    /// <summary>
    /// Abandons/closes the current PR, creates a new temp branch from the same source,
    /// opens a new PR, and returns the new PR ID.
    /// </summary>
    public static async Task<int> RecreateAsync(ParsedPrUrl parsed)
    {
        return parsed.Provider switch
        {
            GitProvider.Ado => await RecreateAdoAsync(parsed),
            GitProvider.GitHub => await RecreateGitHubAsync(parsed),
            _ => throw new NotSupportedException($"Provider {parsed.Provider} not supported for --clean")
        };
    }

    private static async Task<int> RecreateAdoAsync(ParsedPrUrl parsed)
    {
        var org = $"https://dev.azure.com/{parsed.Owner}";
        var project = parsed.Project!;

        // Get current PR's source branch to find the permanent branch
        var prJson = await RunAsync("az", $"repos pr show --id {parsed.PullRequestId} --org \"{org}\" --query \"{{source: sourceRefName, repo: repository.id}}\" -o json");
        var prInfo = JsonDocument.Parse(prJson).RootElement;
        var currentSource = prInfo.GetProperty("source").GetString()!;
        var repoId = prInfo.GetProperty("repo").GetString()!;

        // The permanent source branch — if this is already a temp branch, get the original
        var sourceBranch = currentSource.Contains("aporia-test-")
            ? "refs/heads/feature/order-tracking-notifications"
            : currentSource;

        // Abandon old PR (ignore if already abandoned/completed)
        try { await RunAsync("az", $"repos pr update --id {parsed.PullRequestId} --status abandoned --org \"{org}\""); }
        catch { /* already abandoned or completed */ }

        // Delete old temp branch if it was one
        if (currentSource.Contains("aporia-test-"))
        {
            var sha = await RunAsync("az", $"repos ref list --repository \"{repoId}\" --org \"{org}\" --project \"{project}\" --filter \"{currentSource.Replace("refs/", "")}\" --query \"[0].objectId\" -o tsv");
            sha = sha.Trim();
            if (!string.IsNullOrEmpty(sha))
                await RunAsync("az", $"repos ref delete --name \"{currentSource}\" --object-id \"{sha}\" --repository \"{repoId}\" --org \"{org}\" --project \"{project}\"");
        }

        // Get SHA of permanent source branch
        var sourceSha = (await RunAsync("az",
            $"repos ref list --repository \"{repoId}\" --org \"{org}\" --project \"{project}\" --filter \"{sourceBranch.Replace("refs/", "")}\" --query \"[0].objectId\" -o tsv")).Trim();

        // Create new temp branch
        var branch = $"aporia-test-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        await RunAsync("az", $"repos ref create --name \"refs/heads/{branch}\" --object-id \"{sourceSha}\" --repository \"{repoId}\" --org \"{org}\" --project \"{project}\"");

        // Create new PR
        var newPrJson = await RunAsync("az",
            $"repos pr create --repository \"{repoId}\" --source-branch \"{branch}\" --target-branch main --title \"aporia-test-{branch}\" --org \"{org}\" --project \"{project}\" --query pullRequestId -o tsv");

        return int.Parse(newPrJson.Trim());
    }

    private static async Task<int> RecreateGitHubAsync(ParsedPrUrl parsed)
    {
        var owner = parsed.Owner;
        var repo = parsed.RepoName;

        // Get current PR's head branch
        var prJson = await RunAsync("gh", $"api repos/{owner}/{repo}/pulls/{parsed.PullRequestId} --jq \"{{head: .head.ref, base: .base.ref}}\"");
        var prInfo = JsonDocument.Parse(prJson).RootElement;
        var currentHead = prInfo.GetProperty("head").GetString()!;

        var sourceBranch = currentHead.Contains("aporia-test-")
            ? "feature/order-tracking-notifications"
            : currentHead;

        // Close old PR
        await RunAsync("gh", $"pr close {parsed.PullRequestId} --repo {owner}/{repo} --delete-branch");

        // Get SHA of permanent source branch
        var sha = (await RunAsync("gh", $"api repos/{owner}/{repo}/git/ref/heads/{sourceBranch} --jq .object.sha")).Trim();

        // Create new temp branch
        var branch = $"aporia-test-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        await RunAsync("gh", $"api repos/{owner}/{repo}/git/refs -f ref=refs/heads/{branch} -f sha={sha}");

        // Create PR and extract number
        var output = await RunAsync("gh", $"pr create --repo {owner}/{repo} --head {branch} --base main --title \"aporia-test-{branch}\" --body \"Automated test PR\"");
        var match = Regex.Match(output, @"(\d+)\s*$");
        if (!match.Success)
            throw new InvalidOperationException($"Could not parse PR number from: {output}");

        return int.Parse(match.Groups[1].Value);
    }

    private static async Task<string> RunAsync(string command, string arguments)
    {
        var psi = new ProcessStartInfo(command, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var process = Process.Start(psi)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{command} failed (exit {process.ExitCode}): {error}");

        return output;
    }
}
