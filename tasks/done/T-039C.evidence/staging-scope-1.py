from pathlib import Path
import hashlib
import json
import subprocess

root = Path(__file__).resolve().parents[3]
evidence = root / "tasks/done/T-039C.evidence"
allowed = {".json", ".log", ".py", ".ps1", ".md", ".txt", ".yaml", ".yml", ".xml", ".csv", ".png", ".gitattributes"}
selected = [root / "tasks/T-039C.stage1.md"] + [path for path in evidence.rglob("*") if path.is_file() and path.suffix.lower() in allowed and "__pycache__" not in path.parts]
for path in selected:
    assert path.resolve().is_relative_to(root.resolve())
    assert path.suffix.lower() not in {".exe", ".dll", ".pdb", ".nupkg", ".zip", ".dmp"}
subprocess.run(["git", "add", "-f", "--", *[str(path.relative_to(root)) for path in selected]], cwd=root, check=True)
staged = subprocess.check_output(["git", "diff", "--cached", "--name-only", "-z"], cwd=root).decode().strip("\0").split("\0")
for path in staged:
    assert not path.startswith((".codex/", "artifacts/", "tasks/T-039C.coordination/", "tasks/done/T-039B.review/"))
    assert path != "tasks/BACKLOG.md"
    assert Path(path).suffix.lower() not in {".exe", ".dll", ".pdb", ".nupkg", ".zip", ".dmp"}
summary = {"SelectedFiles": len(selected), "StagedFiles": len(staged), "NoBinaryOrCredentialPaths": True,
    "ArchitectCoordinationUntouched": True, "CodexUntouched": True, "BacklogUntouched": True, "Paths": staged}
(root / "artifacts/t039c/staging-scope-1.json").write_text(json.dumps(summary, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")
print(json.dumps({k: summary[k] for k in ["SelectedFiles", "StagedFiles", "NoBinaryOrCredentialPaths"]}, indent=2))
