using System.Text;

using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;

namespace Aporia.Git;

public static class DiffBuilder
{
    public static string NewFile(string content)
    {
        var sb = new StringBuilder();
        sb.AppendLine("@@ new file @@");
        var lines = content.Split('\n');
        for (var i = 0; i < lines.Length; i++)
            sb.AppendLine($"+ {i + 1}: {lines[i]}");
        return sb.ToString();
    }

    public static string Hunks(string baseContent, string targetContent, int context = 10)
    {
        var diff = InlineDiffBuilder.Diff(baseContent, targetContent);
        var lines = diff.Lines;

        var changed = lines
            .Select((l, i) => (l, i))
            .Where(x => x.l.Type != ChangeType.Unchanged)
            .Select(x => x.i)
            .ToList();

        if (changed.Count == 0)
            return "(no changes detected)";

        var ranges = changed
            .Select(i => (start: Math.Max(0, i - context), end: Math.Min(lines.Count - 1, i + context)))
            .Aggregate(new List<(int start, int end)>(), (acc, r) =>
            {
                if (acc.Count > 0 && r.start <= acc[^1].end + 1)
                    acc[^1] = (acc[^1].start, Math.Max(acc[^1].end, r.end));
                else
                    acc.Add(r);
                return acc;
            });

        var added = changed.Count(i => lines[i].Type == ChangeType.Inserted);
        var removed = changed.Count(i => lines[i].Type == ChangeType.Deleted);

        var sb = new StringBuilder();
        sb.AppendLine($"(+{added} / -{removed} lines changed)");

        // Pre-compute original and modified line numbers for each diff line
        var origLine = 1;
        var modLine = 1;
        var lineTracker = new int[lines.Count, 2];
        for (var i = 0; i < lines.Count; i++)
        {
            lineTracker[i, 0] = origLine;
            lineTracker[i, 1] = modLine;
            if (lines[i].Type != ChangeType.Inserted) origLine++;
            if (lines[i].Type != ChangeType.Deleted) modLine++;
        }

        foreach (var (start, end) in ranges)
        {
            sb.AppendLine($"@@ -{lineTracker[start, 0]},{end - start + 1} +{lineTracker[start, 1]},{end - start + 1} @@");
            for (var i = start; i <= end; i++)
            {
                var line = lines[i];
                var num = line.Type == ChangeType.Deleted ? lineTracker[i, 0] : lineTracker[i, 1];
                var prefix = line.Type switch
                {
                    ChangeType.Inserted => "+",
                    ChangeType.Deleted => "-",
                    _ => " "
                };
                sb.AppendLine($"{prefix} {num}: {line.Text}");
            }
        }

        return sb.ToString();
    }
}
