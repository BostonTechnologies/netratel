# Machine-to-machine authentication

What
- OAuth 2.0 client credentials between NetRatel.API and an external service.
- Short-lived JWTs validated via JWKS; audience-scoped per API.

Configure
- appsettings.json keys:
  - M2M: Authority, Audience, AllowedCallerClientIds, AccessTokenLifetimeMinutes
  - M2MClients:<client_id>: Secret, AllowedAudiences, AllowedScopes
- NetRatelApi: BaseUrl (for an external-service connectivity default)
- Local development values (never commit a real secret):
  - dotnet user-secrets set "M2M:Authority" "https://localhost:5001"
  - dotnet user-secrets set "M2M:Audience" "netratel.api"
  - dotnet user-secrets set "M2M:AllowedCallerClientIds:0" "example-client"
  - dotnet user-secrets set "M2MClients:example-client:Secret" "replace-with-a-local-test-secret"
  - dotnet user-secrets set "M2MClients:example-client:AllowedAudiences:0" "netratel.api"
  - dotnet user-secrets set "M2MClients:example-client:AllowedScopes:0" "netratel.api"

Env vars (prod)
- M2M__Authority, M2M__Audience, M2M__AllowedCallerClientIds__0, M2M__AccessTokenLifetimeMinutes
- M2MClients__example-client__Secret
- M2MClients__example-client__AllowedAudiences__0
- M2MClients__example-client__AllowedScopes__0

Endpoints
- POST /connect/token (grant_type=client_credentials)
- GET /.well-known/openid-configuration
- GET /.well-known/jwks.json
- GET /internal/health: RequireAuthorization("M2MOnly")
- POST /internal/ingest: RequireAuthorization("M2MOnly")
- GET /api/v1/system/m2m/ping: RequireAuthorization(M2M scheme + M2MOnly policy), returns caller identity + claims

Example curl
- curl -X POST "https://localhost:5001/connect/token" \
  -H "Content-Type: application/x-www-form-urlencoded" \
  -d "grant_type=client_credentials&client_id=example-client&client_secret=replace-with-a-local-test-secret&scope=netratel.api"

Notes
- Existing user flows remain unchanged.
