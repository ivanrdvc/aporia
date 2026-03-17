# DI Patterns

## `new X()` inside a singleton
Normal when X is a value object, DTO, or cheap helper with no dependencies.
Bug when X has injected dependencies, holds resources, or is registered in DI.
→ Check: does X's constructor take services?

## Keyed services (`AddKeyedSingleton`, `AddKeyedScoped`)
Normal for strategy/provider selection — multiple implementations registered with different keys.
Bug when the key string doesn't match between registration and resolution.
→ Check: search for the key string — does it appear in both `AddKeyed*` and `[FromKeyedServices]` or `GetKeyedService`?

## Singleton holding `IServiceScopeFactory`
Normal for background services that create a scope per work item.
Bug when scoped services are resolved from root `IServiceProvider` without creating a scope.
→ Check: is a scope created before resolving scoped services?

## `IOptions<T>` vs `IOptionsMonitor<T>`
Normal when config is read once at startup and never changes at runtime.
Bug when the app uses `reloadOnChange: true` and the service should react to config changes.
→ Check: does the config source reload, and does the service need to see updates?
