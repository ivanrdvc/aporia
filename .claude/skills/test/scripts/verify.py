#!/usr/bin/env python3
"""
Verify script for Aporia integration test runs.

Parses session JSON files and optionally a test log file to produce a structured
summary of agent behavior, tool usage, and token consumption.

Usage:
    python3 verify.py [session-dir] [--log <logfile>] [--verbose] [--history]

If session-dir is omitted, auto-discovers the most recent run-* directory.
"""

import json
import re
import sys
import urllib.request
import urllib.error
from pathlib import Path
from collections import Counter
from datetime import datetime, timezone

# .claude/skills/test/scripts/verify.py → parents[4] = repo root
SESSIONS_ROOT = (
    Path(__file__).resolve().parents[4]
    / "tests" / "Aporia.Tests.Integration" / "bin" / "Debug" / "net10.0" / "sessions"
)
HISTORY_FILE = SESSIONS_ROOT / "runs.log"


# ── Session loading ──────────────────────────────────────────────────────────

def find_latest_run(root: Path):
    runs = sorted(root.glob("run-*"), key=lambda p: p.name, reverse=True)
    return runs[0] if runs else None


def load_sessions(run_dir: Path):
    """Load session JSON files. Handles envelope format and flat arrays."""
    sessions = []
    for f in sorted(run_dir.glob("*.json")):
        with open(f, encoding="utf-8") as fh:
            raw = json.load(fh)
        if isinstance(raw, dict) and "messages" in raw:
            sessions.append((f.name, raw))
        else:
            sessions.append((f.name, {"messages": raw}))
    return sessions


# ── Extraction helpers ───────────────────────────────────────────────────────

def extract_function_calls(messages):
    calls = []
    for msg in messages:
        for content in msg.get("Contents", []):
            if content.get("$type") == "functionCall":
                calls.append(content)
    return calls


def extract_function_results(messages):
    results = {}
    for msg in messages:
        for content in msg.get("Contents", []):
            if content.get("$type") == "functionResult":
                results[content["CallId"]] = content.get("Result", "")
    return results


def extract_timestamps(messages):
    timestamps = []
    for msg in messages:
        ts = msg.get("CreatedAt")
        if ts:
            try:
                timestamps.append(datetime.fromisoformat(ts))
            except ValueError:
                pass
    return timestamps


# ── Analysis ─────────────────────────────────────────────────────────────────

def _get_args(call, key):
    """Extract argument values from plural keys (paths, queries)."""
    args = call.get("Arguments", {})
    val = args.get(key)
    if isinstance(val, list):
        return val
    if val:
        return [val]
    return []


def analyze_session(name, messages):
    """Analyze tool usage for a session."""
    calls = extract_function_calls(messages)
    results = extract_function_results(messages)
    timestamps = extract_timestamps(messages)

    tool_counts = Counter(c["Name"] for c in calls)
    total_calls = len(calls)

    fetch_paths = []
    for c in calls:
        if c["Name"] == "FetchFile":
            fetch_paths.extend(_get_args(c, "paths"))
    path_counts = Counter(fetch_paths)
    duplicates = {p: n for p, n in path_counts.items() if n > 1}

    not_found = []
    for c in calls:
        if c["Name"] == "FetchFile":
            result_text = results.get(c["CallId"], "")
            if "File not found" in result_text:
                not_found.extend(_get_args(c, "paths"))

    list_dir_paths = []
    for c in calls:
        if c["Name"] == "ListDirectory":
            list_dir_paths.extend(_get_args(c, "paths"))
    list_dir_dupes = {p: n for p, n in Counter(list_dir_paths).items() if n > 1}

    search_queries = []
    search_empty = 0
    for c in calls:
        if c["Name"] == "SearchCode":
            queries = _get_args(c, "queries")
            search_queries.extend(queries)
            result_text = results.get(c["CallId"], "")
            if "No results" in result_text:
                search_empty += len(queries)
    search_dupes = {q: n for q, n in Counter(search_queries).items() if n > 1}

    explore_calls = [c for c in calls if c["Name"] == "Explore"]
    explore_queries = []
    for c in explore_calls:
        for q in _get_args(c, "queries"):
            explore_queries.append(q[:120] + "..." if len(q) > 120 else q)

    flags = []
    if total_calls > 20:
        flags.append(f"Excessive tool calls ({total_calls})")
    if duplicates:
        flags.append(f"Duplicate file fetches: {duplicates}")
    if len(not_found) > 2:
        flags.append(f"Many file-not-found ({len(not_found)}): {not_found}")
    if list_dir_dupes:
        flags.append(f"Repeated ListDirectory: {list_dir_dupes}")
    if search_dupes:
        flags.append(f"Duplicate searches: {search_dupes}")

    wall_clock = None
    if len(timestamps) >= 2:
        wall_clock = (timestamps[-1] - timestamps[0]).total_seconds()

    return {
        "session_file": name,
        "total_tool_calls": total_calls,
        "tool_counts": dict(tool_counts),
        "llm_turns": sum(1 for m in messages if m.get("Role") == "assistant"),
        "duplicate_fetches": duplicates,
        "not_found": not_found,
        "search_queries": search_queries,
        "search_empty": search_empty,
        "search_dupes": search_dupes,
        "explore_queries": explore_queries,
        "list_dir_dupes": list_dir_dupes,
        "flags": flags,
        "workflow": "Optimal" if not flags else "Suboptimal",
        "wall_clock_seconds": wall_clock,
    }


# ── Prompt budget analysis ──────────────────────────────────────────────────

def _extract_first_user_text(messages):
    for msg in messages:
        if msg.get("Role") == "user":
            parts = [c.get("Text", "") for c in msg.get("Contents", []) if c.get("$type") == "text"]
            return "".join(parts)
    return ""


def _measure_session(label, session):
    instructions = session.get("instructions", "") or ""
    tools = session.get("tools", [])
    messages = session.get("messages", [])

    total_chars = 0
    for msg in messages:
        for content in msg.get("Contents", []):
            t = content.get("$type")
            if t == "text":
                total_chars += len(content.get("Text", ""))
            elif t == "functionResult":
                total_chars += len(content.get("Result", ""))
            elif t == "functionCall":
                args = content.get("Arguments", {})
                total_chars += len(json.dumps(args)) if args else 0

    return {
        "label": label,
        "sys_prompt_chars": len(instructions),
        "tools_chars": len(json.dumps(tools)) if tools else 0,
        "tool_names": [t.get("name", "?") for t in tools],
        "init_message_chars": len(_extract_first_user_text(messages)),
        "total_chars": total_chars,
    }


# ── Token summary parsing from log ──────────────────────────────────────────

def parse_token_summary(log_text):
    pattern = re.compile(
        r"Token usage \| trace: (\w+)\n((?:\s+.+\n)+)",
        re.MULTILINE,
    )
    matches = pattern.findall(log_text)
    if not matches:
        return None

    summaries = []
    for trace_id, block in matches:
        lines = [l.strip() for l in block.strip().split("\n") if l.strip()]
        agents = []
        total_line = None
        tools_line = None

        for line in lines:
            if line.startswith("Total"):
                total_line = line
            elif line.startswith("Tools:"):
                tools_line = line
            else:
                agents.append(line)

        summaries.append({
            "trace_id": trace_id,
            "agents": agents,
            "total": total_line,
            "tools": tools_line,
        })

    return summaries


def parse_findings_from_log(log_text):
    """Extract findings count and maxComments from test log output."""
    m = re.search(r"Findings:\s*(\d+)\s+\(maxComments:\s*(\d+)\)", log_text)
    if m:
        return int(m.group(1)), int(m.group(2))
    return None, None


def parse_session_dir_from_log(log_text):
    """Extract session directory path from test log output."""
    m = re.search(r"Sessions:\s*(.+)", log_text)
    if m:
        return Path(m.group(1).strip())
    return None


def parse_strategy_from_log(log_text):
    """Extract strategy name from test log output (if overridden)."""
    m = re.search(r"Strategy(?:\s+override)?:\s*(\S+)", log_text)
    if m:
        return m.group(1).strip()
    return None


def parse_parse_failure(log_text):
    """Detect 'Failed to parse ReviewResult' warnings in test log."""
    m = re.search(r"Failed to parse ReviewResult from last message \((\d+) chars, (\d+) messages\)", log_text)
    if m:
        return int(m.group(1)), int(m.group(2))
    return None




def parse_total_tokens(token_summaries):
    """Extract total input tokens from token summary."""
    if not token_summaries:
        return None
    for ts in token_summaries:
        if ts["total"]:
            m = re.search(r"([\d,]+)\s+in", ts["total"])
            if m:
                return int(m.group(1).replace(",", ""))
    return None


def parse_model_name(token_summaries):
    """Extract reviewer model name from token summary."""
    if not token_summaries:
        return None
    for ts in token_summaries:
        for agent in ts["agents"]:
            if "Reviewer" in agent:
                m = re.search(r"\(([^)]+)\)", agent)
                if m:
                    return m.group(1)
    return None


# ── Findings extraction & matching ────────────────────────────────────────────

EXPECTATIONS_FILE = Path(__file__).resolve().parent / "expectations.json"


def load_all_expectations():
    """Load all expected findings from expectations.json."""
    if not EXPECTATIONS_FILE.exists():
        return {}
    with open(EXPECTATIONS_FILE, encoding="utf-8") as f:
        return json.load(f)


def extract_findings_from_sessions(sessions):
    """Extract ReviewResult findings from the reviewer's last assistant message."""
    # Reviewer is the last session file
    for _name, session in reversed(sessions):
        agent = session.get("agent", "").lower()
        if agent == "explorer":
            continue
        messages = session.get("messages", [])
        # Walk messages in reverse to find last assistant text
        for msg in reversed(messages):
            if msg.get("Role") != "assistant":
                continue
            for content in msg.get("Contents", []):
                if content.get("$type") != "text":
                    continue
                text = content.get("Text", "")
                try:
                    parsed = json.loads(text)
                    if isinstance(parsed, dict) and "findings" in parsed:
                        return parsed["findings"]
                except (json.JSONDecodeError, TypeError):
                    continue
        break  # only check the reviewer session
    return None


def match_findings(actual_findings, expected_findings):
    """Match actual findings against expected using file path + keyword.

    Same logic as ExpectedFindingsEvaluator.cs: normalize paths (strip leading /,
    case-insensitive), keyword substring match in message.
    """
    matched_actual = set()
    results = []

    for exp in expected_findings:
        exp_path = exp["file"].lstrip("/").lower()
        # Support single keyword string or list of keywords (match any)
        keywords_raw = exp.get("keywords", exp.get("keyword", ""))
        if isinstance(keywords_raw, str):
            keywords = [keywords_raw.lower()]
        else:
            keywords = [k.lower() for k in keywords_raw]
        found = False

        for i, finding in enumerate(actual_findings):
            if i in matched_actual:
                continue
            actual_path = finding.get("filePath", "").lstrip("/").lower()
            message = finding.get("message", "").lower()

            if actual_path == exp_path and any(k in message for k in keywords):
                found = True
                matched_actual.add(i)
                break

        results.append({
            "file": exp["file"],
            "keywords": keywords,
            "description": exp.get("description", ""),
            "required": exp.get("required", True),
            "found": found,
        })

    extras = [actual_findings[i] for i in range(len(actual_findings))
              if i not in matched_actual]

    return results, extras


def _print_findings_report(match_results, extras):
    """Print findings coverage report."""
    if not match_results:
        return

    required = [r for r in match_results if r["required"]]
    optional = [r for r in match_results if not r["required"]]
    req_found = sum(1 for r in required if r["found"])
    opt_found = sum(1 for r in optional if r["found"])

    total_found = req_found + opt_found
    total = len(match_results)
    recall = total_found / total if total else 0

    print(f"\n## Finding Coverage\n")
    print(f"  Recall: {total_found}/{total} ({recall:.0%})"
          f"  [required: {req_found}/{len(required)}, optional: {opt_found}/{len(optional)}]")

    if required:
        print(f"\n  Required:")
        for r in required:
            icon = "+" if r["found"] else "MISS"
            print(f"    [{icon}] {r['description']}  ({r['file']})")

    if optional:
        print(f"\n  Optional:")
        for r in optional:
            icon = "+" if r["found"] else "-"
            print(f"    [{icon}] {r['description']}  ({r['file']})")

    if extras:
        print(f"\n  Extra findings ({len(extras)}):")
        for f in extras:
            print(f"    ? {f.get('filePath', '?')} — {f.get('message', '')[:100]}")


# ── Report ───────────────────────────────────────────────────────────────────

def fmt_chars(chars):
    return f"{chars:,} chars (~{chars // 4:,} tok)"


def _print_session_budget(data):
    print(f"  {data['label']}:")
    print(f"    System prompt:    {fmt_chars(data['sys_prompt_chars'])}")
    print(f"    Tool definitions: {fmt_chars(data['tools_chars'])}  [{', '.join(data['tool_names'])}]")
    print(f"    Initial message:  {fmt_chars(data['init_message_chars'])}")
    init_total = data['sys_prompt_chars'] + data['tools_chars'] + data['init_message_chars']
    print(f"    -------------------------------------")
    print(f"    Initial total:    {fmt_chars(init_total)}")
    print(f"    Session total:    {fmt_chars(data['total_chars'])}")


def _print_session_tools(label, data):
    print(f"  --- {label} ---")
    print(f"    Tool calls: {data['total_tool_calls']}  |  LLM turns: {data['llm_turns']}")
    print(f"    Tools: {data['tool_counts']}")
    if data["wall_clock_seconds"] is not None:
        print(f"    Wall clock: {data['wall_clock_seconds']:.1f}s")
    if data["explore_queries"]:
        print(f"    Explore calls ({len(data['explore_queries'])}):")
        for i, q in enumerate(data["explore_queries"], 1):
            print(f"      {i}. {q}")
    if data["search_queries"]:
        print(f"    Searches ({len(data['search_queries'])}): {data['search_queries']}")
    if data["search_empty"]:
        print(f"    ! Empty searches: {data['search_empty']}")
    if data["search_dupes"]:
        print(f"    ! Duplicate searches: {data['search_dupes']}")
    if data["not_found"]:
        print(f"    File not found: {data['not_found']}")
    if data["duplicate_fetches"]:
        print(f"    ! DUPLICATE fetches: {data['duplicate_fetches']}")
    if data["list_dir_dupes"]:
        print(f"    ! DUPLICATE ListDirectory: {data['list_dir_dupes']}")
    print(f"    Workflow: {data['workflow']}")
    if data["flags"]:
        for f in data["flags"]:
            print(f"      ! {f}")


def _print_summary(sessions_data, findings_count, max_comments, parse_failure=None,
                    session_findings_count=None):
    """Print aggregate tool use summary at the top."""
    reviewers = [s for s in sessions_data if s.get("agent") != "explorer"]
    explorers = [s for s in sessions_data if s.get("agent") == "explorer"]

    rev_calls = sum(s["total_tool_calls"] for s in reviewers)
    rev_turns = sum(s["llm_turns"] for s in reviewers)
    exp_calls = sum(s["total_tool_calls"] for s in explorers)
    total_calls = rev_calls + exp_calls

    agg_tools = Counter()
    for s in sessions_data:
        agg_tools.update(s["tool_counts"])

    all_dupes = sum(sum(s["duplicate_fetches"].values()) - len(s["duplicate_fetches"])
                    for s in sessions_data)
    all_not_found = sum(len(s["not_found"]) for s in sessions_data)
    all_search_dupes = sum(sum(s["search_dupes"].values()) - len(s["search_dupes"])
                          for s in sessions_data)
    all_search_empty = sum(s["search_empty"] for s in sessions_data)
    all_flags = [f for s in sessions_data for f in s["flags"]]

    rev_wall = sum(s["wall_clock_seconds"] for s in reviewers if s["wall_clock_seconds"])
    exp_wall = sum(s["wall_clock_seconds"] for s in explorers if s["wall_clock_seconds"])

    print(f"\n## Summary\n")

    if findings_count is not None:
        status = ""
        if findings_count == 0:
            status = "  !! ZERO FINDINGS"
        elif findings_count == max_comments:
            status = "  ! hit cap"
        print(f"  Findings:  {findings_count} / {max_comments}{status}")

    if reviewers:
        print(f"  Reviewer:  {rev_calls} calls, {rev_turns} LLM turns, {len(explorers)} explores")
    if explorers:
        print(f"  Explorers: {exp_calls} calls across {len(explorers)} agents (avg {exp_calls/len(explorers):.1f})")
    print(f"  Total:     {total_calls} tool calls  |  {dict(agg_tools)}")
    if rev_wall or exp_wall:
        parts = []
        if rev_wall:
            parts.append(f"reviewer {rev_wall:.0f}s")
        if exp_wall:
            parts.append(f"explorers {exp_wall:.0f}s")
        print(f"  Wall clock: {' + '.join(parts)}")

    issues = []
    if all_dupes:
        issues.append(f"{all_dupes} duplicate fetches")
    if all_not_found:
        issues.append(f"{all_not_found} file-not-found")
    if all_search_dupes:
        issues.append(f"{all_search_dupes} duplicate searches")
    if all_search_empty:
        issues.append(f"{all_search_empty} empty searches")
    if issues:
        print(f"  Issues:    {' | '.join(issues)}")

    if parse_failure:
        chars, msgs = parse_failure
        verdict = "PARSE_FAILURE"
        print(f"  !! Parse failure: model produced {chars} chars across {msgs} messages but JSON parsing failed")
        print(f"     Findings were lost — 0 is a fallback, not a real result")
    elif findings_count == 0 and session_findings_count:
        verdict = "FINDINGS_LOST"
        print(f"  !! Pipeline returned 0 but model produced {session_findings_count} findings in the session")
        print(f"     Structured output parsed in session but lost in the C# pipeline")
    elif findings_count == 0:
        verdict = "ZERO FINDINGS"
    elif all_flags:
        verdict = f"ISSUES ({len(all_flags)})"
    else:
        verdict = "CLEAN"
    print(f"  Verdict:   {verdict}")

    return verdict


def _print_minimal_report(run_dir, strategy, findings_count, max_comments,
                          token_summaries, parse_failure):
    """Print a reduced report when no session files are available.

    This happens for strategies that manage their own sessions externally
    (e.g. Copilot, ClaudeCode) and don't write through FileSessionProvider.
    """
    strategy_label = strategy or "unknown"

    print(f"\n{'=' * 60}")
    print(f"  Session analysis: {run_dir.name}")
    print(f"  Strategy: {strategy_label}")
    print(f"  Sessions: 0  (strategy uses external session management)")
    print(f"  Path: {run_dir}")
    print(f"{'=' * 60}")

    print(f"\n## Summary\n")

    if findings_count is not None:
        status = ""
        if findings_count == 0:
            status = "  !! ZERO FINDINGS"
        elif findings_count == max_comments:
            status = "  ! hit cap"
        print(f"  Findings:  {findings_count} / {max_comments}{status}")

    if parse_failure:
        chars, msgs = parse_failure
        print(f"  !! Parse failure: model produced {chars} chars across {msgs} messages but JSON parsing failed")

    print(f"  Session analysis: skipped (no session files for {strategy_label} strategy)")

    if token_summaries:
        print(f"\n## Token Usage\n")
        for ts in token_summaries:
            print(f"  trace: {ts['trace_id']}")
            for agent in ts["agents"]:
                print(f"    {agent}")
            if ts["total"]:
                print(f"    {ts['total']}")

    print()


def print_report(run_dir, sessions_data, token_summaries, budgets,
                 findings_count, max_comments, verbose,
                 match_results=None, extras=None, parse_failure=None,
                 session_findings_count=None):
    print(f"\n{'=' * 60}")
    print(f"  Session analysis: {run_dir.name}")
    print(f"  Sessions: {len(sessions_data)}")
    print(f"  Path: {run_dir}")
    print(f"{'=' * 60}")

    verdict = _print_summary(sessions_data, findings_count, max_comments, parse_failure,
                              session_findings_count)

    if verbose and budgets:
        print(f"\n## Prompt Budget (~4 chars/token)\n")
        for b in budgets:
            _print_session_budget(b)
            print()

    reviewer = [s for s in sessions_data if s.get("agent") != "explorer"]
    explorers = [s for s in sessions_data if s.get("agent") == "explorer"]

    if reviewer:
        print(f"\n## Reviewer\n")
        for s in reviewer:
            _print_session_tools(s["session_file"], s)
            print()

    if explorers:
        print(f"\n## Explorers ({len(explorers)})\n")
        for s in explorers:
            _print_session_tools(s["session_file"], s)
            print()

    if token_summaries:
        print(f"\n## Token Usage\n")
        for ts in token_summaries:
            print(f"  trace: {ts['trace_id']}")
            for agent in ts["agents"]:
                print(f"    {agent}")
            if ts["total"]:
                print(f"    {ts['total']}")
            if ts["tools"]:
                print(f"    {ts['tools']}")

    if match_results is not None:
        _print_findings_report(match_results, extras or [])

    print()
    return verdict


# ── OpenObserve OTEL log fetch ───────────────────────────────────────────────

OO_BASE     = "http://localhost:5080"
OO_ORG      = "default"
OO_AUTH     = "Basic cm9vdEBleGFtcGxlLmNvbTpDb21wbGV4cGFzcyMxMjM="

# Log bodies that add no diagnostic value
_OO_NOISE = [
    "Execution attempt. Source:",
    "Application started",
    "Application is shutting down",
    "Now listening on:",
    "Hosting environment:",
    "Content root path:",
]


def fetch_otel_logs(trace_id_prefix: str) -> list[dict] | None:
    """Fetch log entries for a trace from OpenObserve. Returns None if unavailable."""
    now_us   = int(datetime.now(timezone.utc).timestamp() * 1_000_000)
    day_us   = now_us - int(24 * 3600 * 1_000_000)

    payload = json.dumps({
        "query": {
            "sql": f"SELECT * FROM default WHERE trace_id LIKE '{trace_id_prefix}%' ORDER BY _timestamp ASC",
            "start_time": day_us,
            "end_time":   now_us,
            "size": 200,
        }
    }).encode()

    req = urllib.request.Request(
        f"{OO_BASE}/api/{OO_ORG}/_search",
        data=payload,
        headers={"Authorization": OO_AUTH, "Content-Type": "application/json"},
    )
    try:
        with urllib.request.urlopen(req, timeout=5) as r:
            return json.load(r).get("hits", [])
    except (urllib.error.URLError, OSError):
        return None


def _print_otel_section(trace_id_prefix: str) -> None:
    hits = fetch_otel_logs(trace_id_prefix)
    if hits is None:
        print(f"  (OpenObserve unavailable — skipping)")
        return
    if not hits:
        print(f"  (no log entries found for trace {trace_id_prefix})")
        return

    printed = 0
    for h in hits:
        body = h.get("body", "")
        if any(noise in body for noise in _OO_NOISE):
            continue

        ts_us  = h.get("_timestamp", 0)
        ts     = datetime.fromtimestamp(ts_us / 1_000_000, tz=timezone.utc).strftime("%H:%M:%S")
        sev    = h.get("severity", "")[:4].upper()
        source = h.get("instrumentation_library_name", "").split(".")[-1]
        line   = body.split("\n")[0][:120]

        print(f"  {ts}  {sev:4}  [{source}]  {line}")
        printed += 1

    if not printed:
        print(f"  (all {len(hits)} entries were noise-filtered)")


# ── History ──────────────────────────────────────────────────────────────────

def append_history(run_dir, findings_count, max_comments, sessions_data,
                   token_summaries, verdict):
    """Append a one-liner to runs.log for cross-run comparison."""
    model = parse_model_name(token_summaries) or "?"
    total_tok = parse_total_tokens(token_summaries)
    tok_str = f"{total_tok:,}" if total_tok else "?"

    reviewers = [s for s in sessions_data if s.get("agent") != "explorer"]
    explorers = [s for s in sessions_data if s.get("agent") == "explorer"]
    total_calls = sum(s["total_tool_calls"] for s in sessions_data)

    rev_wall = sum(s["wall_clock_seconds"] for s in reviewers if s["wall_clock_seconds"])
    exp_wall = sum(s["wall_clock_seconds"] for s in explorers if s["wall_clock_seconds"])
    wall = rev_wall + exp_wall

    findings = findings_count if findings_count is not None else "?"
    mc = max_comments if max_comments is not None else "?"

    line = (
        f"{run_dir.name} | {model} | "
        f"{findings}/{mc} findings | "
        f"{total_calls} calls ({len(explorers)} explores) | "
        f"{tok_str} tok in | "
        f"{wall:.0f}s | "
        f"{verdict}"
    )

    HISTORY_FILE.parent.mkdir(parents=True, exist_ok=True)
    with open(HISTORY_FILE, "a", encoding="utf-8") as f:
        f.write(line + "\n")


def print_history():
    """Print the last 20 entries from runs.log."""
    if not HISTORY_FILE.exists():
        print("No history yet.")
        return
    lines = HISTORY_FILE.read_text(encoding="utf-8").strip().split("\n")
    print(f"\n  Run history ({len(lines)} total, showing last 20):\n")
    for line in lines[-20:]:
        print(f"  {line}")
    print()


# ── Main ─────────────────────────────────────────────────────────────────────

def main():
    args = sys.argv[1:]
    log_file = None
    session_dir = None
    verbose = False
    show_history = False
    show_otel = False

    i = 0
    while i < len(args):
        if args[i] == "--log" and i + 1 < len(args):
            log_file = Path(args[i + 1])
            i += 2
        elif args[i] == "--verbose":
            verbose = True
            i += 1
        elif args[i] == "--history":
            show_history = True
            i += 1
        elif args[i] == "--otel":
            show_otel = True
            i += 1
        else:
            session_dir = Path(args[i])
            i += 1

    if show_history:
        print_history()
        if session_dir is None and log_file is None:
            return

    # Parse log early — we need it for session dir resolution and strategy detection
    log_text = None
    token_summaries = None
    findings_count = None
    max_comments = None
    parse_failure = None
    strategy = None
    if log_file and log_file.exists():
        log_text = log_file.read_text(encoding="utf-8")
        token_summaries = parse_token_summary(log_text)
        findings_count, max_comments = parse_findings_from_log(log_text)
        parse_failure = parse_parse_failure(log_text)
        strategy = parse_strategy_from_log(log_text)

    # Resolve session dir: explicit arg > log file > latest on disk
    if session_dir is None and log_text:
        session_dir = parse_session_dir_from_log(log_text)
    if session_dir is None:
        session_dir = find_latest_run(SESSIONS_ROOT)
        if not session_dir:
            print(f"No run-* directories found under {SESSIONS_ROOT}")
            sys.exit(1)

    sessions = load_sessions(session_dir) if session_dir.exists() else []

    # Strategies that use external session management (Copilot, ClaudeCode) don't write
    # session files through FileSessionProvider. Produce a minimal log-only report.
    if not sessions:
        _print_minimal_report(session_dir, strategy, findings_count, max_comments,
                              token_summaries, parse_failure)
        append_history(session_dir, findings_count, max_comments, [], token_summaries,
                       "NO_SESSIONS")
        return

    sessions_data = []
    budgets = []

    for idx, (name, session) in enumerate(sessions):
        messages = session.get("messages", [])
        agent = session.get("agent", "").lower()
        label = session.get("agent", "") or f"Session {idx + 1}"

        data = analyze_session(name, messages)
        data["agent"] = agent
        sessions_data.append(data)
        budgets.append(_measure_session(label, session))

    # Extract actual findings from reviewer session and match against expectations
    match_results = None
    extras = None
    actual_findings = extract_findings_from_sessions(sessions)
    if actual_findings is not None:
        all_expectations = load_all_expectations()
        # Try all scenarios, pick the one with the most matches
        best = None
        for _key, scenario in all_expectations.items():
            results, ex = match_findings(actual_findings, scenario["expectedFindings"])
            found = sum(1 for r in results if r["found"])
            if best is None or found > best[0]:
                best = (found, results, ex)
        if best and best[0] > 0:
            match_results, extras = best[1], best[2]

    session_findings_count = len(actual_findings) if actual_findings else 0

    verdict = print_report(
        session_dir, sessions_data, token_summaries, budgets,
        findings_count, max_comments, verbose,
        match_results, extras, parse_failure,
        session_findings_count,
    )

    if show_otel and token_summaries:
        print(f"\n## OTEL Log Timeline\n")
        for ts in token_summaries:
            print(f"  trace: {ts['trace_id']}")
            _print_otel_section(ts["trace_id"])
            print()

    append_history(
        session_dir, findings_count, max_comments,
        sessions_data, token_summaries, verdict,
    )


if __name__ == "__main__":
    main()
