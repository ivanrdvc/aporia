# Middleware and Hosting Patterns

## Middleware ordering
Normal when using endpoint routing — auth is per-endpoint via attributes/conventions, not middleware order.
Bug when `UseAuthentication()` is called after `UseAuthorization()`.
→ Check: does the app use endpoint routing or classic middleware pipeline?

## Config binding without `ValidateOnStart()`
Normal when config has safe defaults and the app works without explicit values.
Bug when required config (API keys, connection strings) silently defaults to empty and causes runtime failures.
→ Check: are the bound values required for the app to function?

## `BackgroundService` without `StoppingToken`
Normal when the work is idempotent and safe to interrupt (queue polling, short-lived operations).
Bug when interruption corrupts data or leaks resources (batch writes, held locks).
→ Check: what happens if the operation is killed mid-execution?

## Same interface registered multiple times
Normal for `IEnumerable<T>` injection (multiple handlers, validators, hosted services) or keyed services.
Bug when code expects a single `T` but multiple are registered — only the last one resolves.
→ Check: is the interface resolved as `T` or `IEnumerable<T>`?
