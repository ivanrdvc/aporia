using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Aporia.Git;
using Aporia.Infra.Cosmos;

namespace Aporia.CodeGraph;

public class CodeGraphIndexer(
    IServiceProvider sp,
    ICodeGraphStore store,
    IRepoStore repoStore,
    IEnumerable<ILanguageParser> parsers,
    ILogger<CodeGraphIndexer> logger)
{
    private const int MaxFileSize = 512_000;
    private const int MaxFiles = 5_000;

    public async Task IndexAsync(IndexRequest req, CancellationToken ct)
    {
        using var scope = sp.CreateScope();
        var git = scope.ServiceProvider.GetRequiredKeyedService<IGitConnector>(req.Provider);
        var repo = await repoStore.GetAsync(req.RepositoryId);
        var organization = repo?.Organization ?? "";

        var syntheticReq = new ReviewRequest(
            req.Provider, req.Project, req.RepositoryId,
            req.RepositoryId, PullRequestId: 0,
            SourceBranch: req.Branch, TargetBranch: req.Branch,
            Organization: organization);

        var allFiles = await git.ListFiles(syntheticReq, "/", recursive: true);

        var parseableFiles = allFiles
            .Where(f => parsers.Any(p => p.CanParse(f)))
            .Take(MaxFiles)
            .ToList();

        if (parseableFiles.Count == MaxFiles)
            logger.LogWarning("Repository {RepoId} has more than {Max} parseable files, capping index", req.RepositoryId, MaxFiles);

        var existingByPath = (await store.GetAllAsync(req.RepositoryId))
            .ToDictionary(f => f.Id);

        var unchanged = new ConcurrentBag<FileIndex>();
        var changed = new ConcurrentBag<FileIndex>();
        var parserList = parsers.ToList();

        await Parallel.ForEachAsync(parseableFiles, new ParallelOptions { MaxDegreeOfParallelism = 10, CancellationToken = ct },
            async (filePath, token) =>
            {
                try
                {
                    var content = await git.GetFile(syntheticReq, filePath);
                    if (content is null || content.Length > MaxFileSize) return;

                    var hash = ComputeHash(content);
                    var docId = filePath.Replace('/', '|');

                    if (existingByPath.TryGetValue(docId, out var existingDoc) && existingDoc.ContentHash == hash)
                    {
                        unchanged.Add(existingDoc);
                        return;
                    }

                    var parser = parserList.FirstOrDefault(p => p.CanParse(filePath));
                    if (parser is null) return;

                    var (symbols, refs) = parser.Parse(content, filePath);

                    changed.Add(new FileIndex
                    {
                        Id = docId,
                        RepoId = req.RepositoryId,
                        Branch = req.Branch,
                        Language = parser.Language,
                        ContentHash = hash,
                        Symbols = symbols,
                        References = refs
                    });
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to index {FilePath}", filePath);
                }
            });

        var indexedFiles = changed.Concat(unchanged).ToList();

        ResolveCrossFileReferences(indexedFiles);

        await Parallel.ForEachAsync(changed, new ParallelOptions { MaxDegreeOfParallelism = 10, CancellationToken = ct },
            async (file, _) => await store.UpsertFileAsync(file));

        var indexedPaths = new HashSet<string>(indexedFiles.Select(f => f.Id));
        await store.DeleteOrphansAsync(req.RepositoryId, indexedPaths, ct);

        logger.LogInformation("Indexed {Count} files for {RepoId}", indexedFiles.Count, req.RepositoryId);
    }

    private static void ResolveCrossFileReferences(List<FileIndex> files)
    {
        var symbolMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            foreach (var symbol in file.Symbols)
            {
                if (!symbolMap.TryGetValue(symbol.Name, out var paths))
                {
                    paths = [];
                    symbolMap[symbol.Name] = paths;
                }
                paths.Add(file.Id);
            }
        }

        foreach (var file in files)
        {
            for (var i = 0; i < file.References.Count; i++)
            {
                var reference = file.References[i];
                if (reference.Kind is not ("calls" or "implements")) continue;

                var target = reference.Target;
                string? resolvedTarget = null;

                if (target.Contains('.'))
                {
                    var typeName = target.Split('.')[0];
                    if (symbolMap.TryGetValue(typeName, out var typePaths))
                    {
                        var resolvedFile = typePaths.FirstOrDefault(p => p != file.Id) ?? typePaths[0];
                        resolvedTarget = $"{resolvedFile.Replace('|', '/')}:{target}";
                    }
                }
                else if (symbolMap.TryGetValue(target, out var targetPaths))
                {
                    var resolvedFile = targetPaths.FirstOrDefault(p => p != file.Id) ?? targetPaths[0];
                    resolvedTarget = $"{resolvedFile.Replace('|', '/')}:{target}";
                }

                if (resolvedTarget is not null)
                {
                    file.References[i] = new SymbolReference
                    {
                        Target = resolvedTarget,
                        Kind = reference.Kind,
                        Line = reference.Line
                    };
                }
            }
        }
    }

    private static string ComputeHash(string content)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexStringLower(hash);
    }
}
