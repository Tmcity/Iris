"""Read public GitHub metadata without loading a credential store or sending Authorization."""
from pathlib import Path
import datetime
import hashlib
import json
import urllib.error
import urllib.request

evidence = Path(__file__).resolve().parent
destination = evidence / "public-state-1"
destination.mkdir(exist_ok=False)
observations = []
for name, path in [("repository", ""), ("releases", "/releases?per_page=100"), ("latest", "/releases/latest")]:
    url = "https://api.github.com/repos/Tmcity/Iris" + path
    request = urllib.request.Request(url, headers={"User-Agent": "Iris-T039C-Public-Read", "Accept": "application/vnd.github+json"})
    assert not request.has_header("Authorization")
    try:
        response = urllib.request.urlopen(request, timeout=45)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        body = response.read(2 * 1024 * 1024 + 1)
        assert len(body) <= 2 * 1024 * 1024
        status = response.status
        headers = {key: response.headers.get(key) for key in ["Date", "Content-Type", "X-RateLimit-Limit", "X-RateLimit-Remaining"]}
    (destination / (name + ".json")).write_bytes(body)
    observations.append({"Name": name, "Url": url, "Status": status, "Bytes": len(body),
        "Sha256": hashlib.sha256(body).hexdigest(), "Headers": headers, "AuthorizationSent": False,
        "ObservedUtc": datetime.datetime.now(datetime.timezone.utc).isoformat()})
(destination / "observations.json").write_text(json.dumps(observations, indent=2) + "\n", encoding="utf-8", newline="\n")
print(json.dumps(observations, indent=2))
