"""Capture committed release sources and validate the exact public asset set. No network or credentials."""
from pathlib import Path
import argparse
import hashlib
import json
import os
import platform
import re
import shutil
import struct
import subprocess
import sys
import zipfile

ROOT = Path(__file__).resolve().parents[1]
SOURCE_ROOTS = ["src", "tests", "build", ".github", "docs", "README.md", "Iris.slnx", "Directory.Build.props", "Directory.Packages.props", "global.json"]
VERSION = re.compile(r"^\d+\.\d+\.\d+$")

def unique_object(pairs):
    value = {}
    for key, item in pairs:
        if key in value:
            raise ValueError("Duplicate JSON property")
        value[key] = item
    return value

def read_json(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"), object_pairs_hook=unique_object)

def digest(path, algorithm="sha256"):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, algorithm).hexdigest()

def require(condition, message):
    if not condition:
        raise ValueError(message)

def safe_name(name):
    require(isinstance(name, str) and name not in ("", ".", "..") and not any(c in name for c in "/\\:\x00"), "Asset must have one basename")
    return name

def git(*arguments, data=None):
    return subprocess.check_output(["git", *arguments], cwd=ROOT, input=data)

def source_tree(entries):
    node = {}
    for entry in entries:
        current = node
        parts = entry["Path"].split("/")
        for part in parts[:-1]:
            current = current.setdefault(part, {})
        current[parts[-1]] = entry["GitBlob"]
    def write(current):
        lines = []
        for name, value in current.items():
            mode, kind, blob = ("040000", "tree", write(value)) if isinstance(value, dict) else ("100644", "blob", value)
            lines.append(f"{mode} {kind} {blob}\t{name}\n")
        return git("mktree", data="".join(lines).encode()).decode().strip()
    return write(node)

def capture_source(version, powershell):
    require(VERSION.fullmatch(version), "Numeric release version required")
    entries = []
    for row in git("ls-tree", "-r", "-z", "HEAD", "--", *SOURCE_ROOTS).decode().strip("\0").split("\0"):
        metadata, path = row.split("\t")
        mode, kind, blob = metadata.split()
        require(mode == "100644" and kind == "blob", "Unexpected source file mode")
        require(git("hash-object", "--path", path, path).decode().strip() == blob, "Commit release source changes before packaging: " + path)
        entries.append({"Path": path, "GitBlob": blob, "WorkingBytes": (ROOT / path).stat().st_size, "WorkingSha256": digest(ROOT / path)})
    untracked = git("ls-files", "--others", "--exclude-standard", "-z", "--", *SOURCE_ROOTS).decode().strip("\0").split("\0")
    require(not any(path and "__pycache__" not in Path(path).parts for path in untracked), "Commit new release source files before packaging")
    return {"Version": version, "PackageId": "Iris", "Channel": "win", "RuntimeId": "win-x64", "Repository": "https://github.com/Tmcity/Iris",
        "SourceCommit": git("rev-parse", "HEAD").decode().strip(), "SourceGitTree": source_tree(entries), "SourceRoots": SOURCE_ROOTS,
        "SourceTreeSemantics": "Canonical tree of the listed committed roots. Tasks, artifacts and generated release files are excluded; no self-reference.",
        "SourceFiles": entries, "IntendedTag": "v" + version,
        "Build": {"DotnetSdk": subprocess.check_output(["dotnet", "--version"], cwd=ROOT).decode().strip(), "Python": platform.python_version(),
            "PowerShell": powershell, "Vpk": "1.2.161", "GitHubRunId": os.environ.get("GITHUB_RUN_ID"), "GitHubRunAttempt": os.environ.get("GITHUB_RUN_ATTEMPT"),
            "GitHubRef": os.environ.get("GITHUB_REF"), "RunnerImage": os.environ.get("ImageVersion")},
        "Signed": False, "AppliedPackageType": "full", "DeltaApplicationVerified": False}

def verify_hashes(directory):
    directory = Path(directory)
    require(all(path.is_file() and not path.is_symlink() for path in directory.iterdir()), "Public assets must be regular files")
    manifest = read_json(directory / "SHA256.json")
    require(len({entry["Name"] for entry in manifest}) == len(manifest), "Duplicate hash record")
    require({safe_name(entry["Name"]) for entry in manifest} == {path.name for path in directory.iterdir()} - {"SHA256.json"}, "Public asset/hash set differs")
    for entry in manifest:
        path = directory / entry["Name"]
        require(path.stat().st_size == entry["Bytes"] and digest(path).upper() == entry["Sha256"].upper(), "Asset bytes differ: " + entry["Name"])
    return manifest

def verify_indexes(directory, version):
    directory = Path(directory)
    feed = read_json(directory / "releases.win.json")["Assets"]
    require(0 < len(feed) <= 256, "Invalid release feed size")
    names = set()
    for entry in feed:
        name = safe_name(entry["FileName"])
        require(name not in names and entry["PackageId"] == "Iris" and VERSION.fullmatch(entry["Version"]), "Invalid/conflicting feed identity")
        names.add(name)
        require(entry["Type"] in {"Full", "Delta"}, "Unexpected package type")
        require(name == f'Iris-{entry["Version"]}-{entry["Type"].lower()}.nupkg', "Invalid package filename")
        path = directory / name
        require(path.is_file() and path.stat().st_size == entry["Size"] and digest(path).upper() == entry["SHA256"].upper()
            and digest(path, "sha1").upper() == entry["SHA1"].upper(), "Feed does not match package: " + name)
    require(names == {path.name for path in directory.glob("*.nupkg")}, "Package/feed set differs")
    assets = read_json(directory / "assets.win.json")
    installers = []
    require(len({entry["RelativeFileName"] for entry in assets}) == len(assets), "Duplicate asset index entry")
    for entry in assets:
        name = safe_name(entry["RelativeFileName"])
        require((directory / name).is_file(), "Asset index references missing public file: " + name)
        require(entry["Type"] in {"Installer", "Full", "Delta"}, "Unexpected asset index type")
        if entry["Type"] == "Installer":
            installers.append(name)
        else:
            require(name in names, "Asset index package is absent from feed")
    require(installers == [f"Iris-{version}-Setup.exe"], "Only the versioned outer installer may be public")
    legacy_names = set()
    for line in (directory / "RELEASES").read_text(encoding="utf-8-sig").splitlines():
        sha1, name, size = line.split()
        name = safe_name(name)
        require(name in names and name not in legacy_names, "Invalid legacy feed entry")
        require(int(size) == (directory / name).stat().st_size and digest(directory / name, "sha1").upper() == sha1.upper(), "Legacy feed bytes differ")
        legacy_names.add(name)
    full_names = {entry["FileName"] for entry in feed if entry["Type"] == "Full"}
    require(legacy_names == full_names, "Legacy feed/full-package set differs")
    return feed

def normalize_assets(directory, version):
    directory = Path(directory)
    path = directory / "assets.win.json"
    assets = read_json(path)
    installers = [item for item in assets if item["Type"] == "Installer"]
    require(len(installers) == 1 and installers[0]["RelativeFileName"] == "Iris-win-Setup.exe", "Unexpected SDK installer asset index")
    installers[0]["RelativeFileName"] = f"Iris-{version}-Setup.exe"
    path.write_text(json.dumps(assets, indent=2) + "\n", encoding="utf-8", newline="\n")
    return {"IndexedOuterInstaller": installers[0]["RelativeFileName"], "FeedEntries": len(verify_indexes(directory, version))}

def verify_payload(directory, version):
    directory = Path(directory)
    package = directory / f"Iris-{version}-full.nupkg"
    manifest = read_json(directory / "payload-manifest.json")
    with zipfile.ZipFile(package) as archive:
        entries = {entry.filename[8:]: entry for entry in archive.infolist() if entry.filename.startswith("lib/app/") and not entry.is_dir()}
        require(len(entries) == sum(entry.filename.startswith("lib/app/") and not entry.is_dir() for entry in archive.infolist()), "Duplicate archive payload entry")
        require(len({item["Path"].replace("\\", "/") for item in manifest}) == len(manifest), "Duplicate payload record")
        require(set(entries) == {item["Path"].replace("\\", "/") for item in manifest}, "Actual full payload differs from manifest")
        for item in manifest:
            path = item["Path"].replace("\\", "/")
            require(not any(part in {"..", ".ssh", "update-worker"} for part in path.split("/")), "Unsafe payload path")
            require(not any(value in path for value in ["Tests", "GuestUpdateHost", "secrets.dat", "portable.txt"]) and not path.endswith((".pfx", ".key")), "Non-product payload")
            require(len(archive.read(entries[path])) == item["Bytes"] and hashlib.sha256(archive.read(entries[path])).hexdigest().upper() == item["Sha256"].upper(), "Payload bytes differ: " + path)
        identity = json.loads(archive.read("lib/app/release-source.json"))
        require(identity == {"PackageId": "Iris", "Channel": "win", "RuntimeId": "win-x64", "Repository": "https://github.com/Tmcity/Iris", "Version": version}, "Payload release identity differs")
        for path in ["Iris.App.runtimeconfig.json", "uia/Iris.UiaHost.runtimeconfig.json"]:
            config = json.loads(archive.read("lib/app/" + path))["runtimeOptions"]
            frameworks = config.get("frameworks") or ([config["framework"]] if config.get("framework") else [])
            require("includedFrameworks" not in config and frameworks, "App and UiaHost must remain framework-dependent")
        require("coreclr.dll" not in entries, "App must not be self-contained")
        helper = archive.read("lib/app/maintenance/Iris.Maintenance.exe")
    outer = directory / f"Iris-{version}-Setup.exe"
    footer_size = 56
    with outer.open("rb") as stream:
        stream.seek(-footer_size, 2)
        footer = stream.read()
        require(footer[:16] == b"IRIS-INSTALL-V1!", "Invalid outer installer footer")
        setup_bytes = struct.unpack("<q", footer[16:24])[0]
        require(0 < setup_bytes <= 1024 ** 3 and outer.stat().st_size == len(helper) + setup_bytes + footer_size, "Outer installer length differs")
        stream.seek(0)
        require(stream.read(len(helper)) == helper, "Outer maintenance prefix differs from payload")
        require(hashlib.sha256(stream.read(setup_bytes)).digest() == footer[24:], "Internal Setup digest differs")
    return {"PayloadEntries": len(entries), "MaintenancePrefixBytes": len(helper), "InternalSetupBytes": setup_bytes,
        "InternalSetupSha256": footer[24:].hex(), "FullSha256": digest(package), "OuterSha256": digest(outer)}

def verify_directory(directory, version):
    require(VERSION.fullmatch(version), "Invalid version")
    directory = Path(directory)
    verify_hashes(directory)
    verify_indexes(directory, version)
    payload = verify_payload(directory, version)
    provenance = read_json(directory / "release-provenance.json")
    require(provenance["Version"] == version and provenance["IntendedTag"] == "v" + version, "Provenance version differs")
    require(re.fullmatch(r"[0-9a-f]{40}", provenance["SourceCommit"]) and re.fullmatch(r"[0-9a-f]{40}", provenance["SourceGitTree"]), "Invalid source binding")
    require(payload == provenance["Payload"], "Provenance/payload binding differs")
    allowed = {"SHA256.json", "payload-manifest.json", "staging-manifest.json", "release-provenance.json", "INSTALLATION.md", "RELEASE_NOTES.md", "RELEASES", "releases.win.json", "assets.win.json", f"Iris-{version}-Setup.exe"}
    allowed.update(entry["FileName"] for entry in read_json(directory / "releases.win.json")["Assets"])
    if "Validation" in provenance:
        require(provenance["Validation"]["BuildWarnings"] == provenance["Validation"]["BuildErrors"] == 0 and provenance["Validation"]["FullTests"]["Failed"] == 0, "Invalid validation summary")
        validation_names = {"release-manifest-tests.log", "release-build.log", "full-tests.log", "validation-summary.json", "package.log"}
        require({item["Name"] for item in provenance["Validation"]["Files"]} == validation_names, "Validation evidence set differs")
        for item in provenance["Validation"]["Files"]:
            require((directory / item["Name"]).stat().st_size == item["Bytes"] and digest(directory / item["Name"]) == item["Sha256"], "Validation log bytes differ")
        summary = read_json(directory / "validation-summary.json")
        require(summary["SourceCommit"] == provenance["SourceCommit"] and summary["FullTests"] == provenance["Validation"]["FullTests"], "Validation belongs to a different source")
        allowed.update(validation_names)
    require({path.name for path in directory.iterdir()} == allowed, "Unexpected or missing public asset")
    return {"Version": version, "SourceCommit": provenance["SourceCommit"], "SourceGitTree": provenance["SourceGitTree"], "PublicAssets": len(allowed), **payload}

def finalize(directory, version, source):
    directory = Path(directory)
    provenance = read_json(source)
    require(provenance["Version"] == version, "Source capture version differs")
    provenance["Payload"] = verify_payload(directory, version)
    (directory / "release-provenance.json").write_text(json.dumps(provenance, indent=2) + "\n", encoding="utf-8", newline="\n")
    shutil.copyfile(ROOT / "docs/INSTALLATION.md", directory / "INSTALLATION.md")
    notes = f"# Iris {version}\n\nWindows x64; Windows API floor 10.0.19041. This build is unsigned.\n\nUse Iris-{version}-Setup.exe, the outer installer. Missing .NET 10 Desktop Runtime requires Microsoft prerequisite installation and may require administrator approval.\n\nSource commit: {provenance['SourceCommit']}\nSource tree: {provenance['SourceGitTree']} (scope in release-provenance.json).\n\nApp and UiaHost are framework-dependent; Maintenance is self-contained. Updates use full packages. Delta is generated only; delta application is not claimed.\n\nSee INSTALLATION.md and verify SHA256.json. Public user-path acceptance is recorded separately from build/package success.\n"
    (directory / "RELEASE_NOTES.md").write_text(notes, encoding="utf-8", newline="\n")
    hashes = [{"Name": path.name, "Bytes": path.stat().st_size, "Sha256": digest(path).upper()} for path in sorted(directory.iterdir()) if path.is_file() and path.name != "SHA256.json"]
    (directory / "SHA256.json").write_text(json.dumps(hashes, indent=2) + "\n", encoding="utf-8", newline="\n")
    return verify_directory(directory, version)

if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("command", choices=["capture-source", "finalize", "verify", "indexes", "normalize-assets"])
    parser.add_argument("--version", required=True)
    parser.add_argument("--directory", type=Path)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--source", type=Path)
    parser.add_argument("--powershell", default="")
    args = parser.parse_args()
    if args.command == "capture-source":
        result = capture_source(args.version, args.powershell)
        require(not args.output.exists(), "Source capture already exists")
        args.output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8", newline="\n")
        result = {key: result[key] for key in ["Version", "SourceCommit", "SourceGitTree"]}
    elif args.command == "finalize":
        result = finalize(args.directory, args.version, args.source)
    elif args.command == "indexes":
        result = {"Assets": len(verify_indexes(args.directory, args.version))}
    elif args.command == "normalize-assets":
        result = normalize_assets(args.directory, args.version)
    else:
        result = verify_directory(args.directory, args.version)
    print(json.dumps(result, indent=2))
