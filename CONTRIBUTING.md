# Contributing to Lexarbor

Thank you for improving Lexarbor. Keep changes focused, preserve the public API contract, and include verification appropriate to the affected component.

## Before opening a pull request

1. Create a branch from the latest `main`.
2. Follow the placement rules in [Repository layout](docs/development/RepositoryLayout.md).
3. Do not commit credentials, databases, generated frontend assets, test results, or local configuration.
4. Update tests and documentation when behavior or operational contracts change.

## Local verification

Run the checks relevant to your change; run all of them before requesting merge for cross-cutting changes. Backend tests start a disposable PostgreSQL server through Testcontainers, so Docker must be available (see [Testing](docs/development/Testing.md#test-database)). PostgreSQL is the only supported storage ([ADR-011](docs/adr/ADR-011-postgresql-only-storage.md)); legacy installations upgrade through v0.2.0-rc.2. Keep the startup check for a completed import when the old file is present.

```bash
dotnet restore Lexarbor.sln
dotnet build Lexarbor.sln --configuration Release --no-restore
dotnet test --solution Lexarbor.sln --configuration Release --no-build  # requires Docker

cd frontend
npm ci
npm run test:types
npx playwright install chromium
npm run test:e2e
```

Container or persistence changes also require:

```bash
docker build -t lexarbor:ci .
bash .github/scripts/test-container.sh lexarbor:ci
```

## Pull requests

- Explain the problem and the chosen solution.
- Describe any deployment or configuration impact.
- Write issue and pull request bodies, reviews, replies, and progress updates in plain Chinese: state directly what was done, the result, and what remains. Give necessary terms a concrete meaning, and keep code, paths, commands, and formal names as written. Use an English conventional commit format for the pull request title (`feat:` / `fix:` / `docs:` / `test:` / `refactor:` / `chore:`).

By contributing, you agree that your contribution is licensed under the repository's [MIT License](LICENSE).
