---
name: dotnet-architecture
description: ".NET architecture patterns — dependency injection, middleware pipelines, configuration binding, async best practices, project structure conventions"
---

When reviewing .NET architecture:

- Check for correct DI lifetimes (Scoped for request-state, Singleton for stateless, Transient sparingly)
- Verify async methods propagate CancellationToken and avoid async void
- Ensure IOptions<T>/IOptionsSnapshot<T>/IOptionsMonitor<T> usage matches the reload semantics needed
- Look for missing ConfigureAwait(false) in library code (not needed in app-level ASP.NET code)
- Check that IDisposable/IAsyncDisposable resources are properly disposed or registered with DI
- Validate middleware ordering (auth before authorization, exception handler first)
- Flag direct new-ing of services that should come from DI
- Check for correct use of ILogger<T> category naming
