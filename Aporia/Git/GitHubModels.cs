using System.Text.Json.Serialization;

namespace Aporia.Git;

internal record GitHubContent(
    string? Content,
    string Path,
    string? Type = null,
    [property: JsonPropertyName("git_url")] string? GitUrl = null);

internal record GitHubBlob(
    string? Content,
    string Encoding);

internal record GitHubPrResponse(
    string Title,
    string? Body,
    GitHubRef Head,
    GitHubRef Base);

internal record GitHubPrFile(
    string Filename,
    string Status,
    string? Patch,
    [property: JsonPropertyName("previous_filename")] string? PreviousFilename = null);

internal record GitHubCompareResponse(List<GitHubPrFile>? Files);

internal record GitHubTreeResponse(List<GitHubTreeEntry>? Tree);

internal record GitHubTreeEntry(
    string Path,
    string Type
);

internal record GitHubSearchResponse(List<GitHubSearchItem>? Items);

internal record GitHubSearchItem(
    string Name,
    string Path);

internal record GitHubCommit(GitHubCommitDetail Commit);

internal record GitHubCommitDetail(string Message);

internal record GitHubReviewComment(
    long Id,
    string? Body,
    string? Path = null,
    int? Line = null,
    [property: JsonPropertyName("in_reply_to_id")] long? InReplyToId = null);

internal record GitHubIssueComment(
    long Id,
    string? Body);
