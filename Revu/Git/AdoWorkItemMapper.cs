using System.Text.RegularExpressions;

using Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models;

namespace Revu.Git;

/// <summary>
/// Converts ADO work items to <see cref="WorkItemContext"/> with HTML cleaning and field capping.
/// </summary>
public static class AdoWorkItemMapper
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
        text = Regex.Replace(text, @"<br\s*/?>|</p>|</li>|</div>|</h\d>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<li[^>]*>", "- ", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"</?[A-Za-z][^>]*>", "");
        text = Regex.Replace(text, @"[^\S\n]+", " ");
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        text = text.Trim();

        if (text.Length > MaxFieldLength)
            text = text[..MaxFieldLength] + " [truncated]";

        return text.Length > 0 ? text : null;
    }
}
