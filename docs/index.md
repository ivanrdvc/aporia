# Documentation

## Design

1. [Architecture](architecture.md) — strategy layer, CoreStrategy reviewer-explorer design, agent-as-tool pattern
2. [Internals](internals.md) — GetDiff mechanics, code search behavior, Cosmos containers
3. [Observability](observability.md) — OpenTelemetry setup, custom metrics, App Insights KQL queries

## Features

- [Incremental Reviews](features/incremental-reviews.md) — iteration tracking, comment dedup, Cosmos persistence
- [Code Graph](features/code-graph.md) — tree-sitter structural index, callers/implementations/dependents queries, reviewer tool

## Plans

- [Commit Messages as Review Context](../commit-messages-plan.md) — include PR commit messages in the reviewer prompt for author intent
