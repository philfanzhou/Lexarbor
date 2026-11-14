# Business API key release notes

## Breaking changes

- All existing catalog and submission business routes now require a Lexarbor-issued
  API key in `X-Api-Key`. Catalog clients formerly called anonymously. Submission
  clients formerly used their self-signed JWT. Issue and configure keys during
  the upgrade window. Use the old submission subject as the new caller ID.
- `RateLimits:PublicApi` is retired. Remove it from persistent settings, environment
  variables and launch arguments. The administrator rate-limit API no longer
  returns `publicApi` or accepts `publicApiEnabled`.

The SQLite upgrade adds `business_api_key`. The administration UI provides key
issuance, metadata listing and revocation under `/#/settings/api-keys`.
See [upgrade and rollback steps](Deployment.md#business-api-key-upgrade) and
[Business API keys](../api/BusinessApiKeys.md).
