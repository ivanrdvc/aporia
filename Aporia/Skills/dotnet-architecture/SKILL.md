---
name: dotnet-architecture
description: ".NET pattern reference — standard patterns that are frequently misidentified as bugs. Check before reporting DI, HttpClient, auth, or async findings to avoid false positives."
---

Standard .NET patterns that are frequently misidentified as bugs during review.
Each resource describes how a pattern works and the one check that distinguishes
correct usage from a real bug. Not checking leads to false positives.

This list is not exhaustive. Issues not covered here are equally valid to report.

## Resources

Use `read_skill_resource("dotnet-architecture", "<filename>")` to load.

| Resource | Covers |
|---|---|
| `di-patterns.md` | DI lifetimes, `new` in singletons, keyed services, service locator |
| `http-client.md` | HttpClient lifecycle, IHttpClientFactory, socket management |
| `async-patterns.md` | ConfigureAwait, CancellationToken, async void, early returns |
| `auth-patterns.md` | Webhook secrets, auth attributes, token validation |
| `middleware-and-hosting.md` | Middleware ordering, config binding, hosted services, multiple registrations |
