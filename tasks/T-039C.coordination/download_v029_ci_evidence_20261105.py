"""Fetch original CI ZIPs after official page digest binding, supplementing with API when available."""
from pathlib import Path
from concurrent.futures import ThreadPoolExecutor
import argparse
import datetime
import hashlib
import html
import json
import re
import subprocess

ROOT = Path("F:/Iris")
PUBLICATION = ROOT / "tasks/done/T-039C.evidence/publication-v0.2.9-20261105-1"
parser = argparse.ArgumentParser()
parser.add_argument("--metadata", type=Path)
parser.add_argument("--run-page", type=Path, required=True)
args = parser.parse_args()
for path in (path for path in (args.metadata, args.run_page) if path is not None):
    assert path.resolve().is_relative_to(PUBLICATION / "observations")
metadata = json.loads(args.metadata.read_text(encoding="utf-8-sig")) if args.metadata else None
page = args.run_page.read_text(encoding="utf-8")
assert "0b3267f1f6cdc1a9fb8cfe1c93dde22da273ecd2" in page
names = ("iris-validation-42028799541-1", "iris-publication-42028799541-1")
expected = {item["name"]: item for item in metadata["artifacts"] if item["name"] in names} if metadata else {}
if metadata:
    assert set(expected) == set(names)
matched = {}
for row in re.findall(r"<tr\b[^>]*>.*?</tr>", page, re.S):
    name = re.search(r"iris-(?:validation|publication)[\w.-]+", html.unescape(re.sub(r"<[^>]+>", " ", row)))
    artifact = re.search(r"artifact-(\d+)-hash", row)
    digest = re.search(r"sha256:([a-f0-9]{64})", row)
    if name and artifact and digest and name[0] in names:
        matched[name[0]] = (int(artifact[1]), digest[1])
assert set(matched) == set(names)
for name, item in expected.items():
    assert matched[name] == (item["id"], item["digest"].removeprefix("sha256:"))
    assert item["expired"] is False and 0 < item["size_in_bytes"] <= 16 * 1024 * 1024
LOGS = PUBLICATION / "ci-artifact-download-20261105-1"
LOGS.mkdir(exist_ok=False)


def download(name):
    item = expected.get(name)
    destination = ROOT / "artifacts/t039c" / ("ci-v029-original-42028799541-1.zip" if name.startswith("iris-validation") else "ci-v029-publication-42028799541-1.zip")
    assert not destination.exists()
    folder = LOGS / name
    folder.mkdir()
    url = "https://nightly.link/Tmcity/Iris/actions/runs/42028799541/" + name + ".zip"
    command = ["curl.exe", "--silent", "--show-error", "--location", "--max-time", "90", "--retry", "0",
        "--proto", "=https", "--proto-redir", "=https", "-D", str(folder / "response.headers"),
        "-o", str(destination), "--write-out", "%{http_code}", url]
    started = datetime.datetime.now(datetime.timezone.utc).isoformat()
    process = subprocess.Popen(command, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    output, error = process.communicate()
    (folder / "stderr.log").open("xb").write(error)
    data = destination.read_bytes() if destination.exists() else b""
    record = {"Url": url, "StartedUtc": started, "FinishedUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
              "ExitCode": process.returncode, "HttpStatus": output.decode("ascii").strip(), "AuthorizationSent": False,
              "OriginalPath": str(destination), "Bytes": len(data), "Sha256": hashlib.sha256(data).hexdigest(),
              "OfficialArtifactId": matched[name][0], "OfficialPageDigest": matched[name][1], "OfficialApiDigest": item["digest"] if item else None,
              "OfficialPagePath": str(args.run_page), "OfficialPageSha256": hashlib.sha256(args.run_page.read_bytes()).hexdigest(),
              "OfficialRestApiDigestVerified": item is not None}
    (folder / "download.json").open("x", encoding="utf-8", newline="\n").write(json.dumps(record, indent=2) + "\n")
    assert process.returncode == 0 and record["HttpStatus"] == "200"
    assert 0 < len(data) <= 16 * 1024 * 1024 and record["Sha256"] == matched[name][1]
    if item:
        assert len(data) == item["size_in_bytes"]
    print(json.dumps({"Name": name, "Bytes": len(data), "OfficialPageDigestMatched": True,
                      "OfficialApiDigestMatched": item is not None}), flush=True)


with ThreadPoolExecutor(max_workers=2) as pool:
    list(pool.map(download, names))
