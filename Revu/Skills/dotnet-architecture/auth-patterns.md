# Auth Patterns

## Empty webhook secret / HMAC skip
Normal when another auth layer exists (Function key auth, API Management, IP allowlist, gateway).
Bug when the endpoint is publicly accessible with no other auth and the secret check is skipped.
→ Check: is there a fallback auth mechanism at the infrastructure level?

## No `[Authorize]` on an endpoint
Normal when auth is applied globally via middleware/convention, or the endpoint is intentionally anonymous (health checks, webhooks).
Bug when the endpoint handles sensitive data and no auth exists at any layer.
→ Check: is `RequireAuthorization()` applied globally, or is this an anonymous endpoint by design?

## Custom token validation instead of middleware
Normal when the token format isn't JWT (webhook signatures, API keys) or validation is conditional per request.
Bug when standard JWT/OAuth tokens are validated manually instead of using `AddJwtBearer()`.
→ Check: is the token format non-standard?
