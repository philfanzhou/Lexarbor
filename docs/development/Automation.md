# Repository automation

Lexarbor keeps automation under `.github/` and separates workflows by responsibility. No workflow deploys to a server or environment.

## File layout

| Path | Responsibility |
|---|---|
| `.github/workflows/ci.yml` | Backend tests and coverage, frontend type/build/browser checks, container integration, and image vulnerability scanning |
| `.github/workflows/dependency-review.yml` | Reject high- or critical-severity dependency vulnerabilities introduced by a pull request |
| `.github/workflows/security.yml` | CodeQL analysis for GitHub Actions, C#, and JavaScript/TypeScript on changes and weekly |
| `.github/workflows/release.yml` | Verify a version tag, publish and attest the multi-platform GHCR image, then create a GitHub Release |
| `.github/dependabot.yml` | Weekly grouped updates for npm, NuGet, Docker base images, and GitHub Actions |
| `.github/release.yml` | Categories used by GitHub's generated release notes |
| `.github/scripts/` | Repository-owned scripts used by workflows and runnable locally |
| `frontend/e2e/` | Playwright administration UI scenarios with deterministic API fixtures |

Third-party Actions are pinned to full commit SHAs. Dependabot keeps those pins current and preserves the release tag in the adjacent comment. Workflows set read-only permissions by default and grant write access only to the two publishing jobs and the CodeQL result upload. Each publishing job requests only the scopes it needs: `publish-container` holds the registry and attestation scopes, `publish-release` holds `contents: write` alone.

## Continuous integration

CI runs on pushes to `main`, pull requests targeting `main`, manual dispatches, and calls from the release workflow. Its jobs run in parallel:

- Backend: direct/transitive NuGet vulnerability audit, Release build, xUnit tests on Microsoft.Testing.Platform, TRX output, Cobertura coverage from `Microsoft.Testing.Extensions.CodeCoverage`, and a GitHub job summary. Results remain downloadable for 14 days.
- Frontend: npm vulnerability audit, type checks, production build, and Playwright Chromium scenarios covering session restoration, login, catalog rendering, and catalog creation. Failure traces, screenshots, videos, and the HTML report remain downloadable for 7 days.
- Container: image build, health check without a bind mount, first-start database/configuration creation, preservation of pre-mounted files, a blocking scan for fixable critical vulnerabilities, and a build of the same AMD64/ARM64 pair the release workflow publishes. The image is built with a synthetic version that the health check must find reported back, so the argument the release workflow uses to stamp a tag is exercised on every run.

Superseded runs on the same branch are canceled. Every job has an explicit timeout.

Run the repository-owned checks locally with:

```bash
dotnet test Lexarbor.sln --configuration Release -- --coverage --coverage-output-format cobertura

cd frontend
npm ci
npx playwright install chromium
npm run test:e2e

cd ..
docker build -t lexarbor:ci .
bash .github/scripts/test-container.sh lexarbor:ci
```

The browser tests mock only the external administration API contract. Backend authentication and HTTP behavior remain covered by the .NET integration tests, while container behavior is exercised against the real published application.

## Security automation

CodeQL uses the extended security query suite for workflow, backend, and frontend code. Dependency Review runs only for pull requests and prevents newly introduced high- or critical-severity vulnerable dependencies. Trivy examines both operating-system and application packages in the final image and blocks fixable critical vulnerabilities.

Dependabot checks four ecosystems every Monday in the `Asia/Singapore` timezone and groups related updates to reduce pull-request noise. Routine version updates stay within the current major version; deliberate platform-major upgrades remain maintainer-led changes. Security updates are still generated independently. Repository vulnerability alerts and Dependabot security updates must also remain enabled in GitHub repository settings.

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

The tag without its `v` prefix is passed into the image build as the `APP_VERSION` argument and becomes the assembly version, which the application reports as `version` in its `/health` response. A build that does not pass the argument reports `0.0.0-dev`, so an image built outside the release workflow is always distinguishable from a released one. No file in the repository stores the version, which means there is nothing to bump before tagging and no stored value that can disagree with the tag.

Steps 1 to 3 run in `publish-container` and step 4 in `publish-release`, which depends on it. A release therefore never announces a version whose image failed to publish. Both jobs additionally guard on `startsWith(github.ref, 'refs/tags/')`, which is redundant against the tag-only trigger and is kept so that adding a branch trigger or a manual dispatch later cannot publish from a non-tag ref.

A stable `v1.2.3` publishes `1.2.3`, `1.2`, `1`, and `latest`. A pre-release publishes only its full pre-release version and is marked as a GitHub pre-release. No long-lived registry credential is required because publishing uses the publishing jobs' short-lived `GITHUB_TOKEN`.

GitHub may create the container package as private on its first publication. For anonymous pulls from this public repository, open the package settings once, connect it to this repository if necessary, and change its visibility to public. Package visibility is deliberately not changed by the workflow.

## Recommended repository rules

Protect `main` after the new workflows have completed once. Require these checks before merge:

- `Backend tests`
- `Frontend build and browser tests`
- `Container integration and vulnerability scan`
- `Review dependency changes`
- the three CodeQL language analyses

Also require pull requests, dismiss stale approvals if review is used, block force pushes and branch deletion, and keep administrators subject to the rule. These are repository settings rather than version-controlled workflow behavior.
