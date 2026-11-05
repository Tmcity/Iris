"""Prepare the reviewed tag-only 0.2.10 executor for the actual fixed 0.2.9 baseline."""
from pathlib import Path

ROOT = Path("F:/Iris/tasks/T-039C.coordination")
text = (ROOT / "execute_v029_20261105.py").read_text(encoding="utf-8-sig")
text = text.replace("0.2.9", "0.2.10").replace("v029", "v0210").replace("0.2.7", "0.2.9")
replacements = {
    'SOURCE = "0b3267f1f6cdc1a9fb8cfe1c93dde22da273ecd2"': 'SOURCE = "227b3d88938543766a27d86a94ade52f9e5b89b7"',
    'TREE = "fcb432c3df3b5930bbc2900f43235384130b97ae"': 'TREE = "69d4faf228d9d0b98d77ecb4a8dac582ad33421b"',
    'REVIEW = "7662ed580eb924756885db7ab3a7d7a04e8ea8ea"': 'REVIEW = "d77973943421bd0aeb9d69d09edb4992e0c94688"',
    "manual-old-ready-v027-before-v0210-20261105-1": "manual-old-ready-v029-before-v0210-20261105-1",
    "next-source-v0210-final-20261105-1/review.json": "next-source-v0210-ui-acceptance-20261105-1/final-review.json",
    "publication-v0.2.8-20261105-1/state.json": "publication-v0.2.9-20261105-1/state.json",
    "64627391": "64628500",
    "f8f9de897b637f8c98a07d601420885bf65529701d014fc52b59f87ce75bace8": "42c32435e5883608c77f097befd2b9a7f91845b35942a09966018361c40a779e",
    "assert len(feed) == 3": "assert len(feed) == 4",
    'candidate["PublicAssets"] == 19': 'candidate["PublicAssets"] == 20',
    "C:\\IrisAcceptanceUpgrade\\": "C:\\IrisAcceptanceReview\\",
    'assert baseline["OldReadyVerified"] and baseline["SandboxId"] == SANDBOX and baseline["PendingTransactionAbsent"]':
        'assert baseline["OldReadyVerified"] and baseline["SandboxId"] == SANDBOX and baseline["PendingTransactionAbsent"]\n'
        '    assert baseline["UserScreenshotVisuallyReviewed"] and baseline["OldAndNewSyntheticHistoryVerified"]\n'
        '    assert candidate["Manual029BaselineVerified"]',
}
for before, after in replacements.items():
    assert before in text, before
    text = text.replace(before, after)
assert 'TAG = "v0.2.10"' in text and "v029-execution" not in text
assert 'OldVersion": "0.2.9"' in text and '"Iris-0.2.9-full.nupkg"' in text
assert "release-0.2.10-public-base-candidate-1" in text
compile(text, "execute_v0210_20261105.py", "exec")
(ROOT / "execute_v0210_20261105.py").open("x", encoding="utf-8", newline="\n").write(text)
print("Prepared read-only 0.2.10 executor for fixed 0.2.9; no remote operation performed.")
