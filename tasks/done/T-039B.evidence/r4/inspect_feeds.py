from pathlib import Path
import hashlib
import json
from inspect_delivery import file_record, json_read, sha

evidence = Path(__file__).resolve().parent
root = evidence.parents[3]
active = json_read(evidence / "active_round.json")
results = []
for version in ("0.2.0", "0.2.1"):
    directory = root / ("artifacts/t039b/release-" + version + "-" + active["PackageTag"])
    assets = json_read(directory / "releases.win.json")["Assets"]
    for asset in assets:
        expected = f'Iris-{asset["Version"]}-{asset["Type"].lower()}.nupkg'
        assert asset["PackageId"] == "Iris" and asset["FileName"] == expected
        path = directory / expected
        assert path.stat().st_size == asset["Size"]
        assert sha(path).upper() == asset["SHA256"].upper()
        with path.open("rb") as file:
            assert hashlib.file_digest(file, "sha1").hexdigest().upper() == asset["SHA1"].upper()
    legacy_entries = []
    for line in (directory / "RELEASES").read_text(encoding="utf-8-sig").splitlines():
        digest, name, size = line.split()
        path = directory / name
        assert path.stat().st_size == int(size)
        with path.open("rb") as file:
            assert hashlib.file_digest(file, "sha1").hexdigest().upper() == digest.upper()
        legacy_entries.append({"FileName": name, "Size": int(size), "SHA1": digest})
    results.append({"Version": version, "Feed": file_record(directory / "releases.win.json"), "Assets": assets,
        "LegacyFeed": file_record(directory / "RELEASES"), "LegacyEntries": legacy_entries})
target = evidence / active.get("FeedInspection", "feed-inspection-1.json")
if target.exists(): raise FileExistsError(target)
target.write_text(json.dumps({"PackageTag": active["PackageTag"], "Results": results}, indent=2) + "\n", encoding="utf-8")
print(json.dumps({"PackageTag": active["PackageTag"], "FeedAssetsVerified": [len(item["Assets"]) for item in results],
    "LegacyEntriesVerified": [len(item["LegacyEntries"]) for item in results]}))
