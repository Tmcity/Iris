"""Read public GitHub state without credentials or remote mutations."""

from concurrent.futures import ThreadPoolExecutor, as_completed
from datetime import datetime, timezone
from hashlib import sha256
from pathlib import Path
import json
import ssl
import sys
import urllib.error
import urllib.request


ROOT = Path(__file__).resolve().parents[2]
API = "https://api.github.com/repos/Tmcity/Iris"
ENDPOINTS = {
    "original-ci-run": "/actions/runs/40193113435",
    "public-releases": "/releases?per_page=10",
    "v021-release": "/releases/tags/v0.2.1",
    "v022-release": "/releases/tags/v0.2.2",
    "version-tag-refs": "/git/matching-refs/tags/v0.2.",
    "public-main": "/git/ref/heads/main",
}


def read_endpoint(label, suffix, destination):
    request = urllib.request.Request(
        API + suffix,
        headers={
            "Accept": "application/vnd.github+json",
            "User-Agent": "Iris-architect-read-only-review",
            "X-GitHub-Api-Version": "2022-11-28",
        },
        method="GET",
    )
    opener = urllib.request.build_opener(
        urllib.request.ProxyHandler({}),
        urllib.request.HTTPSHandler(context=ssl.create_default_context()),
    )
    try:
        response = opener.open(request, timeout=20)
    except urllib.error.HTTPError as error:
        response = error
    except (OSError, urllib.error.URLError) as error:
        return {"Label": label, "Url": request.full_url, "ErrorType": type(error).__name__}, None
    with response:
        payload = response.read(2_000_001)
        if len(payload) > 2_000_000:
            raise RuntimeError("Public observation exceeded its response budget")
        status = response.code
    path = destination / (label + ".json")
    with path.open("xb") as output:
        output.write(payload)
    row = {
        "Label": label,
        "Url": request.full_url,
        "HttpStatus": status,
        "Bytes": len(payload),
        "Sha256": sha256(payload).hexdigest(),
        "RawPath": path.relative_to(ROOT).as_posix(),
    }
    try:
        parsed = json.loads(payload)
    except (UnicodeError, json.JSONDecodeError):
        parsed = None
    return row, parsed


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    now = datetime.now(timezone.utc)
    stamp = now.strftime("%Y%m%dT%H%M%S%fZ")
    destination = ROOT / "artifacts/t039c/public-observations" / stamp
    destination.mkdir(parents=True, exist_ok=False)
    rows = []
    parsed = {}
    with ThreadPoolExecutor(max_workers=4) as executor:
        futures = {
            executor.submit(read_endpoint, label, suffix, destination): label
            for label, suffix in ENDPOINTS.items()
        }
        for future in as_completed(futures):
            label = futures[future]
            row, value = future.result()
            rows.append(row)
            parsed[label] = value
    statuses = {row["Label"]: row.get("HttpStatus") for row in rows}
    run = parsed.get("original-ci-run")
    releases = parsed.get("public-releases")
    refs = parsed.get("version-tag-refs")
    branch = parsed.get("public-main")
    receipt = {
        "ObservedAt": now.isoformat(),
        "Repository": "https://github.com/Tmcity/Iris",
        "Identity": "Unauthenticated GET requests; no authorization headers, cookies or proxy credentials",
        "Observations": sorted(rows, key=lambda row: row["Label"]),
        "OriginalRun": {key: run.get(key) for key in ("id", "status", "conclusion", "run_attempt", "head_sha", "event", "html_url")}
        if statuses["original-ci-run"] == 200 and isinstance(run, dict) else None,
        "PublicReleaseTags": [release.get("tag_name") for release in releases]
        if statuses["public-releases"] == 200 and isinstance(releases, list) else None,
        "VersionTagRefs": {ref.get("ref"): ref.get("object", {}).get("sha") for ref in refs}
        if statuses["version-tag-refs"] == 200 and isinstance(refs, list) else None,
        "PublicMain": branch.get("object", {}).get("sha")
        if statuses["public-main"] == 200 and isinstance(branch, dict) else None,
        "Scope": "Public-state observation only. Anonymous release 404 cannot rule out hidden drafts. No workflow run, push, tag creation, Release write, release-asset download or installation performed.",
    }
    receipt_path = ROOT / "tasks/T-039C.coordination" / ("public-observation-" + stamp + ".json")
    with receipt_path.open("x", encoding="utf-8") as output:
        json.dump(receipt, output, ensure_ascii=False, indent=2)
        output.write("\n")
    print(json.dumps({
        "Receipt": str(receipt_path),
        "HttpStatuses": statuses,
        "OriginalRun": receipt["OriginalRun"],
        "PublicReleaseTags": receipt["PublicReleaseTags"],
        "VersionTagRefs": receipt["VersionTagRefs"],
        "PublicMain": receipt["PublicMain"],
    }, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
