"""Read-only R4 source/candidate/package checks, independent of delivery generation."""
from pathlib import Path, PurePosixPath
import hashlib
import json
import subprocess
import zipfile

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[3]
SOURCE_BINDING = ROOT / "tasks/done/T-039B.evidence/r4/tested-source-binding.json"
binding = json.loads(SOURCE_BINDING.read_text("utf-8-sig"))
records = []


def sha(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def check(name, passed, **detail):
    records.append(dict(name=name, passed=bool(passed), **detail))


def git(*args, data=None):
    return subprocess.check_output(["git", *args], cwd=ROOT, input=data, stderr=subprocess.PIPE)


def canonical_tree(entries):
    node = {}
    for name, blob in entries.items():
        current = node
        parts = PurePosixPath(name).parts
        for part in parts[:-1]:
            current = current.setdefault(part, {})
        current[parts[-1]] = blob

    def digest(tree):
        content = bytearray()
        for name, value in sorted(tree.items(), key=lambda row: (row[0] + ("/" if isinstance(row[1], dict) else "")).encode("utf-8")):
            directory = isinstance(value, dict)
            identity = digest(value) if directory else bytes.fromhex(value)
            content.extend((b"40000 " if directory else b"100644 ") + name.encode("utf-8") + b"\0" + identity)
        return hashlib.sha1(b"tree " + str(len(content)).encode() + b"\0" + content).digest()

    return digest(node).hex()


roots = ["src", "tests", "build", ".github", "Iris.slnx", "Directory.Build.props", "Directory.Packages.props", "global.json"]
assert binding["Roots"] == roots
source_names = [p["Path"] for p in binding["SourceFiles"]]
for name in source_names:
    parts = PurePosixPath(name).parts
    assert ".." not in parts and parts[0] in roots
current_names = git("ls-files", "--cached", "--others", "--exclude-standard", "-z", "--", *roots).decode().rstrip("\0").split("\0")
check("source-path-set", set(current_names) == set(source_names), bound=len(source_names), current=len(set(current_names)))
current_blobs = git("hash-object", "--stdin-paths", data=("\n".join(source_names) + "\n").encode()).decode().splitlines()
for item, blob in zip(binding["SourceFiles"], current_blobs, strict=True):
    file = ROOT / item["Path"]
    check("source:" + item["Path"], file.stat().st_size == item["Bytes"] and sha(file) == item["Sha256"] and blob == item["GitBlob"])
calculated_tree = canonical_tree({p["Path"]: p["GitBlob"] for p in binding["SourceFiles"]})
check("canonical-source-tree", calculated_tree == binding["GitTree"], independentlyCalculated=calculated_tree, expected=binding["GitTree"])
candidate = Path(binding["TestedCandidateDirectory"])
for item in binding["TestedCandidateFiles"]:
    file = candidate / item["Path"]
    check("tested-binary:" + item["Path"], file.is_file() and file.stat().st_size == item["Bytes"] and sha(file) == item["Sha256"])

packages = []
for version in ["0.2.0", "0.2.1"]:
    folder = ROOT / f"artifacts/t039b/release-{version}-final-6"
    manifest = json.loads((folder / "payload-manifest.json").read_text("utf-8-sig"))
    inventory = {p["Path"].replace("\\", "/"): p for p in manifest}
    with zipfile.ZipFile(folder / f"Iris-{version}-full.nupkg") as archive:
        entries = [p for p in archive.infolist() if p.filename.startswith("lib/app/") and not p.is_dir()]
        payload = {p.filename[8:]: p for p in entries}
        check(f"{version}:payload-set", len(entries) == len(payload) == len(manifest) and set(payload) == set(inventory), entries=len(entries))
        for name, item in payload.items():
            with archive.open(item) as stream:
                digest = hashlib.file_digest(stream, "sha256").hexdigest()
            expected = inventory.get(name, {})
            check(f"{version}:payload:{name}", expected.get("Bytes") == item.file_size and expected.get("Sha256", "").lower() == digest)
        identity = json.loads(archive.read("lib/app/release-source.json").decode("utf-8-sig"))
        check(f"{version}:identity", identity == dict(PackageId="Iris", Channel="win", RuntimeId="win-x64", Repository="https://github.com/Tmcity/Iris", Version=version))
        runtime = json.loads(archive.read("lib/app/Iris.App.runtimeconfig.json").decode("utf-8-sig"))["runtimeOptions"]
        check(f"{version}:framework-dependent-app", "includedFrameworks" not in runtime and any(p["name"] == "Microsoft.WindowsDesktop.App" and p["version"].startswith("10.") for p in runtime["frameworks"]) and "coreclr.dll" not in payload)
        check(f"{version}:no-test-payload", not any(".tests." in n.lower() or "update-worker" in n.lower() or n.endswith(".pdb") for n in payload))
        maintenance = archive.read("lib/app/maintenance/Iris.Maintenance.exe")
    outer = (folder / f"Iris-{version}-Setup.exe").read_bytes()
    footer = len(b"IRIS-INSTALL-V1!") + 40
    length = int.from_bytes(outer[-40:-32], "little", signed=True)
    offset = len(outer) - footer - length
    check(f"{version}:bundle-magic", outer[-footer:-40] == b"IRIS-INSTALL-V1!")
    check(f"{version}:bundle-size", 0 < length <= 1024**3 and offset > 0)
    check(f"{version}:bundle-setup-hash", offset > 0 and hashlib.sha256(outer[offset:-footer]).digest() == outer[-32:])
    check(f"{version}:bundle-maintenance", offset > 0 and outer[:offset] == maintenance)
    files = {p.name: p for p in folder.iterdir() if p.is_file()}
    hashes = json.loads((folder / "SHA256.json").read_text("utf-8-sig"))
    check(f"{version}:top-level-hash-set", {p["Name"] for p in hashes} == set(files) - {"SHA256.json"})
    for item in hashes:
        file = files[item["Name"]]
        check(f"{version}:top-level:{file.name}", file.stat().st_size == item["Bytes"] and sha(file) == item["Sha256"].lower())
    feed = json.loads((folder / "releases.win.json").read_text("utf-8-sig"))
    full = [p for p in feed["Assets"] if p["Type"] == "Full"]
    check(f"{version}:feed-has-current-full", any(p["Version"] == version and p["PackageId"] == "Iris" for p in full))
    for item in full:
        path = folder / item["FileName"]
        check(f"{version}:feed-full:{path.name}", path.is_file() and path.stat().st_size == item["Size"] and sha(path) == item["SHA256"].lower())
    packages.append(dict(version=version, payloadEntries=len(manifest), topLevelFiles=len(files), fullFeedEntries=len(full), files=[dict(name=n, bytes=p.stat().st_size, sha256=sha(p)) for n,p in sorted(files.items())]))

result = dict(observedHead=git("rev-parse", "HEAD").decode().strip(), scope="Source snapshot, fixed tested binaries and final-6 package bytes only; no final commit, independent tests, installer execution or guest completion is claimed.",
    sourceBindingSha256=sha(SOURCE_BINDING), verifierSha256=sha(Path(__file__)), sourceFiles=len(source_names), testedFiles=len(binding["TestedCandidateFiles"]),
    checks=len(records), passed=sum(p["passed"] for p in records), failures=[p for p in records if not p["passed"]], packages=packages, records=records)
with (HERE / "candidate-check-1.json").open("x", encoding="utf-8") as output:
    json.dump(result, output, indent=2, ensure_ascii=False)
print(json.dumps({k:v for k,v in result.items() if k not in ["records", "packages"]}, indent=2, ensure_ascii=False))
print(json.dumps([dict(version=p["version"], payloadEntries=p["payloadEntries"], topLevelFiles=p["topLevelFiles"], fullFeedEntries=p["fullFeedEntries"]) for p in packages]))
raise SystemExit(bool(result["failures"]))
