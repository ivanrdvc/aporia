using System.Text.RegularExpressions;

using Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models;

namespace Revu.Git;

/// <summary>
/// Converts ADO work items to <see cref="WorkItemContext"/> with HTML cleaning and field capping.
/// </summary>
public static partial class AdoWorkItemMapper
{
    public static readonly string[] Fields =
    [
        "System.WorkItemType", "System.Title", "System.Description",
        "Microsoft.VSTS.Common.AcceptanceCriteria", "System.Parent"
    ];

    private const int MaxFieldLength = 1500;

    public static int? GetParentId(WorkItem wi)
    {
        if (wi.Fields.TryGetValue("System.Parent", out var obj) && obj is not null
            && int.TryParse(obj.ToString(), out var parentId))
            return parentId;

        return null;
    }

    public static WorkItemContext ToContext(WorkItem wi, WorkItemContext? parent = null)
    {
        var fields = (IReadOnlyDictionary<string, object>)wi.Fields;
        var type = fields.GetValueOrDefault("System.WorkItemType")?.ToString() ?? "Unknown";
        var title = fields.GetValueOrDefault("System.Title")?.ToString() ?? "";
        var description = CleanHtml(fields.GetValueOrDefault("System.Description")?.ToString());
        var acceptanceCriteria = CleanHtml(fields.GetValueOrDefault("Microsoft.VSTS.Common.AcceptanceCriteria")?.ToString());

        return new WorkItemContext(type, title, description, acceptanceCriteria, parent);
    }

    public static List<WorkItemContext> MapWithParents(
        IReadOnlyList<WorkItem> workItems,
        IReadOnlyDictionary<int, WorkItemContext> parentMap)
    {
        var items = new List<WorkItemContext>();
        foreach (var wi in workItems)
        {
            if (wi.Fields is null)
                continue;

            var parentId = GetParentId(wi);
            WorkItemContext? parent = parentId is not null ? parentMap.GetValueOrDefault(parentId.Value) : null;
            items.Add(ToContext(wi, parent));
        }

        return items;
    }

    public static string? CleanHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return null;

        var text = System.Net.WebUtility.HtmlDecode(html);
        text = BlockBreakRegex().Replace(text, "\n");
        text = ListItemRegex().Replace(text, "- ");
        text = AnyTagRegex().Replace(text, "");
        text = HorizontalWhitespaceRegex().Replace(text, " ");
        text = ExcessiveNewlinesRegex().Replace(text, "\n\n");
        text = text.Trim();

        if (text.Length > MaxFieldLength)
            text = text[..MaxFieldLength] + " [truncated]";

        return text.Length > 0 ? text : null;
    }

    [GeneratedRegex(@"<br\s*/?>|</p>|</li>|</div>|</h\d>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockBreakRegex();

    [GeneratedRegex(@"<li[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ListItemRegex();

    [GeneratedRegex(@"</?[A-Za-z][^>]*>")]
    private static partial Regex AnyTagRegex();

    [GeneratedRegex(@"[^\S\n]+")]
    private static partial Regex HorizontalWhitespaceRegex();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ExcessiveNewlinesRegex();
}
