from pathlib import Path
import hashlib
import json
import xml.etree.ElementTree as ET

root = Path(__file__).resolve().parents[4]
evidence = Path(__file__).resolve().parent
approved_path = root / "tasks/done/T-039B.review/acceptance-review-4/localization-before-1/approved-catalog-additions.json"
approved = json.loads(approved_path.read_text(encoding="utf-8"))["App"]
catalog_path = root / "build/localization/app-catalog.json"
original = json.loads(catalog_path.read_text(encoding="utf-8"))
assert not original.keys() & approved.keys()
assert len(approved) == 5
for language, suffix in [("zh-CN", ""), ("en-US", ".en-US")]:
    values = {item.attrib["name"]: item.findtext("value") for item in ET.parse(root / ("src/Iris.App/Resources/Strings" + suffix + ".resx")).getroot().findall("data")}
    for key, pair in approved.items(): assert values[key] == pair[language], key
updated = dict(original)
updated.update(approved)
assert all(updated[key] == value for key, value in original.items())
catalog_path.write_text(json.dumps(updated, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")
result = {"OriginalCatalogKeys": len(original), "AddedKeys": list(approved), "CurrentCatalogKeys": len(updated),
    "ApprovedInputSha256": hashlib.sha256(approved_path.read_bytes()).hexdigest(), "ExistingPairsUnchanged": True,
    "ExceptionManifestSha256": hashlib.sha256((root / "build/localization/exceptions.json").read_bytes()).hexdigest()}
(evidence / "catalog-sync-result.json").write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
print(json.dumps(result))
