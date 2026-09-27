#!/usr/bin/env python3
"""Verify real Host publish artifacts and startup logs and authorized HTTP; no replacement Host service."""

import json
import os
from pathlib import Path
import re
import shutil
import signal
import select
import socket
import subprocess
import tempfile
import time
import urllib.error
import urllib.request


REPOSITORY = Path(__file__).resolve().parents[2]
SHA_A = "0123456789abcdef0123456789abcdef01234567"
SHA_B = "abcdef0123456789abcdef0123456789abcdef01"
ROWS = [
    ("release", "1.2.3", SHA_A, "release"),
    ("prerelease", "1.3.0-rc.1", SHA_A, "release"),
    ("edge-a", None, SHA_A, "edge"),
    ("edge-b", None, SHA_B, "edge"),
    ("default", None, None, None),
    ("missing-version", None, None, None),
]
PROBE_PROJECT = """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>
</Project>
"""
PROBE_SOURCE = """using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(args[0]));
var identity = assembly.GetType("Lexarbor.Host.ApplicationVersion", throwOnError: true)!;
Console.WriteLine(JsonSerializer.Serialize(new
{
    Version = identity.GetProperty("Current")!.GetValue(null),
    Revision = identity.GetProperty("Revision")!.GetValue(null),
    Channel = identity.GetProperty("Channel")!.GetValue(null),
    InformationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
    Metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .ToDictionary(attribute => attribute.Key, attribute => attribute.Value)
}));
"""


def stop(process):
    if process.poll() is None:
        # Include compiler/build child processes when interrupted or timed out.
        os.killpg(process.pid, signal.SIGTERM)
        try:
            process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            os.killpg(process.pid, signal.SIGKILL)
            process.wait()


def run(command, log, **kwargs):
    with log.open("w") as output:
        process = subprocess.Popen(command, stdout=output, stderr=subprocess.STDOUT,
                                   start_new_session=True, **kwargs)
        try:
            if process.wait(timeout=300) != 0:
                raise RuntimeError(f"Command failed; see {log}")
        finally:
            stop(process)


def verify_startup(output, expected, environment, log, token):
    # Host deliberately has a fixed port. Never mistake another server for this one.
    with socket.socket() as available:
        available.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        available.bind(("127.0.0.1", 5008))
    with log.open("w") as stream:
        process = subprocess.Popen(["dotnet", str(output / "Lexarbor.Host.dll")],
                                   cwd=output, env=environment, stdout=stream,
                                   stderr=subprocess.STDOUT, start_new_session=True)
        try:
            deadline = time.monotonic() + 45
            while time.monotonic() < deadline:
                if process.poll() is not None:
                    raise RuntimeError(f"Host exited before health; see {log}")
                if "Now listening on:" in log.read_text():
                    try:
                        with urllib.request.urlopen("http://127.0.0.1:5008/health", timeout=2) as response:
                            health = json.load(response)
                        assert health == {"success": True, "data": {"status": "healthy"}}, health
                        break
                    except (urllib.error.URLError, TimeoutError):
                        pass
                time.sleep(0.1)
            else:
                raise RuntimeError(f"Host startup timed out; see {log}")
            request = urllib.request.Request("http://127.0.0.1:5008/admin/system/version",
                headers={"Authorization": "Bearer " + token, "If-None-Match": "*"})
            with urllib.request.urlopen(request, timeout=10) as response:
                assert response.status == 200
                assert response.headers["Cache-Control"] == "no-store"
                assert response.headers["ETag"] is None
                assert response.headers["Last-Modified"] is None
                body = json.load(response)
            assert body == {"success": True, "data": {key.lower(): value for key, value in expected.items()}}, body
            contents = log.read_text()
            versions = re.findall(r"Lexarbor starting, version (\S+)", contents)
            identities = re.findall(r"Lexarbor build, channel (\S+), revision (\S+)", contents)
            assert versions == [expected["Version"]], versions
            assert identities == [(expected["Channel"], expected["Revision"] or "unknown")], identities
        finally:
            stop(process)


def main():
    root = Path(tempfile.mkdtemp(prefix="lexarbor-build-identity-"))
    print(f"Build identity artifacts: {root}", flush=True)
    fixture_process = None
    try:
        probe = root / "probe"
        probe.mkdir()
        (probe / "Probe.csproj").write_text(PROBE_PROJECT)
        (probe / "Program.cs").write_text(PROBE_SOURCE)
        run(["dotnet", "publish", str(probe / "Probe.csproj"), "-c", "Release",
             "-o", str(probe / "published")], root / "probe-build.log", cwd=root)
        fixture = root / "fixture"
        fixture.mkdir()
        (fixture / "Fixture.csproj").write_text(PROBE_PROJECT.replace('Microsoft.NET.Sdk"', 'Microsoft.NET.Sdk.Web"'))
        (fixture / "Program.cs").write_text((REPOSITORY / ".github/scripts/build-identity-fixture.cs").read_text())
        run(["dotnet", "publish", str(fixture / "Fixture.csproj"), "-c", "Release", "-o", str(fixture / "published")],
            root / "fixture-build.log", cwd=root)
        fixture_process = subprocess.Popen(["dotnet", str(fixture / "published/Fixture.dll")],
            stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True, start_new_session=True)
        if not select.select([fixture_process.stdout], [], [], 30)[0]:
            raise RuntimeError("Loopback identity fixture did not start")
        identity = json.loads(fixture_process.stdout.readline())
        for name, version, revision, channel in ROWS:
            row = root / name
            row.mkdir()
            output = row / "published"
            command = ["dotnet", "publish", str(REPOSITORY / "src/Lexarbor.Host/Lexarbor.Host.csproj"),
                       "-c", "Release", "-o", str(output), "--artifacts-path", str(row / "artifacts"),
                       "-p:UseAppHost=false"]
            for key, value in [("Version", version), ("BuildRevision", revision), ("BuildChannel", channel)]:
                if value is not None:
                    command.append(f"-p:{key}={value}")
            if name == "missing-version":
                command.append("-p:GenerateAssemblyInformationalVersionAttribute=false")
            run(command, row / "publish.log", cwd=REPOSITORY)
            assert not list(output.rglob(".git")), "Publish output must be independent of Git"
            expected = {"Version": "unknown" if name == "missing-version" else version or "0.0.0-dev", "Revision": revision,
                        "Channel": channel or "development"}
            # Conflicting values arrive only after compilation; none may restamp the artifact.
            environment = os.environ.copy()
            environment.update({"APP_VERSION": "9.9.9-runtime", "Version": "9.9.9-runtime",
                                "APP_REVISION": "f" * 40, "BuildRevision": "f" * 40,
                                "APP_CHANNEL": "edge" if channel != "edge" else "release",
                                "BuildChannel": "edge" if channel != "edge" else "release",
                                "ASPNETCORE_ENVIRONMENT": "Production"})
            for variable in ["DOTNET_RUNNING_IN_CONTAINER", "DOTNET_RUNNING_IN_CONTAINERS"]:
                environment.pop(variable, None)
            run(["dotnet", str(probe / "published/Probe.dll"), str(output / "Lexarbor.Host.dll")],
                row / "identity.json", cwd=root, env=environment)
            actual = json.loads((row / "identity.json").read_text())
            assert {key: actual[key] for key in expected} == expected, actual
            if name == "missing-version":
                assert actual["InformationalVersion"] is None, actual
            else:
                assert actual["InformationalVersion"].split("+", 1)[0] == expected["Version"], actual
            assert actual["Metadata"]["BuildRevision"] in ([None, ""] if revision is None else [revision]), actual
            assert actual["Metadata"]["BuildChannel"] == expected["Channel"], actual
            settings_path = output / "appsettings.json"
            settings = json.loads(settings_path.read_text())
            settings.update({"Version": "8.8.8-config", "BuildRevision": "e" * 40,
                             "BuildChannel": "release" if channel != "release" else "edge"})
            settings_path.write_text(json.dumps(settings))
            environment.update({"IdentityService__Authority": identity["issuer"],
                "IdentityService__Issuer": identity["issuer"], "IdentityService__Audience": "lexarbor"})
            verify_startup(output, expected, environment, row / "startup.log", identity["token"])
            print(f"PASS {name}: {json.dumps(expected)}; authorized HTTP and exact health envelope", flush=True)
    except BaseException:
        print(f"FAILED: artifacts retained at {root}", flush=True)
        raise
    else:
        shutil.rmtree(root)
        print("All published Host identities passed", flush=True)
    finally:
        if fixture_process is not None:
            stop(fixture_process)


def interrupted(_signum, _frame):
    raise KeyboardInterrupt


if __name__ == "__main__":
    signal.signal(signal.SIGTERM, interrupted)
    main()
