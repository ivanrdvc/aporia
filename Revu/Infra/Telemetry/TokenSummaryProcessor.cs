using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

using Microsoft.Extensions.Logging;

using OpenTelemetry;

namespace Revu.Infra.Telemetry;

public sealed class TokenSummaryProcessor(ILogger<TokenSummaryProcessor> logger) : BaseProcessor<Activity>
{
    private static readonly TimeSpan StalenessThreshold = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, TraceAccumulator> _traces = new();

    public override void OnEnd(Activity activity)
    {
        var traceKey = activity.TraceId.ToString();
        var operationName = activity.GetTagItem("gen_ai.operation.name")?.ToString();

        if (operationName == "chat" && TryReadTokenUsage(activity, out var tokenRecord))
        {
            var acc = _traces.GetOrAdd(traceKey, _ => new TraceAccumulator());
            acc.Tokens.Add(tokenRecord);
        }

        if (operationName == "execute_tool")
        {
            var toolName = activity.GetTagItem("gen_ai.tool.name")?.ToString();
            if (toolName is not null)
            {
                var acc = _traces.GetOrAdd(traceKey, _ => new TraceAccumulator());
                acc.ToolCalls.Add(toolName);
            }
        }

        if (activity.Parent is null && _traces.TryRemove(traceKey, out var completed))
        {
            EmitSummary(traceKey, completed);
            EvictStale();
        }
    }

    private static bool TryReadTokenUsage(Activity activity, out TokenRecord record)
    {
        record = default;

        var inputTag = activity.GetTagItem("gen_ai.usage.input_tokens");
        if (inputTag is null)
            return false;

        record = new TokenRecord(
            Agent: ResolveAgentName(activity),
            Model: activity.GetTagItem("gen_ai.request.model")?.ToString() ?? "unknown",
            Input: Convert.ToInt32(inputTag),
            Output: Convert.ToInt32(activity.GetTagItem("gen_ai.usage.output_tokens") ?? 0));
        return true;
    }

    private static string ResolveAgentName(Activity activity)
    {
        var current = activity.Parent;
        while (current is not null)
        {
            var name = current.GetTagItem("gen_ai.agent.name")?.ToString();
            if (name is not null)
                return name;
            current = current.Parent;
        }

        return "unknown";
    }

    private void EmitSummary(string traceId, TraceAccumulator accumulator)
    {
        if (accumulator.Tokens.IsEmpty)
            return;

        var groups = accumulator.Tokens
            .GroupBy(r => (r.Agent, r.Model))
            .OrderByDescending(g => g.Sum(r => r.Input))
            .ToList();

        var totalInput = 0;
        var totalOutput = 0;
        var totalCalls = 0;

        var sb = new StringBuilder();
        sb.AppendLine($"Token usage | trace: {traceId[..12]}");

        foreach (var group in groups)
        {
            var input = group.Sum(r => r.Input);
            var output = group.Sum(r => r.Output);
            var calls = group.Count();
            var peak = group.Max(r => r.Input);

            totalInput += input;
            totalOutput += output;
            totalCalls += calls;

            sb.AppendLine(
                $"  {group.Key.Agent,-14} ({group.Key.Model}): {input,10:N0} in / {output,6:N0} out  ({calls} calls, peak: {peak:N0} in)");
        }

        sb.AppendLine($"  {"Total",-14}               {totalInput,10:N0} in / {totalOutput,6:N0} out  ({totalCalls} calls)");

        if (!accumulator.ToolCalls.IsEmpty)
        {
            var toolGroups = accumulator.ToolCalls
                .GroupBy(t => t)
                .OrderByDescending(g => g.Count())
                .Select(g => $"{g.Count()}x {g.Key}");
            sb.AppendLine($"  Tools: {string.Join(", ", toolGroups)}");
        }

        logger.LogInformation("{TokenSummary}", sb.ToString());
    }

    private void EvictStale()
    {
        var cutoff = DateTimeOffset.UtcNow - StalenessThreshold;
        foreach (var (key, value) in _traces)
        {
            if (value.Created < cutoff)
                _traces.TryRemove(key, out _);
        }
    }

    private sealed class TraceAccumulator
    {
        public ConcurrentBag<TokenRecord> Tokens { get; } = [];
        public ConcurrentBag<string> ToolCalls { get; } = [];
        public DateTimeOffset Created { get; } = DateTimeOffset.UtcNow;
    }

    private readonly record struct TokenRecord(string Agent, string Model, int Input, int Output);
}
