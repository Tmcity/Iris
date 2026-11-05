"""Retrieve public artifacts only after binding their digest to the official run page."""
from pathlib import Path
from concurrent.futures import ThreadPoolExecutor
import datetime
import hashlib
import html
import json
import re
import subprocess

ROOT = Path("F:/Iris")
PUBLICATION = ROOT / "tasks/done/T-039C.evidence/publication-v0.2.7-20261105-2"
PAGE = PUBLICATION / "observations/20261105T060422754203Z-web-run/response.body"
page = PAGE.read_text(encoding="utf-8")
expected = {
    "iris-validation-41976966535-1": (13392382636, "f74d5800df0525b1594f5793394c8d6992f35b442ed429d9a7504bd4389f27a9"),
    "iris-publication-41976966535-1": (13392428250, "22e043efae52700f56676539b6c87848ac86d4b2bd71d7755129656454b72cb8"),
}
matched = {}
for row in re.findall(r"<tr\b[^>]*>.*?</tr>", page, re.S):
    name = re.search(r"iris-(?:validation|publication)[\w.-]+", html.unescape(re.sub(r"<[^>]+>", " ", row)))
    artifact = re.search(r"artifact-(\d+)-hash", row)
    digest = re.search(r"sha256:([a-f0-9]{64})", row)
    if name and artifact and digest and name[0] in expected:
        matched[name[0]] = (int(artifact[1]), digest[1])
assert matched == expected and "fc44a3a78c2729c25dfb91296c51602b843a94f9" in page
LOGS = PUBLICATION / "ci-artifact-download-20261105-1"
LOGS.mkdir(exist_ok=False)


def download(name):
    destination = ROOT / "artifacts/t039c" / ("ci-v027-original-41976966535-1.zip" if name.startswith("iris-validation") else "ci-v027-publication-41976966535-1.zip")
    assert not destination.exists()
    folder = LOGS / name
    folder.mkdir()
    url = "https://nightly.link/Tmcity/Iris/actions/runs/41976966535/" + name + ".zip"
    command = ["curl.exe", "--silent", "--show-error", "--location", "--max-time", "90", "--retry", "0",
        "--proto", "=https", "--proto-redir", "=https", "-D", str(folder / "response.headers"),
        "-o", str(destination), "--write-out", "%{http_code}", url]
    process = subprocess.Popen(command, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    output, error = process.communicate()
    (folder / "stderr.log").write_bytes(error)
    data = destination.read_bytes() if destination.exists() else b""
    record = {"Url": url, "ObservedUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
              "ExitCode": process.returncode, "HttpStatus": output.decode("ascii").strip(), "AuthorizationSent": False,
              "OriginalPath": str(destination), "Bytes": len(data), "Sha256": hashlib.sha256(data).hexdigest(),
              "OfficialArtifactId": expected[name][0], "OfficialPageDigest": expected[name][1],
              "OfficialPagePath": str(PAGE), "OfficialPageSha256": hashlib.sha256(PAGE.read_bytes()).hexdigest(),
              "OfficialRestApiDigestVerified": False}
    (folder / "download.json").write_text(json.dumps(record, indent=2) + "\n", encoding="utf-8", newline="\n")
    assert process.returncode == 0 and record["HttpStatus"] == "200"
    assert 0 < len(data) <= 4 * 1024 * 1024 and record["Sha256"] == expected[name][1]
    print(json.dumps({"Name": name, "Bytes": len(data), "OfficialPageDigestMatched": True}), flush=True)


with ThreadPoolExecutor(max_workers=2) as pool:
    list(pool.map(download, expected))
