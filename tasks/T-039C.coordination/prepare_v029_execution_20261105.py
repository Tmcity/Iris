"""Prepare the existing bounded tag-only executor for the separately reviewed 0.2.9."""
from pathlib import Path

ROOT = Path("F:/Iris")
source = ROOT / "tasks/T-039C.coordination/execute_v028_final_20261105.py"
target = ROOT / "tasks/T-039C.coordination/execute_v029_20261105.py"
text = source.read_text(encoding="utf-8-sig").replace("0.2.8", "0.2.9").replace("v028", "v029")
replacements = {
    'SOURCE = "0c9b0a1881d07224597163b1e9b2f080ab60d434"': 'SOURCE = "0b3267f1f6cdc1a9fb8cfe1c93dde22da273ecd2"',
    'TREE = "6928cec3575944b9154abbc129ac848ab943b872"': 'TREE = "fcb432c3df3b5930bbc2900f43235384130b97ae"',
    'REVIEW = "422525535cdc46fcc10a6533d0f88e2cac2a89e8"': 'REVIEW = "7662ed580eb924756885db7ab3a7d7a04e8ea8ea"',
    "manual-old-ready-v027-before-final028-20261105-1": "manual-old-ready-v027-before-v029-20261105-1",
    "next-source-v029-native-fixed-20261105-2": "next-source-v029-final-20261105-1",
    "release-0.2.9-public-base-candidate-2": "release-0.2.9-public-base-candidate-1",
    "v029-final-execution-preflight-": "v029-execution-preflight-",
    "publication-v0.2.7-20261105-2/state.json": "publication-v0.2.8-20261105-1/state.json",
    "C:\\IrisAcceptanceInput\\": "C:\\IrisAcceptanceUpgrade\\",
    'assert baseline["OldReadyVerified"] and baseline["SandboxId"] == SANDBOX':
        'assert baseline["OldReadyVerified"] and baseline["SandboxId"] == SANDBOX and baseline["PendingTransactionAbsent"]',
    'assert not any("0.2.9" in item["Name"] for item in old["Packages"])':
        'assert not any(any(version in item["Name"] for version in ("0.2.8", "0.2.9")) for item in old["Packages"])\n'
        '    assert not any("transaction.json" in item["Path"].lower() for item in supplement["MaintenanceFiles"])',
}
for before, after in replacements.items():
    assert before in text, before
    text = text.replace(before, after)
assert 'TAG = "v0.2.9"' in text and "v028" not in text and "C:\\IrisAcceptanceInput\\" not in text
assert "0c9b0a1881d07224597163b1e9b2f080ab60d434" not in text
assert "publication-v0.2.8-20261105-1/state.json" in text
target.open("x", encoding="utf-8", newline="\n").write(text)
print("Prepared 0.2.9 executor; default is read-only and old approvals cannot authorize publication.")
