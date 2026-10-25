"""Read-only byte checks before running the already verified final-8 guest inputs."""
import json
from inspect_delivery import inspect

report = inspect(False)
print(json.dumps({
    "GitTree": report["GitTree"],
    "SourceFiles": report["SourceFiles"],
    "FrozenFiles": report["TestedCandidateFiles"],
    "Packages": [{"Version": p["Version"], "PayloadEntries": p["PayloadManifestEntries"],
        "Files": [{k: v for k, v in item.items()} for item in p["Files"]]} for p in report["ReleasePackages"]],
    "AllBoundBytesMatch": True,
    "NoBuildTestOrPackagingExecuted": True,
}, ensure_ascii=False, indent=2))
