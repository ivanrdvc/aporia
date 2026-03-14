using System.Text;

namespace Revu.CodeGraph;

public class CodeGraphQuery(List<FileIndex> graph)
{
    private const int MaxResults = 50;
    public string Execute(string queryKind, string target, string? filePath = null)
    {
        var sb = new StringBuilder();

        switch (queryKind.ToLowerInvariant())
        {
            case "callers":
                FormatCallers(sb, target, filePath);
                break;
            case "implementations":
                FormatImplementations(sb, target);
                break;
            case "dependents":
                FormatDependents(sb, target);
                break;
            case "outline":
                FormatOutline(sb, target);
                break;
            case "hierarchy":
                FormatHierarchy(sb, target, filePath);
                break;
            default:
                sb.AppendLine($"Unknown query kind: {queryKind}. Use: callers, implementations, dependents, outline, hierarchy.");
                break;
        }

        var result = sb.ToString().TrimEnd();
        return result.Length == 0 ? $"No results for {queryKind} query on '{target}'." : result;
    }

    private void FormatCallers(StringBuilder sb, string target, string? filePath)
    {
        sb.AppendLine($"Callers of {target}{(filePath is not null ? $" ({filePath})" : "")}:");

        var count = 0;
        foreach (var file in graph)
        {
            if (count >= MaxResults) break;
            var path = file.Id.Replace('|', '/');
            foreach (var reference in file.References)
            {
                if (reference.Kind != "calls") continue;

                var refTarget = reference.Target;
                if (!refTarget.Equals(target, StringComparison.OrdinalIgnoreCase) &&
                    !refTarget.EndsWith($".{target}", StringComparison.OrdinalIgnoreCase) &&
                    !refTarget.EndsWith($":{target}", StringComparison.OrdinalIgnoreCase) &&
                    !refTarget.EndsWith($"/{target}", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (filePath is not null && !refTarget.Contains(filePath, StringComparison.OrdinalIgnoreCase))
                {
                    var targetFile = graph.FirstOrDefault(f =>
                        f.Id.Replace('|', '/').Equals(filePath, StringComparison.OrdinalIgnoreCase));
                    if (targetFile is not null && !targetFile.Symbols.Any(s =>
                            s.Name.Equals(target, StringComparison.OrdinalIgnoreCase)))
                        continue;
                }

                var enclosing = reference.Line is not null
                    ? file.Symbols.FirstOrDefault(s => s.StartLine <= reference.Line && s.EndLine >= reference.Line)
                    : null;

                var location = enclosing is not null
                    ? $"{path}:{enclosing.StartLine}  — {(enclosing.Enclosing is not null ? $"{enclosing.Enclosing}." : "")}{enclosing.Name}"
                    : $"{path}:{reference.Line}";

                sb.AppendLine($"  {location}");
                if (enclosing is not null)
                    sb.AppendLine($"    {enclosing.Signature}");

                if (++count >= MaxResults)
                {
                    sb.AppendLine($"  ... truncated at {MaxResults} results");
                    break;
                }
            }
        }
    }

    private void FormatImplementations(StringBuilder sb, string target)
    {
        sb.AppendLine($"Implementations of {target}:");

        var count = 0;
        foreach (var file in graph)
        {
            if (count >= MaxResults) break;
            var path = file.Id.Replace('|', '/');
            foreach (var reference in file.References)
            {
                if (reference.Kind != "implements") continue;
                if (!reference.Target.Equals(target, StringComparison.OrdinalIgnoreCase) &&
                    !reference.Target.EndsWith($".{target}", StringComparison.OrdinalIgnoreCase))
                    continue;

                var implementor = file.Symbols
                    .Where(s => s.Kind is "class" or "struct" or "record")
                    .FirstOrDefault(s => reference.Line is null ||
                        (s.StartLine <= reference.Line && s.EndLine >= reference.Line));

                if (implementor is not null)
                {
                    sb.AppendLine($"  {path}:{implementor.StartLine}  — {implementor.Name}");
                    sb.AppendLine($"    {implementor.Signature}");
                }
                else
                {
                    sb.AppendLine($"  {path}:{reference.Line}");
                }

                if (++count >= MaxResults)
                {
                    sb.AppendLine($"  ... truncated at {MaxResults} results");
                    break;
                }
            }
        }
    }

    private void FormatDependents(StringBuilder sb, string target)
    {
        var targetPath = target.TrimStart('/');
        var targetDocId = targetPath.Replace('/', '|');

        var targetFile = graph.FirstOrDefault(f =>
            f.Id.Equals(targetDocId, StringComparison.OrdinalIgnoreCase) ||
            f.Id.Replace('|', '/').Equals(targetPath, StringComparison.OrdinalIgnoreCase));

        if (targetFile is null)
        {
            sb.AppendLine($"File not found in code graph: {target}");
            return;
        }

        var symbolNames = targetFile.Symbols.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        sb.AppendLine($"Files depending on {targetPath}:");

        var count = 0;
        foreach (var file in graph)
        {
            if (file.Id == targetFile.Id) continue;

            var path = file.Id.Replace('|', '/');
            var matchingRefs = file.References
                .Where(r => symbolNames.Contains(r.Target) ||
                            symbolNames.Any(s => r.Target.EndsWith($".{s}", StringComparison.OrdinalIgnoreCase) ||
                                                 r.Target.EndsWith($":{s}", StringComparison.OrdinalIgnoreCase)))
                .GroupBy(r => r.Kind)
                .ToList();

            if (matchingRefs.Count == 0) continue;

            var summary = string.Join(", ", matchingRefs.Select(g =>
            {
                var targets = g.Select(r =>
                {
                    var t = r.Target;
                    var dot = t.LastIndexOfAny(['.', ':']);
                    return dot >= 0 ? t[(dot + 1)..] : t;
                }).Distinct(StringComparer.OrdinalIgnoreCase);
                return $"{g.Key} {string.Join(", ", targets)}";
            }));

            sb.AppendLine($"  {path,-40} — {summary}");

            if (++count >= MaxResults)
            {
                sb.AppendLine($"  ... truncated at {MaxResults} results");
                break;
            }
        }
    }

    private void FormatOutline(StringBuilder sb, string target)
    {
        var targetPath = target.TrimStart('/');
        var targetDocId = targetPath.Replace('/', '|');

        var file = graph.FirstOrDefault(f =>
            f.Id.Equals(targetDocId, StringComparison.OrdinalIgnoreCase) ||
            f.Id.Replace('|', '/').Equals(targetPath, StringComparison.OrdinalIgnoreCase));

        if (file is null)
        {
            sb.AppendLine($"File not found in code graph: {target}");
            return;
        }

        sb.AppendLine($"{targetPath}:");
        foreach (var symbol in file.Symbols.OrderBy(s => s.StartLine))
        {
            var indent = symbol.Enclosing is not null ? "  " : "";
            sb.AppendLine($"  {indent}{symbol.Kind,-12}{symbol.Signature,-60} L{symbol.StartLine}-{symbol.EndLine}");
        }
    }

    private void FormatHierarchy(StringBuilder sb, string target, string? filePath)
    {
        FileIndex? file = null;
        SymbolNode? symbol = null;

        if (filePath is not null)
        {
            var docId = filePath.TrimStart('/').Replace('/', '|');
            file = graph.FirstOrDefault(f => f.Id.Equals(docId, StringComparison.OrdinalIgnoreCase));
            symbol = file?.Symbols.FirstOrDefault(s => s.Name.Equals(target, StringComparison.OrdinalIgnoreCase));
        }

        if (symbol is null)
        {
            foreach (var f in graph)
            {
                var match = f.Symbols.FirstOrDefault(s =>
                    s.Name.Equals(target, StringComparison.OrdinalIgnoreCase) &&
                    s.Kind is "class" or "interface" or "struct" or "record" or "type");
                if (match is not null)
                {
                    file = f;
                    symbol = match;
                    break;
                }
            }
        }

        if (symbol is null || file is null)
        {
            sb.AppendLine($"Type not found in code graph: {target}");
            return;
        }

        var path = file.Id.Replace('|', '/');
        sb.AppendLine($"{target} ({path}:{symbol.StartLine}):");

        var bases = file.References
            .Where(r => r.Kind == "implements" &&
                        r.Line is not null && r.Line >= symbol.StartLine && r.Line <= symbol.EndLine)
            .Select(r => r.Target)
            .ToList();
        sb.AppendLine(bases.Count > 0 ? $"  extends: {string.Join(", ", bases)}" : "  extends: (none)");

        var extenders = new List<string>();
        foreach (var f in graph)
        {
            foreach (var r in f.References)
            {
                if (r.Kind != "implements") continue;
                if (!r.Target.Equals(target, StringComparison.OrdinalIgnoreCase) &&
                    !r.Target.EndsWith($".{target}", StringComparison.OrdinalIgnoreCase))
                    continue;

                var implementor = f.Symbols
                    .Where(s => s.Kind is "class" or "struct" or "record")
                    .FirstOrDefault(s => r.Line is null || (s.StartLine <= r.Line && s.EndLine >= r.Line));

                extenders.Add(implementor is not null
                    ? $"{f.Id.Replace('|', '/')}:{implementor.StartLine} — {implementor.Name}"
                    : f.Id.Replace('|', '/'));
            }
        }

        sb.AppendLine(extenders.Count > 0
            ? $"  extended by: {string.Join(", ", extenders)}"
            : "  extended by: (none)");
    }
}
