# Repository layout and file ownership

This is a single repository with separate frontend and backend development and a single-image delivery. Directories are divided into production sources, tests, documentation, and operations and GitHub governance, so that build output and tooling files never mix into product code.

```text
Lexarbor/
├── .config/                 .NET local tool manifest
├── .github/                 Actions, Dependabot, issue and PR templates, and CI-only scripts
├── docs/                    Architecture, ADRs, frontend specification, development and deployment docs
├── frontend/                Vue administration frontend with its type and browser tests
├── scripts/                 Scripts a user or an operator runs deliberately
├── src/Lexarbor.Domain/     Domain model, rules, and repository abstractions
├── src/Lexarbor.Database/   EF Core, SQLite, migrations, repository implementations, and seed data
├── src/Lexarbor.Service/    HTTP contract, DTOs, conversions, and exception mapping
├── src/Lexarbor.Host/       Composition root, authentication, persistent configuration, and entry point
├── tests/                   .NET test projects, one per production project
├── Directory.Build.props    Compilation settings shared by every .NET project
├── Directory.Packages.props The single source of truth for NuGet versions
├── Dockerfile               Build entry point for the production frontend and backend image
└── Lexarbor.sln             Root solution entry point
```

## Placement rules

- C# code that is deployable or referenced by a production project goes in `src/`, with the directory name matching the project name.
- .NET unit and integration tests go in the root `tests/`; frontend-only tests stay inside `frontend/`.
- Every document maintained over the long term belongs in `docs/`. The repository root keeps only the community files GitHub recognizes and the build entry points.
- `.github/scripts/` serves GitHub Actions only; scripts a user runs deliberately go in `scripts/`.
- Frontend build output goes only to `frontend/dist/`, and is copied into the Host's `wwwroot` during the container build, so no static build directory appears at the repository root.
- NuGet package versions are declared only in `Directory.Packages.props`; each `.csproj` expresses dependencies and project-specific metadata only.
- A new architecture decision goes in `docs/adr/ADR-NNN-title.md`. Do not leave working design notes at the repository root.

## What must not be committed

Databases, runtime configuration, secrets, `bin/`, `obj/`, `TestResults/`, `frontend/dist/`, Playwright reports, and local IDE configuration are all excluded by the ignore rules. Example configuration meant for distribution must have its secrets removed and must use an unmistakably named example file.
