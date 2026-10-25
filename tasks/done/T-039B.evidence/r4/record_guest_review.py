"""Bind the two actual guest PNGs individually viewed by the implementing engineer."""
from pathlib import Path
import json
from inspect_delivery import file_record, json_read

evidence = Path(__file__).resolve().parent
target = evidence / "reviewed-guests-r4-1.json"
if target.exists():
    raise FileExistsError(target)
manifest = json_read(evidence / "guest-run-manifest.json")
frames = []
for scenario, relative, identity_name in [
    ("install", "output/installed-settings.png", "installed-identity"),
    ("update", "output/update-r4/updated-settings.png", "new-identity"),
]:
    directory = evidence / manifest[scenario]
    result_path = directory / "output" / ("update-r4" if scenario == "update" else "") / "result.json"
    result = json_read(result_path)
    identity = next(item["Value"] for item in result["Checks"] if item["Name"] == identity_name)
    frames.append({"Scenario": scenario, "Image": file_record(directory / relative), "VersionFromRawResult": identity["Version"],
        "IndividuallyViewedWith": "functions view_image", "Observed": "Actual guest desktop with English Iris Settings / General, visible software update version, readable text and Save/Cancel buttons; Light appearance.",
        "Limit": "The viewport does not show every update action. These PNGs are not recovery-window Dark/UI or public online-path acceptance; separately bound rendering/regressions cover their stated scope."})
target.write_text(json.dumps({"Frames": frames, "Scope": "Engineer actual per-image observation, distinct from architect independent visual review and future whole-task acceptance."},
    ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")
print(json.dumps({"ViewedFrames": len(frames), "Versions": [f["VersionFromRawResult"] for f in frames]}))
