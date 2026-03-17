# HttpClient Patterns

## `new HttpClient()` in a singleton or static field
Normal when the instance is stored and reused for the app lifetime — thread-safe, recommended for long-lived clients.
Bug when a new instance is created per request, per scope, or inside a `using` block — causes socket exhaustion.
→ Check: is the HttpClient stored in a field and reused, or created and discarded?

## `IHttpClientFactory` not used
Normal when a static/singleton HttpClient is reused, or the code is a console app/Function/test with a single client.
Bug when multiple named/typed clients with different configs are needed, or handler rotation matters.
→ Check: is there a single long-lived client, or does the code need factory features?
