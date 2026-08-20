# Repository automation

Lexarbor keeps automation under `.github/` and separates workflows by responsibility. No workflow deploys to a server or environment.

## File layout

| Path | Responsibility |
|---|---|
| `.github/workflows/ci.yml` | Backend tests and coverage, frontend type/build/browser checks, container integration, and image vulnerability scanning |
| `.github/workflows/dependency-review.yml` | Reject high- or critical-severity dependency vulnerabilities introduced by a pull request |
| `.github/workflows/security.yml` | CodeQL analysis for GitHub Actions, C#, and JavaScript/TypeScript on changes and weekly |
| `.github/workflows/registry-cleanup.yml` | Weekly deletion of container versions no tag reaches |
| `.github/workflows/release.yml` | Publish and attest the multi-platform GHCR image, as `edge` on a default-branch push and as the version tags on a release tag, then create a GitHub Release for a tag |
| `.github/dependabot.yml` | Weekly grouped updates for npm, NuGet, Docker base images, and GitHub Actions |
| `.github/release.yml` | Categories used by GitHub's generated release notes |
| `.github/scripts/` | Repository-owned scripts used by workflows and runnable locally |
| `frontend/e2e/` | Playwright administration UI scenarios with deterministic API fixtures |

Third-party Actions are pinned to full commit SHAs. Dependabot keeps those pins current and preserves the release tag in the adjacent comment. Workflows set read-only permissions by default and grant write access only to the two publishing jobs and the CodeQL result upload. Each publishing job requests only the scopes it needs: `publish-container` holds the registry and attestation scopes, `publish-release` holds `contents: write` alone.

## Continuous integration

CI runs on pull requests targeting `main`, manual dispatches, and calls from the release workflow. A push to `main` reaches it through that workflow's `verify` job rather than through a trigger of its own, so a commit is never built twice. Its jobs run in parallel:

- Backend: direct/transitive NuGet vulnerability audit, Release build, xUnit tests on Microsoft.Testing.Platform, TRX output, Cobertura coverage from `Microsoft.Testing.Extensions.CodeCoverage`, and a GitHub job summary. Results remain downloadable for 14 days.
- Frontend: npm vulnerability audit, type checks, production build, and Playwright Chromium scenarios covering session restoration, login, catalog rendering, and catalog creation. The browser is installed without `--with-deps`, because the runner image already supplies every library Chromium links against and the flag otherwise only adds font packages this suite never renders; the same command therefore works locally. Failure traces, screenshots, videos, and the HTML report remain downloadable for 7 days.
- Container: image build, health check without a bind mount, the container running as a non-root user and reporting its declared `HEALTHCHECK` as healthy, first-start database/configuration creation, preservation of pre-mounted files, a blocking scan for fixable critical vulnerabilities, and a build of the same AMD64/ARM64 pair the release workflow publishes. The image is built with a synthetic version that the container test must find in the startup log, so the argument the release workflow uses to stamp a tag is exercised on every run.

Superseded runs on the same branch are canceled. Every job has an explicit timeout.

Compiler warnings fail the backend build under CI, which `Directory.Build.props` enables through the same `CI` variable that sets `ContinuousIntegrationBuild`. A local build still only warns, so an unused variable in a half-finished edit does not stop work. NuGet audit warnings are excluded from that promotion: they report a newly published advisory rather than a defect in this code, and `assert-no-vulnerable-dotnet-packages.ps1` already fails the run for them one step later with the package, version, severity, and advisory listed.

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

The tag without its `v` prefix is passed into the image build as the `APP_VERSION` argument and becomes the assembly version, which the application writes to its startup log. It is not served over HTTP; see [Deployment](Deployment.md) for how to read it and why it is not on `/health`. A build that does not pass the argument reports `0.0.0-dev`, so an image built outside the release workflow is always distinguishable from a released one. No file in the repository stores the version, which means there is nothing to bump before tagging and no stored value that can disagree with the tag.

Steps 1 to 3 run in `publish-container` and step 4 in `publish-release`, which depends on it. A release therefore never announces a version whose image failed to publish. Each job names the refs it accepts instead of inferring them from the trigger: `publish-container` accepts a tag or the default branch, and `publish-release` accepts only a tag. A ref added to the trigger later, or a manual dispatch, therefore cannot publish an image or create a release without being named in those conditions as well.

A stable `v1.2.3` publishes `1.2.3`, `1.2`, `1`, and `latest`. A pre-release publishes only its full pre-release version and is marked as a GitHub pre-release. No long-lived registry credential is required because publishing uses the publishing jobs' short-lived `GITHUB_TOKEN`.

GitHub may create the container package as private on its first publication. For anonymous pulls from this public repository, open the package settings once, connect it to this repository if necessary, and change its visibility to public. Package visibility is deliberately not changed by the workflow.

## The edge image

Every push to `main` publishes `ghcr.io/<owner>/<repository>:edge` through the same `publish-container` job a release uses, with the same dependency on a full CI pass. An edge image is therefore not a lower bar than a release image; it is the same bar without a version.

```bash
docker pull ghcr.io/philfanzhou/lexarbor:edge
```

`edge` is a moving name and always points at the newest `main` build. Nothing published from `main` carries a permanent name: no per-commit tag rule is configured, and the attestation is pushed into the registry only for a release, because the referrer tag it writes is named after the image digest and would otherwise leave one permanent tag behind on every push. The attestation itself is recorded in the repository's attestation store for an edge image as well, so `gh attestation verify` covers both. Each push does leave the previous edge image and its platform manifests behind as untagged versions, which `registry-cleanup.yml` collects.

Three properties keep the edge path from reaching the release tags:

- `type=semver` derives its value from the ref and produces nothing on a branch, so no version tag can come from `main`.
- `type=raw,value=latest` has no ref of its own to fail on, so it carries `startsWith(github.ref, 'refs/tags/v')` explicitly. Without that test a default-branch push would move `latest` onto a development build.
- `validate-version` returns an empty version for any non-tag ref instead of deriving one. `${VERSION_TAG#v}` removes nothing from a branch name, so a derived value would have been the literal `main`. With the version empty the build argument is omitted entirely and the image keeps the Dockerfile's `0.0.0-dev` placeholder, which is how an edge image is told apart from a release at runtime.

`publish-release` carries its own `startsWith(github.ref, 'refs/tags/')` test rather than relying on the publishing job above it, so a default-branch push publishes an image and creates no GitHub Release.

A default-branch push runs CI once, through `verify`, so its checks appear under the release workflow rather than under CI. A pull request still runs CI directly and keeps the job names branch protection requires.

## Registry cleanup

`registry-cleanup.yml` runs weekly and deletes container versions that no tag reaches. It can also be dispatched manually, with a dry run and a retention period as inputs.

Untagged is not the same as unused. A multi-platform image is published as an index, and the platform manifests it points at carry no tags of their own, so the untagged versions sitting alongside `edge` are that image's own AMD64 manifest, ARM64 manifest, and attestation manifests. Deleting untagged versions, which is what a naive retention rule does, would delete the published image's parts and leave the tag resolving to an index whose children are gone.

`prune_registry_versions.py` therefore decides by reachability instead. It starts at every version carrying an ordinary tag, follows each index into its children, and keeps a `sha256-<digest>` referrer tag only while the subject it names is still reachable, so an attestation outlives its image by exactly nothing. Whatever is left over is unreferenced.

Three conditions stop the run rather than widen it, because each one would otherwise read as "nothing is reachable":

- the package reports no versions at all;
- no version carries an ordinary tag;
- a manifest cannot be read, which is not evidence that nothing points at it.

Unreferenced versions younger than the retention period, seven days by default, are held rather than deleted. A client that resolved an index moments ago may still be fetching its parts, and a publish in flight has not been tagged yet; both windows are minutes.

Deletion needs a token permitted to delete versions of this package, which is not something a token advertises. Before deleting anything the script asks about a version id that cannot exist: 404 means the request was authorized and merely found nothing, and 403 means a personal access token with `delete:packages` is required instead of `GITHUB_TOKEN`.

Run it locally against the real package without deleting:

```bash
GH_TOKEN=$(gh auth token) PACKAGE_OWNER=philfanzhou PACKAGE_NAME=Lexarbor   DRY_RUN=true python3 .github/scripts/prune_registry_versions.py
```

## Recommended repository rules

Protect `main` after the new workflows have completed once. Require these checks before merge:

- `Backend tests`
- `Frontend build and browser tests`
- `Container integration and vulnerability scan`
- `Review dependency changes`
- the three CodeQL language analyses

Also require pull requests, dismiss stale approvals if review is used, block force pushes and branch deletion, and keep administrators subject to the rule. These are repository settings rather than version-controlled workflow behavior.
