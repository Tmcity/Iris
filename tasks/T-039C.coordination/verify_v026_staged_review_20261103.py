"""Finish the staged review without formatting or normalizing original evidence."""
from pathlib import Path
import hashlib
import json
import subprocess

ROOT = Path("F:/Iris")
FINAL = ROOT / "tasks/done/T-039C.evidence/final-review-v026-20261103-1"
staged = subprocess.check_output(["git", "diff", "--cached", "--name-only"], cwd=ROOT).decode().splitlines()
assert staged and all(not path.startswith(".codex/") for path in staged)
regular = [path for path in staged if not path.startswith("tasks/done/T-039C.evidence/")]
subprocess.run(["git", "add", "--", "tasks/T-039C.coordination/finalize_v026_review_20261103.py",
                "tasks/T-039C.coordination/verify_v026_staged_review_20261103.py"], cwd=ROOT, check=True)
regular.append("tasks/T-039C.coordination/verify_v026_staged_review_20261103.py")
subprocess.run(["git", "diff", "--cached", "--check", "--", *regular], cwd=ROOT, check=True)
raw_paths = [path for path in staged if path.startswith("tasks/done/T-039C.evidence/")]
matched = []
for path in raw_paths:
    working = (ROOT / path).read_bytes()
    indexed = subprocess.check_output(["git", "show", ":" + path], cwd=ROOT)
    assert indexed == working, "Git index changed original evidence: " + path
    matched.append({"Path": path, "Bytes": len(indexed), "Sha256": hashlib.sha256(indexed).hexdigest()})
proof = {"SourceCommit": "c32fcb283e417ab11715c46207fdc8cfde4baedf", "EvidenceIndexFilesMatched": len(matched),
         "OriginalCiZipIndexMatched": True, "ReviewCodeAndDocsWhitespaceCheckPassed": True,
         "OriginalEvidenceBytesPreserved": True, "CodexDirectoryStaged": False, "PublicationAuthorized": False,
         "Files": matched}
path = FINAL / "index-byte-check.json"
assert not path.exists()
path.write_text(json.dumps(proof, indent=2) + "\n", encoding="utf-8", newline="\n")
subprocess.run(["git", "add", "--", path.relative_to(ROOT).as_posix()], cwd=ROOT, check=True)
assert subprocess.check_output(["git", "show", ":" + path.relative_to(ROOT).as_posix()], cwd=ROOT) == path.read_bytes()
print(json.dumps({key: value for key, value in proof.items() if key != "Files"}, indent=2))
