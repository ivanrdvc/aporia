# Observability

Aporia uses OpenTelemetry for tracing, metrics, and logs. Locally, data flows to OpenObserve via OTLP. In production, data exports to Azure Application Insights via the Azure Monitor exporter. Both can run simultaneously.

## Automatic telemetry (zero code)

These come from built-in instrumentation, no custom code needed.

| What | Source | Span attributes |
|------|--------|-----------------|
| Token usage per LLM call | `UseOpenTelemetry()` on IChatClient | `gen_ai.usage.input_tokens`, `output_tokens`, `gen_ai.request.model` |
| Agent run spans | `UseOpenTelemetry()` on ChatClientAgent | `gen_ai.agent.name` (Reviewer/Explorer), duration |
| HTTP calls to Azure DevOps | `AddHttpClientInstrumentation()` | URL, status code, duration |
| Function invocation | `UseFunctionsWorkerDefaults()` | trigger type, duration, success/failure |


## Custom metrics

All tagged with `project` + `repository` for per-repo breakdowns.

| Metric | Type | Description |
|--------|------|-------------|
| `aporia.reviews.processed` | counter | PRs reviewed |
| `aporia.review.duration` | histogram (s) | End-to-end review time |
| `aporia.diff.files` | histogram | Files in the diff |
| `aporia.diff.size` | histogram (chars) | Total diff size |
| `aporia.findings.generated` | counter | Findings from LLM before filtering |
| `aporia.findings.posted` | counter | Findings posted after capping |
| `aporia.agent.explorations` | counter | Explorer agent dispatches |
| `aporia.review.parse_failures` | counter | Reviews where structured output failed to parse |
| `aporia.agent.exploration_failures` | counter | Explorer invocations that failed or returned invalid output |

## Token summary processor

`TokenSummaryProcessor` is a `BaseProcessor<Activity>` that aggregates token usage across all LLM
calls in a trace and logs a summary when the trace completes. Registered via
`.AddProcessor<TokenSummaryProcessor>()` in the OTel tracing pipeline.

**How it works:** As spans end, the processor accumulates `chat` spans (token counts + model) and
`execute_tool` spans (tool names). It resolves each chat span's agent by walking the parent chain
to the nearest `invoke_agent` span. When the root span ends (`Activity.Parent is null`), it emits
a structured log with per-agent breakdown and tool call counts, then evicts stale traces (>10 min).

**Example output:**

```
Token usage | trace: abc123def456
  Reviewer   (gpt-4.1):       31,200 in /  1,450 out  (2 calls)
  Explorer   (gpt-4.1-mini):  14,800 in /  2,100 out  (7 calls)
  Total                        46,000 in /  3,550 out  (9 calls)
  Tools: 5x FetchFile, 3x SearchCode, 1x ListDirectory
```

Visible in: console, OpenObserve Logs tab, App Insights logs.

## Local development

Start OpenObserve, then run the function app:

    docker run --rm -it -p 5080:5080 -p 4317:5081 -e ZO_ROOT_USER_EMAIL=root@example.com -e ZO_ROOT_USER_PASSWORD=Complexpass#123 --name openobserve public.ecr.aws/zinclabs/openobserve:latest

Dashboard: http://localhost:5080. Click any trace to see the full span tree with token counts.

## App Insights queries (KQL)

### Total tokens for a specific PR

Replace `TRACE_ID` with the `operation_Id` from a trace.

    dependencies
    | where operation_Id == "TRACE_ID"
    | where name startswith "chat"
    | extend input_tokens = toint(customDimensions["gen_ai.usage.input_tokens"])
    | extend output_tokens = toint(customDimensions["gen_ai.usage.output_tokens"])
    | extend model = tostring(customDimensions["gen_ai.request.model"])
    | summarize total_input=sum(input_tokens), total_output=sum(output_tokens), llm_calls=count() by model

### Tokens by agent — reviewer vs explorers

    let agents = dependencies
    | where operation_Id == "TRACE_ID"
    | where name startswith "invoke_agent"
    | project agent_span_id = id, agent_name = name;
    dependencies
    | where operation_Id == "TRACE_ID"
    | where name startswith "chat"
    | extend input_tokens = toint(customDimensions["gen_ai.usage.input_tokens"])
    | extend output_tokens = toint(customDimensions["gen_ai.usage.output_tokens"])
    | join kind=inner agents on $left.operation_ParentId == $right.agent_span_id
    | summarize input=sum(input_tokens), output=sum(output_tokens), calls=count() by agent_name

### Average cost per review (last 7 days)

    dependencies
    | where timestamp > ago(7d)
    | where name startswith "chat"
    | extend input_tokens = toint(customDimensions["gen_ai.usage.input_tokens"])
    | extend output_tokens = toint(customDimensions["gen_ai.usage.output_tokens"])
    | extend model = tostring(customDimensions["gen_ai.request.model"])
    | summarize total_input=sum(input_tokens), total_output=sum(output_tokens) by operation_Id, model
    | extend cost_usd = iff(model contains "mini",
        (total_input * 0.00015 + total_output * 0.0006) / 1000,
        (total_input * 0.005 + total_output * 0.015) / 1000)
    | summarize avg_cost=round(avg(cost_usd), 4), total_cost=round(sum(cost_usd), 2), reviews=dcount(operation_Id)

### PRs per repo (daily)

    customMetrics
    | where name == "aporia.reviews.processed"
    | extend project = tostring(customDimensions["project"])
    | extend repository = tostring(customDimensions["repository"])
    | summarize reviews=sum(value) by project, repository, bin(timestamp, 1d)

### Waste ratio per repo

    customMetrics
    | where name in ("aporia.findings.generated", "aporia.findings.posted")
    | extend repository = tostring(customDimensions["repository"])
    | summarize generated=sumif(value, name=="aporia.findings.generated"),
                posted=sumif(value, name=="aporia.findings.posted") by repository
    | extend waste_pct = round((generated - posted) / generated * 100, 1)

### Slowest reviews

    customMetrics
    | where name == "aporia.review.duration"
    | extend project = tostring(customDimensions["project"])
    | extend repository = tostring(customDimensions["repository"])
    | top 20 by value desc
    | project timestamp, project, repository, duration_s=round(value, 1)
