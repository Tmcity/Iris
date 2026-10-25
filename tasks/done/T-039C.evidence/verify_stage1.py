from pathlib import Path
import json
import subprocess
import sys

evidence = Path(__file__).resolve().parent
root = evidence.parents[2]
sys.path.insert(0, str(root / "build"))
from release_manifest import digest, read_json, require, verify_directory

binding = read_json(evidence / "stage1-binding.json")
checks = 0
for item in binding["Evidence"] + binding["SourceFiles"] + [binding["Report"]]:
    path = root / item["Path"]
    require(path.stat().st_size == item["Bytes"] and digest(path) == item["Sha256"], "Bound bytes differ: " + item["Path"])
    checks += 1
for package in binding["ReleasePackages"]:
    verify_directory(root / package["Directory"], package["Version"])
    for item in package["Files"]:
        path = root / item["Path"]
        require(path.stat().st_size == item["Bytes"] and digest(path) == item["Sha256"], "Candidate changed")
        checks += 1
require((root / "tasks/current_task.md").exists(), "Incomplete C must retain current_task")
require(not subprocess.check_output(["git", "diff", "--cached", "--name-only", "--", ".codex", "tasks/BACKLOG.md"], cwd=root), "Forbidden staged paths")
report = {"ByteChecks": checks, "AllBoundBytesMatch": True, "Status": binding["Status"], "SourceCommit": binding["SourceCommit"], "SourceGitTree": binding["SourceGitTree"], "BindingSha256": digest(evidence / "stage1-binding.json"), "CurrentHead": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=root).decode().strip(), "NoRemotePublicationClaimed": True}
target = evidence / sys.argv[1]
require(not target.exists(), "Unique check label required")
target.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8", newline="\n")
print(json.dumps(report, indent=2))
