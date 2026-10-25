"""Independently inspect completed real R4 guests; never execute guest commands."""
from pathlib import Path
import argparse
import hashlib
import json
import re

ROOT = Path(__file__).resolve().parents[4]
REVIEW = Path(__file__).resolve().parent
EVIDENCE = ROOT / "tasks/done/T-039B.evidence/r4"
parser = argparse.ArgumentParser()
parser.add_argument("--install", required=True)
parser.add_argument("--update")
parser.add_argument("--tag", required=True)
parser.add_argument("--output", required=True)
args = parser.parse_args()
for value in [args.install, args.update, args.tag, args.output]:
    if value is None:
        continue
    if not re.fullmatch(r"[A-Za-z0-9_.-]+", value) or value in {".", ".."}:
        raise ValueError("Only explicit single names are permitted")
output = REVIEW / args.output
if output.exists():
    raise FileExistsError(output)
checks = []


def sha(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def check(name, condition, **detail):
    checks.append(dict(name=name, passed=bool(condition), **detail))


def one(items, name):
    matches = [p["Value"] for p in items if p["Name"] == name]
    if len(matches) != 1:
        raise ValueError(f"Expected one observation {name}; got {len(matches)}")
    return matches[0]


summaries = []
images = []
scenarios = [(args.install, "install")]
if args.update:
    scenarios.append((args.update, "update"))
for label, scenario in scenarios:
    folder = EVIDENCE / label
    intent = read(folder / "intent.json")
    check(f"{label}:scenario", intent["Scenario"] == scenario)
    check(f"{label}:package-tag", intent["PackageTag"] == args.tag)
    inp = Path(intent["ReadOnlyInput"])
    expected = {p["Path"].replace("\\", "/"): p for p in intent["Files"]}
    actual = {p.relative_to(inp).as_posix(): p for p in inp.rglob("*") if p.is_file()}
    check(f"{label}:input-set", len(expected) == len(intent["Files"]) and set(expected) == set(actual), expected=len(expected), actual=len(actual))
    for name, item in expected.items():
        path = actual.get(name)
        check(f"{label}:input:{name}", path is not None and path.stat().st_size == item["Bytes"] and sha(path) == item["Sha256"].lower())
    setup = ROOT / f"artifacts/t039b/release-0.2.0-{args.tag}/Iris-0.2.0-Setup.exe"
    check(f"{label}:actual-installer", sha(inp / setup.name) == sha(setup))
    check(f"{label}:read-only-share", "--allow-write" not in read(folder / "share-input.command.json")["Command"])
    check(f"{label}:output-share", "--allow-write" in read(folder / "share-output.command.json")["Command"])
    for command in ["start", "share-input", "share-output", "connect", "execute", "stop", "list-after"]:
        check(f"{label}:exit:{command}", read(folder / (command + ".command.json"))["ExitCode"] == 0)
    check(f"{label}:no-sandbox-left", not read(folder / "list-after.log")["WindowsSandboxEnvironments"])
    result_dir = folder / "output" / ("update-r4" if scenario == "update" else "")
    report = read(result_dir / "result.json")
    observations = report["Checks"]
    check(f"{label}:failure-null", report.get("Failure") is None)
    false = [p["Name"] for p in observations if p["Value"] is False]
    check(f"{label}:no-false-assertions", not false, falseAssertions=false)
    env = one(observations, "environment")
    check(f"{label}:clean-dotnet10", not any(p["Version"].startswith("10.") for p in env["RuntimesBefore"]))
    check(f"{label}:guest-user", "WDAGUtilityAccount" in env["User"])
    check(f"{label}:windows-guest", env["Version"].startswith("10.0.") and "Windows 11" in env["OS"])
    prefetch = bool(intent.get("PrefetchRuntimeCache"))
    if prefetch:
        for name in ["official-runtime-sha512-matches", "official-runtime-signature-valid", "still-no-shared-dotnet10-before-iris-installer"]:
            check(f"{label}:prerequisite:{name}", one(observations, name) is True)
        check(f"{label}:runtime-not-preinstalled", not one(observations, "runtime-prefetched-not-installed")["Runtimes"])
        signature = one(observations, "runtime-prefetch-signature")
        check(f"{label}:runtime-signer", signature["Status"] == "Valid" and "O=Microsoft Corporation" in signature["Subject"])
        metadata = read(result_dir / "runtime-prefetch/releases.json")
        release = next(p for p in metadata["releases"] if p.get("windowsdesktop", {}).get("version") == signature["Version"])
        official = next(p for p in release["windowsdesktop"]["files"] if p["rid"] == "win-x64" and p["name"].endswith(".exe"))
        # The signed dependency remains in the dedicated artifacts output; it
        # need not be committed into the textual evidence archive.
        runtime_file = Path(intent["WritableOutput"]) / ("update-r4" if scenario == "update" else "") / "runtime-prefetch" / f"windowsdesktop-runtime-{signature['Version']}-win-x64.exe"
        with runtime_file.open("rb") as stream:
            runtime_sha512 = hashlib.file_digest(stream, "sha512").hexdigest()
        check(f"{label}:actual-runtime-sha512", runtime_sha512 == official["hash"].lower() == signature["Sha512"].lower())
    if scenario == "install":
        required = ["install-did-not-start-app", "settings-window-appeared", "external-maintenance-observed", "all-owned-maintenance-exited", "silent-uninstall-kept-data", "uninstall-key-removed", "transaction-completed", "desktop-runtime-retained"]
        for name in required:
            check(f"{label}:required:{name}", one(observations, name) is True)
        for name in ["install-exit", "uia-start-and-eof-exit", "uninstall-entry-exit", "original-app-exit"]:
            check(f"{label}:required:{name}", one(observations, name) == 0)
        runtime = one(observations, "runtimes-after")
        check(f"{label}:desktop-runtime-installed", any(p["Family"] == "Microsoft.WindowsDesktop.App" and p["Version"].startswith("10.") for p in runtime))
        check(f"{label}:installed-version", one(observations, "installed-identity")["Version"] == "0.2.0")
        owners = [p["Value"] for p in observations if p["Name"] == "maintenance-owner-exit"]
        check(f"{label}:real-maintenance-owners", len(owners) >= 2 and all(p["Code"] == 0 for p in owners), exits=owners)
        screenshot = result_dir / "installed-settings.png"
    else:
        full = ROOT / f"artifacts/t039b/release-0.2.1-{args.tag}/Iris-0.2.1-full.nupkg"
        check(f"{label}:actual-full-package", sha(inp / full.name) == sha(full))
        required = ["clean-no-shared-dotnet10", "install-before-update", "seed-real-data", "old-real-settings-visible", "update-host-exit-zero", "old-real-process-exited", "old-real-process-exit-zero", "new-version", "preserved-data", "exactly-one-restart", "open-updated-settings", "updated-window-visible", "maintenance-registration-after-update", "updated-exit-command", "updated-real-process-exited", "updated-real-exit-zero", "updated-data-readable", "work-directories-bounded"]
        for name in required:
            check(f"{label}:required:{name}", one(observations, name) is True)
        check(f"{label}:new-identity", one(observations, "new-identity")["Version"] == "0.2.1")
        workdirs = one(observations, "work-dirs-after-update")
        check(f"{label}:work-directories-zero", workdirs["Count"] == 0 and not workdirs["Paths"])
        engine = read(result_dir / "engine/result.json")
        check(f"{label}:engine-failure-null", engine["Failure"] is None and not engine["Cleanup"])
        for name in ["real-iris-gracefully-closed-before-commit", "committed-parent-still-alive"]:
            check(f"{label}:engine:{name}", one(engine["Results"], name) is True)
        completed = one(engine["Results"], "actual-update-completed")
        check(f"{label}:native-success", completed["Version"] == "0.2.1" and completed["ParentExit"] == 0 and completed["LastNativeExitCode"] == 0)
        before, after = read(result_dir / "data-before.json"), read(result_dir / "data-after.json")
        for item in ["SettingsReadable", "SecretDecrypts", "HistoryReadable", "LogPreserved"]:
            check(f"{label}:data:{item}", before[item] is True and after[item] is True)
        check(f"{label}:data-hashes", len(before["Hashes"]) == 4 and before["Hashes"] == after["Hashes"])
        screenshot = result_dir / "updated-settings.png"
    check(f"{label}:screenshot-exists", screenshot.is_file())
    if screenshot.is_file():
        images.append(dict(path=screenshot.relative_to(ROOT).as_posix(), bytes=screenshot.stat().st_size, sha256=sha(screenshot), visuallyReviewed=False))
    summaries.append(dict(label=label, sandboxId=intent["SandboxId"], environment=env, runtimeDownloadCachePrefilled=prefetch,
        prerequisiteEvidenceScope="No shared .NET10 before the real Setup installed the verified cached Microsoft runtime; native default complete-download path is not proved" if prefetch else "Unmodified native prerequisite path, as evidenced by the installation log",
        observations=len(observations), booleanAssertions=sum(type(p["Value"]) is bool for p in observations), resultSha256=sha(result_dir / "result.json")))
result = dict(scope="Independent reads of completed real guest inputs, outcome assertions, process exits, data and images. Screenshots still require actual visual inspection; controlled GuestUpdateHost is not the public online user UI path.",
    packageTag=args.tag, checks=len(checks), passed=sum(p["passed"] for p in checks), failures=[p for p in checks if not p["passed"]], scenarios=summaries, images=images, records=checks)
with output.open("x", encoding="utf-8") as stream:
    json.dump(result, stream, indent=2, ensure_ascii=False)
print(json.dumps({k:v for k,v in result.items() if k != "records"}, indent=2, ensure_ascii=False))
raise SystemExit(bool(result["failures"]))
