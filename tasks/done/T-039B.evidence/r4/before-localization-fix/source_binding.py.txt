from pathlib import Path
import hashlib
import json
import shutil
import subprocess
import sys

sys.stdout.reconfigure(encoding="utf-8")
evidence = Path(__file__).resolve().parent
root = evidence.parents[3]
roots = ["src", "tests", "build", ".github", "Iris.slnx", "Directory.Build.props", "Directory.Packages.props", "global.json"]
active = json.loads((evidence / "active_round.json").read_text(encoding="utf-8")) if (evidence / "active_round.json").exists() else {}
binding_path = evidence / active.get("SourceBinding", "tested-source-binding.json")

def git(*arguments, data=None):
    return subprocess.check_output(["git", *arguments], cwd=root, input=data)

def sha(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()

def fingerprint(path, relative_to=root):
    return {"Path": path.relative_to(relative_to).as_posix(), "Bytes": path.stat().st_size, "Sha256": sha(path)}

def tree_of(entries):
    tree = {}
    for entry in entries:
        current = tree
        parts = entry["Path"].split("/")
        for name in parts[:-1]: current = current.setdefault(name, {})
        current[parts[-1]] = entry["GitBlob"]
    def write(node):
        lines = []
        for name, value in node.items():
            if isinstance(value, dict): mode, kind, digest = "040000", "tree", write(value)
            else: mode, kind, digest = "100644", "blob", value
            lines.append(f"{mode} {kind} {digest}\t{name}\n")
        return git("mktree", data="".join(lines).encode()).decode().strip()
    return write(tree)

if sys.argv[1] == "capture":
    paths = sorted(set(git("ls-files", "--cached", "--others", "--exclude-standard", "-z", "--", *roots).decode().rstrip("\0").split("\0")))
    blobs = git("hash-object", "-w", "--stdin-paths", data=("\n".join(paths) + "\n").encode()).decode().splitlines()
    sources = [dict(fingerprint(root / path), GitBlob=blob) for path, blob in zip(paths, blobs, strict=True)]
    candidate = root / active.get("CandidateDirectory", "artifacts/t039b/r4-tested-candidate-1")
    shutil.copytree(root / "tests/Iris.App.Tests/bin/Release/net10.0-windows10.0.19041.0", candidate,
        ignore=shutil.ignore_patterns("TestResults", "*.log", "*.diag"))
    binding = {"BaseCommit": git("rev-parse", "HEAD").decode().strip(), "GitTree": tree_of(sources),
        "GitTreeSemantics": "Canonical Git tree of exactly src/**, tests/**, build/**, .github/** and the four explicit root build files listed in Roots. Tasks/evidence, documentation, .codex, build outputs and this binding are excluded. Recompute from SourceFiles GitBlob entries or the same path subset of the final commit; no self-reference.",
        "Roots": roots, "SourceFiles": sources, "TestedCandidateDirectory": str(candidate),
        "TestedCandidateFiles": [fingerprint(p, candidate) for p in sorted(candidate.rglob("*")) if p.is_file()]}
    path = binding_path
    if path.exists(): raise FileExistsError(path)
    path.write_text(json.dumps(binding, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"SourceFiles": len(sources), "GitTree": binding["GitTree"], "TestedFiles": len(binding["TestedCandidateFiles"])}))
elif sys.argv[1] == "verify-head":
    binding = json.loads(binding_path.read_text(encoding="utf-8"))
    commit = sys.argv[2] if len(sys.argv) > 2 else "HEAD"
    entries = []
    for row in git("ls-tree", "-r", "-z", commit, "--", *roots).decode().rstrip("\0").split("\0"):
        metadata, path = row.split("\t")
        mode, kind, blob = metadata.split()
        if mode != "100644" or kind != "blob": raise ValueError(row)
        entries.append({"Path": path, "GitBlob": blob})
    assert {(p["Path"], p["GitBlob"]) for p in entries} == {(p["Path"], p["GitBlob"]) for p in binding["SourceFiles"]}
    assert tree_of(entries) == binding["GitTree"]
    for entry in binding["SourceFiles"]:
        assert sha(root / entry["Path"]) == entry["Sha256"], entry["Path"]
    print(json.dumps({"Commit": git("rev-parse", commit).decode().strip(), "SourceFiles": len(entries), "GitTree": binding["GitTree"], "MatchesTestedAndPackagedSource": True}))
else: raise ValueError(sys.argv[1])
