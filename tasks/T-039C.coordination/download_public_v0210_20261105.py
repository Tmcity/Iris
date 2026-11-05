"""Read back all official 0.2.10 assets anonymously after its workflow has completed."""
from pathlib import Path
from concurrent.futures import ThreadPoolExecutor, as_completed
import datetime
import hashlib
import importlib.util
import json
import re
import subprocess

ROOT = Path("F:/Iris")
VERSION = "0.2.10"
SOURCE = "227b3d88938543766a27d86a94ade52f9e5b89b7"
TREE = "69d4faf228d9d0b98d77ecb4a8dac582ad33421b"
DESTINATION = ROOT / "artifacts/t039c/public-0.2.10-download-20261105-1"
LOGS = ROOT / "tasks/done/T-039C.evidence/publication-v0.2.10-20261105-1/public-download-20261105-1"
DESTINATION.mkdir(exist_ok=False)
LOGS.mkdir(exist_ok=False)
EXPECTED_NAMES = {
    "SHA256.json", "payload-manifest.json", "staging-manifest.json", "release-provenance.json", "INSTALLATION.md",
    "RELEASE_NOTES.md", "RELEASES", "releases.win.json", "assets.win.json", "Iris-0.2.10-Setup.exe",
    "Iris-0.2.10-full.nupkg", "Iris-0.2.10-delta.nupkg", "Iris-0.2.2-full.nupkg", "Iris-0.2.7-full.nupkg", "Iris-0.2.9-full.nupkg", "release-manifest-tests.log",
    "release-build.log", "full-tests.log", "validation-summary.json", "package.log",
}


def sha(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def save(path, data):
    path.open("x", encoding="utf-8", newline="\n").write(json.dumps(data, indent=2) + "\n")


def download(name, expected=None):
    assert name in EXPECTED_NAMES and Path(name).name == name
    stamp = datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%dT%H%M%S%fZ")
    url = "https://github.com/Tmcity/Iris/releases/download/v0.2.10/" + name + "?iris_public_read=" + stamp
    folder = LOGS / name
    folder.mkdir(exist_ok=False)
    path = DESTINATION / name
    command = ["curl.exe", "--silent", "--show-error", "--location", "--max-time", "180", "--retry", "0",
        "--proto", "=https", "--proto-redir", "=https", "-H", "Cache-Control: no-cache",
        "-A", "Iris-public-asset-verification", "-D", str(folder / "response.headers"),
        "-o", str(path), "--write-out", "%{http_code}", url]
    started = datetime.datetime.now(datetime.timezone.utc).isoformat()
    process = subprocess.Popen(command, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    save(folder / "process.json", {"Pid": process.pid, "Command": command, "StartedUtc": started})
    output, error = process.communicate()
    (folder / "stderr.log").write_bytes(error)
    size = path.stat().st_size if path.exists() else 0
    digest = sha(path) if size else hashlib.sha256(b"").hexdigest()
    record = {"Url": url, "StartedUtc": started, "FinishedUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
              "AuthorizationSent": False, "UsesGitHubRestApi": False, "HttpStatus": output.decode("ascii").strip(),
              "ExitCode": process.returncode, "Bytes": size, "Sha256": digest, "LocalPath": str(path)}
    save(folder / "download.json", record)
    assert process.returncode == 0 and record["HttpStatus"] == "200", name
    if expected:
        assert size == expected["Bytes"] and digest == expected["Sha256"].lower(), name
    print(json.dumps({"Asset": name, "Bytes": size, "Verified": True}), flush=True)
    return record


records = [download("SHA256.json")]
manifest = json.loads((DESTINATION / "SHA256.json").read_text(encoding="utf-8-sig"))
assert isinstance(manifest, list) and len(manifest) == 19
assert {item["Name"] for item in manifest} == EXPECTED_NAMES - {"SHA256.json"}
assert len({item["Name"].casefold() for item in manifest}) == 19
for item in manifest:
    assert item["Name"] in EXPECTED_NAMES and isinstance(item["Bytes"], int) and 0 < item["Bytes"] < 512 * 1024 * 1024
    assert re.fullmatch(r"[a-fA-F0-9]{64}", item["Sha256"])
with ThreadPoolExecutor(max_workers=3) as pool:
    futures = [pool.submit(download, item["Name"], item) for item in manifest]
    for future in as_completed(futures):
        records.append(future.result())
spec = importlib.util.spec_from_file_location("public_release_manifest", ROOT / "build/release_manifest.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
verified = module.verify_directory(DESTINATION, VERSION)
assert verified["SourceCommit"] == SOURCE and verified["SourceGitTree"] == TREE
assert verified["PublicAssets"] == 20 and verified["PayloadEntries"] == 97
provenance = json.loads((DESTINATION / "release-provenance.json").read_text(encoding="utf-8-sig"))
committed = {}
for row in subprocess.check_output(["git", "ls-tree", "-r", "-z", SOURCE, "--", *module.SOURCE_ROOTS], cwd=ROOT).decode().strip("\0").split("\0"):
    metadata, path = row.split("\t")
    mode, kind, blob = metadata.split()
    assert (mode, kind) == ("100644", "blob")
    committed[path] = blob
assert len(committed) == len(provenance["SourceFiles"]) == 721
assert {item["Path"]: item["GitBlob"] for item in provenance["SourceFiles"]} == committed
assert module.source_tree(provenance["SourceFiles"]) == TREE
assert provenance["Build"]["GitHubRunId"] == "42042550153" and provenance["Build"]["GitHubRunAttempt"] == "1"
assert provenance["Build"]["GitHubRef"] == "refs/tags/v0.2.10"
assert sha(DESTINATION / "Iris-0.2.2-full.nupkg") == "c86982f930aafcd142167bbd871d03b588630f7d1d625da968a811daa2dd5d14"
assert sha(DESTINATION / "Iris-0.2.7-full.nupkg") == "f8f9de897b637f8c98a07d601420885bf65529701d014fc52b59f87ce75bace8"
assert sha(DESTINATION / "Iris-0.2.9-full.nupkg") == "42c32435e5883608c77f097befd2b9a7f91845b35942a09966018361c40a779e"
summary = json.loads((DESTINATION / "validation-summary.json").read_text(encoding="utf-8-sig"))
assert summary["FullTests"]["Total"] == 3771 and summary["FullTests"]["Failed"] == 0
assert 0 <= summary["FullTests"]["Skipped"] <= 2
assert summary["BuildWarnings"] == summary["BuildErrors"] == 0
assert all(item["ExitCode"] == 0 for item in summary["Commands"])
full_command = next(item["Command"] for item in summary["Commands"] if item["Log"] == "full-tests.log")
assert full_command[:3] == ["dotnet", "test", "Iris.slnx"] and not any("filter" in part for part in full_command)
review = {"ReviewedUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(), **verified,
          "GitHubRunId": 42042550153, "GitHubRunAttempt": 1, "AnonymousPublicReadback": True,
          "CommittedSourceEntriesMatched": 721, "FullTests": summary["FullTests"],
          "BuildWarnings": 0, "BuildErrors": 0, "Downloads": records, "Directory": str(DESTINATION),
          "RealUiUpgradeVerified": False, "RestartCountVerified": False, "PostUpdateUninstallVerified": False}
save(LOGS / "review.json", review)
print(json.dumps({key: review[key] for key in ("Version", "SourceCommit", "SourceGitTree", "PublicAssets",
    "PayloadEntries", "CommittedSourceEntriesMatched", "FullTests", "FullSha256", "OuterSha256",
    "AnonymousPublicReadback")}, indent=2))
