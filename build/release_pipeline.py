"""Record build/test/package boundaries without exposing credentials or inferring user-path acceptance."""
from pathlib import Path
import argparse
import datetime
import json
import os
import re
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET
from release_manifest import ROOT, digest, read_json, require, verify_directory

def run(command, directory, label, environment):
    path = directory / (label + ".log")
    require(not path.exists(), "Log already exists")
    started = datetime.datetime.now(datetime.timezone.utc).isoformat()
    with path.open("xb") as stream:
        result = subprocess.run(command, cwd=ROOT, env=environment, stdout=stream, stderr=subprocess.STDOUT)
    record = {"Command": command, "StartedUtc": started, "FinishedUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(), "ExitCode": result.returncode,
        "Log": path.name, "Bytes": path.stat().st_size, "Sha256": digest(path)}
    (directory / (label + ".command.json")).write_text(json.dumps(record, indent=2) + "\n", encoding="utf-8", newline="\n")
    print(label + ": exit=" + str(result.returncode), flush=True)
    require(result.returncode == 0, "Recorded command failed: " + label)
    return record

def validate(directory):
    directory.mkdir(parents=True, exist_ok=False)
    environment = os.environ.copy()
    environment["DOTNET_CLI_UI_LANGUAGE"] = "zh-CN"
    environment["PYTHONDONTWRITEBYTECODE"] = "1"
    environment["IRIS_R4_EVIDENCE"] = str(directory / "owned-test-results")
    records = []
    records.append(run([sys.executable, "-B", "-m", "unittest", "discover", "-s", "build/tests", "-p", "test_*.py", "-v"], directory, "release-manifest-tests", environment))
    records.append(run(["dotnet", "build", "Iris.slnx", "-c", "Release", "--no-incremental"], directory, "release-build", environment))
    records.append(run(["dotnet", "test", "Iris.slnx", "-c", "Release", "--no-build", "--no-ansi", "--no-progress", "--report-xunit-xml", "--results-directory", str(directory / "reports")], directory, "full-tests", environment))
    build = (directory / "release-build.log").read_text(encoding="utf-8-sig")
    require(re.search(r"0 个警告", build) and re.search(r"0 个错误", build), "Release must have zero warnings/errors")
    assemblies = []
    for path in sorted((directory / "reports").glob("*.xml")):
        for assembly in ET.parse(path).iter("assembly"):
            assemblies.append({"Name": Path(assembly.attrib["name"]).name,
                **{key.capitalize(): int(assembly.attrib[key]) for key in ["total", "passed", "failed", "skipped"]},
                "SkippedTests": [{"Name": item.attrib["name"], "Reason": "".join(item.find("reason").itertext())}
                    for item in assembly.iter("test") if item.attrib["result"] == "Skip"]})
    totals = {key: sum(item[key] for item in assemblies) for key in ["Total", "Passed", "Failed", "Skipped"]}
    require(totals["Total"] > 0 and totals["Failed"] == 0, "Full test reports missing or failed")
    summary = {"SourceCommit": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT).decode().strip(),
        "BuildWarnings": 0, "BuildErrors": 0, "FullTests": totals, "Assemblies": assemblies, "Commands": records,
        "Scope": "Local/CI checks only; no real installation, public UI upgrade, default prerequisite download or whole-task acceptance is inferred."}
    (directory / "validation-summary.json").write_text(json.dumps(summary, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")
    print(json.dumps(summary, ensure_ascii=False, indent=2))

def package(version, directory, logs, validation, previous, tool_source):
    logs.mkdir(parents=True, exist_ok=False)
    environment = os.environ.copy()
    temporary = logs / "temp"
    temporary.mkdir()
    environment["TEMP"] = environment["TMP"] = str(temporary)
    environment["PYTHONDONTWRITEBYTECODE"] = "1"
    powershell = Path(os.environ["SystemRoot"]) / "System32/WindowsPowerShell/v1.0/powershell.exe"
    command = [str(powershell), "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", str(ROOT / "build/pack.ps1"), "-Version", version, "-OutputDirectory", str(directory)]
    if previous:
        command += ["-PreviousReleaseDirectory", str(previous)]
    if tool_source:
        command += ["-ToolPackageDirectory", str(tool_source)]
    run(command, logs, "package", environment)
    provenance = read_json(directory / "release-provenance.json")
    summary = read_json(validation / "validation-summary.json")
    require(provenance["SourceCommit"] == summary["SourceCommit"], "Validation and package source commits differ")
    files = [(validation / name, name) for name in ["release-manifest-tests.log", "release-build.log", "full-tests.log", "validation-summary.json"]]
    files.append((logs / "package.log", "package.log"))
    records = []
    for source, name in files:
        require(not (directory / name).exists(), "Existing validation asset")
        shutil.copyfile(source, directory / name)
        records.append({"Name": name, "Bytes": source.stat().st_size, "Sha256": digest(source)})
    provenance["Validation"] = {"Files": records, "BuildWarnings": 0, "BuildErrors": 0, "FullTests": summary["FullTests"]}
    (directory / "release-provenance.json").write_text(json.dumps(provenance, indent=2) + "\n", encoding="utf-8", newline="\n")
    hashes = [{"Name": path.name, "Bytes": path.stat().st_size, "Sha256": digest(path).upper()} for path in sorted(directory.iterdir()) if path.name != "SHA256.json"]
    (directory / "SHA256.json").write_text(json.dumps(hashes, indent=2) + "\n", encoding="utf-8", newline="\n")
    print(json.dumps(verify_directory(directory, version), indent=2))

if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("command", choices=["validate", "package"])
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--version")
    parser.add_argument("--logs", type=Path)
    parser.add_argument("--validation", type=Path)
    parser.add_argument("--previous", type=Path)
    parser.add_argument("--tool-source", type=Path)
    args = parser.parse_args()
    if args.command == "validate":
        validate(args.output.resolve())
    else:
        package(args.version, args.output.resolve(), args.logs.resolve(), args.validation.resolve(), args.previous.resolve() if args.previous else None, args.tool_source.resolve() if args.tool_source else None)
