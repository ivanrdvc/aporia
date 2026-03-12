#!/usr/bin/env python3
"""
Dump session messages as readable text.

Usage:
    python3 dump-session.py [session-dir] [-o output.txt]

If session-dir is omitted, auto-discovers the latest run-* directory.
If -o is omitted, prints to stdout.
"""

import json
import sys
import textwrap
from pathlib import Path

SESSIONS_ROOT = (
    Path(__file__).resolve().parents[4]
    / "tests" / "Revu.Tests.Integration" / "bin" / "Debug" / "net10.0" / "sessions"
)


def find_latest_run(root: Path):
    runs = sorted(root.glob("run-*"), key=lambda p: p.name, reverse=True)
    return runs[0] if runs else None


def fmt_content(content):
    t = content.get("$type", "")
    if t == "text":
        return content.get("Text", "")
    elif t == "functionCall":
        name = content.get("Name", "?")
        args = content.get("Arguments", {})
        return f">> {name}({json.dumps(args, indent=2)})"
    elif t == "functionResult":
        result = content.get("Result", "")
        return f"<< result:\n{result}"
    return f"[{t}] {json.dumps(content)}"


def dump_session(path, session, out):
    agent = session.get("agent", "Unknown")
    messages = session.get("messages", [])

    out.write(f"\n{'=' * 70}\n")
    out.write(f"  {path.name}  |  agent: {agent}  |  messages: {len(messages)}\n")
    out.write(f"{'=' * 70}\n\n")

    for msg in messages:
        role = msg.get("Role", "?")
        ts = msg.get("CreatedAt", "")
        ts_str = f"  [{ts}]" if ts else ""

        out.write(f"--- {role}{ts_str} ---\n")
        for content in msg.get("Contents", []):
            text = fmt_content(content)
            if text:
                out.write(f"{text}\n")
        out.write("\n")


def main():
    args = sys.argv[1:]
    session_dir = None
    output_file = None

    i = 0
    while i < len(args):
        if args[i] == "-o" and i + 1 < len(args):
            output_file = args[i + 1]
            i += 2
        else:
            session_dir = Path(args[i])
            i += 1

    if session_dir is None:
        session_dir = find_latest_run(SESSIONS_ROOT)
        if not session_dir:
            print(f"No run-* directories found under {SESSIONS_ROOT}")
            sys.exit(1)

    if not session_dir.exists():
        print(f"Directory not found: {session_dir}")
        sys.exit(1)

    out = open(output_file, "w", encoding="utf-8") if output_file else sys.stdout

    out.write(f"Session dump: {session_dir.name}\n")
    out.write(f"Path: {session_dir}\n")

    for f in sorted(session_dir.glob("*.json")):
        with open(f, encoding="utf-8") as fh:
            raw = json.load(fh)
        if isinstance(raw, dict) and "messages" in raw:
            dump_session(f, raw, out)
        else:
            dump_session(f, {"messages": raw}, out)

    if output_file:
        out.close()
        print(f"Written to {output_file}")


if __name__ == "__main__":
    main()
