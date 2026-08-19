#!/usr/bin/env python3
"""Delete container package versions that no tag can reach.

A multi-platform image is published as an index, and the platform manifests it
points at carry no tags of their own. In the packages API they are therefore
indistinguishable from garbage: the untagged versions sitting alongside the
current edge tag are that image's own amd64 manifest, arm64 manifest, and its
attestation manifests. Deleting untagged versions would delete the published
image's parts and leave the tag resolving to an index whose children are gone,
which is why this walks the graph instead.

Reachability starts at every version carrying an ordinary tag, follows every
index into its children, and additionally keeps a ``sha256-<digest>`` referrer
tag while the subject it names is itself still reachable. Anything left over is
genuinely unreferenced.

Usage:
    GH_TOKEN=... PACKAGE_OWNER=octocat PACKAGE_NAME=example \
        python3 .github/scripts/prune_registry_versions.py
"""

from __future__ import annotations

import json
import os
import re
import sys
import urllib.error
import urllib.request
from datetime import datetime, timedelta, timezone

API = "https://api.github.com"
REGISTRY = "https://ghcr.io"
REFERRER_TAG = re.compile(r"^sha256-[0-9a-f]{64}$")
MANIFEST_ACCEPT = ", ".join(
    (
        "application/vnd.oci.image.index.v1+json",
        "application/vnd.oci.image.manifest.v1+json",
        "application/vnd.docker.distribution.manifest.list.v2+json",
        "application/vnd.docker.distribution.manifest.v2+json",
    )
)


class Abort(Exception):
    """A condition under which deleting anything would be unsafe."""


def require(name: str, default: str | None = None) -> str:
    value = os.environ.get(name) or default
    if not value:
        raise Abort(f"{name} is required")
    return value


def request(url: str, token: str, method: str = "GET", accept: str = "application/vnd.github+json"):
    req = urllib.request.Request(url, method=method)
    req.add_header("Authorization", f"Bearer {token}")
    req.add_header("Accept", accept)
    req.add_header("User-Agent", "lexarbor-registry-cleanup")
    return urllib.request.urlopen(req, timeout=60)


def api_paginated(path: str, token: str) -> list:
    """Every page of a list endpoint, followed through the Link header."""
    url = f"{API}/{path}"
    items: list = []
    while url:
        with request(url, token) as response:
            items.extend(json.load(response))
            link = response.headers.get("Link", "")
        match = re.search(r'<([^>]+)>;\s*rel="next"', link)
        url = match.group(1) if match else None
    return items


def registry_token(repository: str, token: str) -> str:
    """A pull-scoped registry token, which is separate from the API token."""
    url = f"{REGISTRY}/token?scope=repository:{repository}:pull&service=ghcr.io"
    req = urllib.request.Request(url)
    req.add_header("User-Agent", "lexarbor-registry-cleanup")
    # The registry exchanges a GitHub token presented as basic auth for one of
    # its own; the username is not checked.
    import base64

    basic = base64.b64encode(f"x-access-token:{token}".encode()).decode()
    req.add_header("Authorization", f"Basic {basic}")
    with urllib.request.urlopen(req, timeout=60) as response:
        value = json.load(response).get("token")
    if not value:
        raise Abort("Could not obtain a registry pull token.")
    return value


def reachable_digests(versions: list[dict], repository: str, token: str) -> set[str]:
    """Every digest some tag leads to, directly or through an index."""
    seen: set[str] = set()

    def drain(queue: list[str]) -> None:
        while queue:
            digest = queue.pop()
            if digest in seen:
                continue
            seen.add(digest)
            url = f"{REGISTRY}/v2/{repository}/manifests/{digest}"
            try:
                with request(url, token, accept=MANIFEST_ACCEPT) as response:
                    body = json.load(response)
            except urllib.error.URLError as error:
                # A manifest that cannot be read is not evidence that nothing
                # points at it. Carrying on would treat its children as
                # unreferenced and delete them.
                raise Abort(f"Could not read manifest {digest}: {error}") from error
            queue.extend(child["digest"] for child in body.get("manifests", []))

    ordinary = [
        version["name"]
        for version in versions
        if any(not REFERRER_TAG.match(tag) for tag in tags_of(version))
    ]
    # A package carrying no ordinary tag at all is not a state these workflows
    # can produce, so treat it as a failed read rather than as an instruction.
    if not ordinary:
        raise Abort("No ordinarily tagged version found; refusing to continue.")
    drain(ordinary)

    # A referrer tag is named after its subject rather than after anything a
    # person chose, so it is only worth keeping while that subject survives.
    # Repeated to a fixed point because a kept referrer can pull in more of its
    # own.
    while True:
        added = []
        for version in versions:
            if version["name"] in seen:
                continue
            for tag in tags_of(version):
                if REFERRER_TAG.match(tag) and f"sha256:{tag[len('sha256-'):]}" in seen:
                    added.append(version["name"])
                    break
        if not added:
            return seen
        drain(added)


def tags_of(version: dict) -> list[str]:
    return version.get("metadata", {}).get("container", {}).get("tags", []) or []


def parse_timestamp(value: str) -> datetime:
    """A GitHub timestamp, with or without the fractional seconds it sometimes
    carries. Guessing one form and crashing on the other would fail a scheduled
    run for a reason nobody would look for."""
    text = value.replace("Z", "+00:00")
    return datetime.fromisoformat(text).astimezone(timezone.utc)


def main() -> int:
    # GITHUB_REPOSITORY is set for every event, which the repository object in a
    # workflow payload is not guaranteed to be.
    owner_default, _, name_default = os.environ.get("GITHUB_REPOSITORY", "").partition("/")
    owner = require("PACKAGE_OWNER", owner_default)
    package = require("PACKAGE_NAME", name_default).lower()
    retention_days = int(require("RETENTION_DAYS", "7"))
    dry_run = require("DRY_RUN", "true").lower() == "true"
    token = require("GH_TOKEN")

    repository = f"{owner}/{package}".lower()

    # The packages API path differs for a user and an organization, and picking
    # the wrong one answers 404 rather than anything that hints at the cause.
    with request(f"{API}/users/{owner}", token) as response:
        owner_type = json.load(response)["type"]
    if owner_type == "Organization":
        base = f"orgs/{owner}/packages/container/{package}"
    elif owner_type == "User":
        base = f"users/{owner}/packages/container/{package}"
    else:
        raise Abort(f"Unexpected owner type: {owner_type}")

    print(f"Package     {base}")
    print(f"Registry    ghcr.io/{repository}")
    print(f"Retention   {retention_days} days")
    print(f"Dry run     {dry_run}\n")

    versions = api_paginated(f"{base}/versions?per_page=100", token)
    # An empty inventory can only mean the query failed or the package moved.
    # Every test below would then read "nothing is reachable" and delete the lot.
    if not versions:
        raise Abort("The package reports no versions; refusing to continue.")

    reachable = reachable_digests(versions, repository, registry_token(repository, token))
    cutoff = datetime.now(timezone.utc) - timedelta(days=retention_days)

    kept, held, doomed = 0, 0, []
    for version in sorted(versions, key=lambda v: v["created_at"], reverse=True):
        digest, tags = version["name"], ",".join(tags_of(version)) or "-"
        created = parse_timestamp(version["created_at"])
        if digest in reachable:
            kept += 1
            print(f"  keep    {tags:<22.22} {digest[7:23]}")
        elif created >= cutoff:
            # Nothing points at it, but a client that resolved an index moments
            # ago may still be fetching its parts, and a publish in flight has
            # not been tagged yet. Both windows are minutes; retention is days.
            held += 1
            print(f"  hold    {tags:<22.22} {digest[7:23]}  (created {version['created_at']})")
        else:
            doomed.append(version["id"])
            print(f"  DELETE  {tags:<22.22} {digest[7:23]}  (created {version['created_at']})")

    print(
        f"\n{len(versions)} versions: {kept} reachable, "
        f"{held} within the retention window, {len(doomed)} unreferenced."
    )

    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a", encoding="utf-8") as handle:
            handle.write("### Registry cleanup\n\n")
            handle.write("| Versions | Reachable | Held | Unreferenced |\n|---|---|---|---|\n")
            handle.write(f"| {len(versions)} | {kept} | {held} | {len(doomed)} |\n")
            if dry_run:
                handle.write("\nDry run: nothing was deleted.\n")

    if not doomed:
        print("Nothing to delete.")
        return 0
    if dry_run:
        print("Dry run; no version was deleted.")
        return 0

    # Whether GITHUB_TOKEN may delete versions of a user-owned package is not
    # something the token itself advertises. Ask about an id that cannot exist:
    # 404 means the request was authorized and merely found nothing, 403 means
    # it was not, and learning which costs one request and no data.
    try:
        request(f"{API}/{base}/versions/999999999", token, method="DELETE").close()
        raise Abort("The delete permission probe unexpectedly succeeded; aborting.")
    except urllib.error.HTTPError as error:
        if error.code == 403:
            raise Abort(
                f"This token may not delete versions of {base} (403). "
                "A personal access token with delete:packages is required."
            ) from error
        if error.code != 404:
            raise Abort(f"Unexpected {error.code} from the delete permission probe.") from error

    failed = 0
    for version_id in doomed:
        try:
            request(f"{API}/{base}/versions/{version_id}", token, method="DELETE").close()
            print(f"  deleted {version_id}")
        except urllib.error.HTTPError as error:
            print(f"  failed  {version_id} (HTTP {error.code})", file=sys.stderr)
            failed += 1
    if failed:
        raise Abort(f"{failed} version(s) could not be deleted.")
    print(f"Deleted {len(doomed)} version(s).")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Abort as abort:
        print(f"error: {abort}", file=sys.stderr)
        sys.exit(1)
    except urllib.error.HTTPError as error:
        # Anything unhandled above reaches here having deleted nothing, because
        # every request that could delete is made after the inventory is read.
        print(f"error: {error.code} from {error.url}", file=sys.stderr)
        sys.exit(1)
