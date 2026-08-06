# Repository automation

Lexarbor keeps automation under `.github/` and separates workflows by responsibility. No workflow deploys to a server or environment.

## File layout

| Path | Responsibility |
|---|---|
| `.github/workflows/ci.yml` | Backend tests and coverage, frontend type/build/browser checks, container integration, and image vulnerability scanning |
| `.github/workflows/dependency-review.yml` | Reject high- or critical-severity dependency vulnerabilities introduced by a pull request |
| `.github/workflows/security.yml` | CodeQL analysis for GitHub Actions, C#, and JavaScript/TypeScript on changes and weekly |
| `.github/workflows/release.yml` | Verify a version tag, publish the multi-platform GHCR image, attest it, and create a GitHub Release |
| `.github/dependabot.yml` | Weekly grouped updates for npm, NuGet, Docker base images, and GitHub Actions |
| `.github/release.yml` | Categories used by GitHub's generated release notes |
| `.github/scripts/` | Repository-owned scripts used by workflows and runnable locally |
| `frontend/e2e/` | Playwright administration UI scenarios with deterministic API fixtures |

Third-party Actions are pinned to full commit SHAs. Dependabot keeps those pins current and preserves the release tag in the adjacent comment. Workflows set read-only permissions by default and grant write access only to the release job and CodeQL result upload.

## Continuous integration

CI runs on pushes to `main`, pull requests targeting `main`, manual dispatches, and calls from the release workflow. Its jobs run in parallel:

- Backend: direct/transitive NuGet vulnerability audit, Release build, xUnit tests, TRX output, Coverlet Cobertura coverage, and a GitHub job summary. Results remain downloadable for 14 days.
- Frontend: npm vulnerability audit, type checks, production build, and Playwright Chromium scenarios covering session restoration, login, catalog rendering, and catalog creation. Failure traces, screenshots, videos, and the HTML report remain downloadable for 7 days.
- Container: image build, health check without a bind mount, first-start database/configuration creation, preservation of pre-mounted files, and a blocking scan for fixable critical vulnerabilities.

Superseded runs on the same branch are canceled. Every job has an explicit timeout.

Run the repository-owned checks locally with:

```bash
dotnet test src/Lexarbor.sln --configuration Release --collect "XPlat Code Coverage"

cd frontend
npm ci
npx playwright install chromium
npm run test:e2e

cd ..
docker build -f src/Host/Dockerfile -t lexarbor:ci .
bash .github/scripts/test-container.sh lexarbor:ci
```

The browser tests mock only the external administration API contract. Backend authentication and HTTP behavior remain covered by the .NET integration tests, while container behavior is exercised against the real published application.

## Security automation

CodeQL uses the extended security query suite for workflow, backend, and frontend code. Dependency Review runs only for pull requests and prevents newly introduced high- or critical-severity vulnerable dependencies. Trivy examines both operating-system and application packages in the final image and blocks fixable critical vulnerabilities.

Dependabot checks four ecosystems every Monday in the `Asia/Singapore` timezone and groups related updates to reduce pull-request noise. Repository vulnerability alerts and Dependabot security updates must also remain enabled in GitHub repository settings.

## Releases and container tags

Push an annotated or lightweight semantic-version tag to start a release:

```bash
git tag -a v1.2.3 -m "Lexarbor v1.2.3"
git push origin v1.2.3
```

Accepted tags are `vMAJOR.MINOR.PATCH` and optional pre-release variants such as `v1.2.3-rc.1`. The release workflow first calls the complete CI workflow. Only after it passes does the workflow:

1. build Linux AMD64 and ARM64 images;
2. publish the versioned image to `ghcr.io/<owner>/<repository>`;
3. attach OCI labels, an SBOM, maximum BuildKit provenance, and a GitHub artifact attestation;
4. create a GitHub Release with generated notes.

A stable `v1.2.3` publishes `1.2.3`, `1.2`, `1`, and `latest`. A pre-release publishes only its full pre-release version and is marked as a GitHub pre-release. No long-lived registry credential is required because publishing uses the release job's short-lived `GITHUB_TOKEN`.

GitHub may create the container package as private on its first publication. For anonymous pulls from this public repository, open the package settings once, connect it to this repository if necessary, and change its visibility to public. Package visibility is deliberately not changed by the workflow.

## Recommended repository rules

Protect `main` after the new workflows have completed once. Require these checks before merge:

- `Backend tests`
- `Frontend build and browser tests`
- `Container integration and vulnerability scan`
- `Review dependency changes`
- the three CodeQL language analyses

Also require pull requests, dismiss stale approvals if review is used, block force pushes and branch deletion, and keep administrators subject to the rule. These are repository settings rather than version-controlled workflow behavior.
