from pathlib import Path
import json
import re
from inspect_delivery import file_record, json_read, sha

evidence = Path(__file__).resolve().parent
root = evidence.parents[3]
before = json_read(evidence / "localization-before-2/audit-result.json")
after = json_read(evidence / "localization-after-1/audit-result.json")
inputs = json_read(evidence / "localization-before-2/input-binding.json")
unchanged = []
for item in inputs:
    if item["Path"] in ("build/localization/exceptions.json", "build/localization/audit.py", "build/LocalizationAudit/Program.cs", "build/LocalizationAudit/bin/Release/net10.0/LocalizationAudit.dll") or item["Path"].endswith(".resx"):
        assert sha(root / item["Path"]) == item["Sha256"], item["Path"]
        unchanged.append(item)
approved_path = root / "tasks/done/T-039B.review/acceptance-review-4/localization-before-1/approved-catalog-additions.json"
approved = json_read(approved_path)["App"]
original = json_read(evidence / "before-localization-fix/app-catalog.json.txt")
current = json_read(root / "build/localization/app-catalog.json")
assert set(current) - set(original) == set(approved)
assert all(current[key] == value for key, value in original.items())
assert all(current[key] == value for key, value in approved.items())
assert before["unapproved"] and before["resourceErrors"]
assert not after["unapproved"] and not after["resourceErrors"]
assert before["exceptionManifestSha256"] == after["exceptionManifestSha256"] == sha(root / "build/localization/exceptions.json")
commands = {}
for label, expected in [("localization-inventory-before-2", 0), ("localization-catalog-before-2", 1), ("localization-inventory-after-1", 0), ("localization-catalog-after-1", 0)]:
    command = json_read(evidence / (label + ".command.json"))
    assert command["ExitCode"] == expected
    assert "--migrate" not in command["Command"]
    commands[label] = file_record(evidence / (label + ".command.json"))
compiled = json_read(evidence / "localization-after-1/compiled-binding.json")
for item in compiled:
    path = Path(item["FrozenCopy"])
    assert path.stat().st_size == item["Bytes"] and sha(path).upper() == item["Sha256"].upper()
log = (evidence / "localization-inventory-after-1.log").read_text(encoding="utf-8")
matched = int(re.search(r"composite formats parsed: (\d+)", log).group(1))
assert matched == sum(after["resourceCounts"].values()) * 2
result = {"Before": {"Result": file_record(evidence / "localization-before-2/audit-result.json"), "SourceEntries": before["sourceEntries"],
    "UnapprovedSites": len(before["unapproved"]), "ResourceErrors": before["resourceErrors"]},
    "After": {"Result": file_record(evidence / "localization-after-1/audit-result.json"), "SourceEntries": after["sourceEntries"],
    "UnapprovedSites": len(after["unapproved"]), "ResourceErrors": after["resourceErrors"], "ResourceCounts": after["resourceCounts"], "CompiledResourceValuesMatched": matched},
    "ApprovedAdditions": file_record(approved_path), "AddedKeys": list(approved), "OriginalCatalogPairsUnchanged": len(original),
    "CurrentCatalogKeys": len(current), "UnchangedScannerBudgetAndResources": unchanged, "CompiledInputs": compiled, "Commands": commands,
    "MigrationExecuted": False}
target = evidence / "localization-verification-1.json"
if target.exists(): raise FileExistsError(target)
target.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
print(json.dumps({"BeforeUnapprovedSites": len(before["unapproved"]), "AfterUnapprovedSites": len(after["unapproved"]),
    "BeforeSourceEntries": before["sourceEntries"], "AfterSourceEntries": after["sourceEntries"], "CompiledValuesMatched": matched,
    "CatalogKeys": len(current), "ExistingPairsUnchanged": len(original), "ExceptionManifestSha256": after["exceptionManifestSha256"]}))
