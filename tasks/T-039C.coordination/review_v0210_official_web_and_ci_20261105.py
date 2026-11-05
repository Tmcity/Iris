"""Bind every public asset to official website digests and freeze exact successful CI original bytes."""
from pathlib import Path, PurePosixPath
import datetime
import hashlib
import html
import json
import re
import stat
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path("F:/Iris")
PUBLIC = ROOT / "artifacts/t039c/public-0.2.10-download-20261105-2"
PUBLICATION = ROOT / "tasks/done/T-039C.evidence/publication-v0.2.10-20261105-1"
REPORT = ROOT / "tasks/done/T-039C.evidence/ci-v0210-original-42042550153-1"
ASSET_PAGE = PUBLICATION / "observations/20261105T142255665078Z-web-assets/response.body"
SOURCE = "227b3d88938543766a27d86a94ade52f9e5b89b7"
TREE = "69d4faf228d9d0b98d77ecb4a8dac582ad33421b"


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def sha(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


assets = {}
for row in re.findall(r"<li\b[^>]*>.*?</li>", ASSET_PAGE.read_text(encoding="utf-8"), re.S):
    names = re.findall(r'/Tmcity/Iris/releases/download/v0\.2\.10/([^"<>?]+)', row)
    if not names:
        continue
    assert len(set(names)) == 1
    name = html.unescape(names[0])
    digests = set(re.findall(r"sha256:([a-f0-9]{64})", row))
    assert len(digests) == 1 and name not in assets
    path = PUBLIC / name
    assert path.is_file() and sha(path) == next(iter(digests))
    assets[name] = {"Name": name, "Bytes": path.stat().st_size, "Sha256": sha(path)}
assert len(assets) == 20 and set(assets) == {path.name for path in PUBLIC.iterdir()}
public_review = read(PUBLICATION / "public-download-20261105-2/review.json")
assert public_review["SourceCommit"] == SOURCE and public_review["SourceGitTree"] == TREE
assert public_review["PublicAssets"] == 20 and public_review["PayloadEntries"] == 97
assert public_review["FullSha256"] == "dca307a0afc1827e27bc7ec2614e5ead3a30978d4c5d318e4d0d842ebc73f136"
assert public_review["OuterSha256"] == "56166ec3e71a0fad72b1eb135e119635e63720ebe0a8fcf301f58d7ef8e053c7"
extracted = []
specs = (("ci-v0210-original-42042550153-1.zip", "validation", "237f8958d4e8ddbf6ae28bddbf38c5c63598673a57d2413dbc6017105ae48e36"),
         ("ci-v0210-publication-42042550153-1.zip", "publication", "d08fde86b418f15fafbe090e0d62e504350e2f1520fdfcffbbab61ee410ec55d"))
seen, size = set(), 0
for name, folder, expected in specs:
    archive = ROOT / "artifacts/t039c" / name
    assert sha(archive) == expected
    with zipfile.ZipFile(archive) as zipped:
        assert len(zipped.infolist()) <= 2000
        for item in zipped.infolist():
            if item.is_dir():
                continue
            path = PurePosixPath(item.filename.replace("\\", "/"))
            assert not path.is_absolute() and ".." not in path.parts and ":" not in str(path)
            assert stat.S_IFMT(item.external_attr >> 16) != stat.S_IFLNK
            assert path.suffix in {".json", ".xml", ".log", ".png", ".txt"}
            size += item.file_size
            assert item.file_size < 16 * 1024 * 1024 and size < 64 * 1024 * 1024
            destination = (REPORT / folder).joinpath(*path.parts).resolve()
            assert destination.is_relative_to(REPORT.resolve()) and str(destination).casefold() not in seen
            seen.add(str(destination).casefold())
            extracted.append((destination, zipped.read(item)))
totals = {key: 0 for key in ("Total", "Passed", "Failed", "Skipped")}
tests = []
for destination, raw in extracted:
    if destination.suffix == ".xml":
        tree = ET.fromstring(raw)
        for assembly in tree.iter("assembly"):
            assert int(assembly.get("errors", 0)) == int(assembly.get("not-run", 0)) == 0
            for key in totals:
                totals[key] += int(assembly.get(key.lower(), 0))
        tests.extend(tree.iter("test"))
assert totals == public_review["FullTests"] == {"Total": 3771, "Passed": 3771, "Failed": 0, "Skipped": 0}
skipped = [{"Name": row.get("name"), "Reason": row.findtext("reason", "")} for row in tests if row.get("result") == "Skip"]
assert not skipped
host = [row for row in tests if row.get("type") == "Iris.App.Tests.Hosting.AppHostBuilderTests"]
theme = [row for row in tests if row.get("type") == "Iris.App.Tests.Services.ThemeServicePreviewTests"]
http = [row for row in tests if row.get("type") == "Iris.App.Tests.Updates.UpdateHttpTests"]
assert len(host) == 13 and len(theme) == 8 and len(http) == 15
assert all(row.get("result") == "Pass" for row in host + theme + http)
other = [row for row in tests if row.get("type", "").startswith("Iris.App.Tests.") and row not in theme]


def time(row, key):
    return datetime.datetime.fromisoformat(row.get(key))


assert all(time(t, "finish-rtf") <= time(o, "start-rtf") or time(t, "start-rtf") >= time(o, "finish-rtf") for t in theme for o in other)
REPORT.mkdir(exist_ok=False)
for name, _, _ in specs:
    (REPORT / name).open("xb").write((ROOT / "artifacts/t039c" / name).read_bytes())
for destination, raw in extracted:
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.open("xb").write(raw)
validation = REPORT / "validation/iris-validation"
summary = read(validation / "validation-summary.json")
assert summary["SourceCommit"] == SOURCE and summary["FullTests"] == totals
assert summary["BuildWarnings"] == summary["BuildErrors"] == 0
for name in ("release-manifest-tests.log", "release-build.log", "full-tests.log", "validation-summary.json"):
    assert (validation / name).read_bytes() == (PUBLIC / name).read_bytes(), name
for record in summary["Commands"]:
    assert record["ExitCode"] == 0 and sha(validation / record["Log"]) == record["Sha256"]
assert (REPORT / "validation/iris-package-logs/package.log").read_bytes() == (PUBLIC / "package.log").read_bytes()
receipt = read(REPORT / "publication/publish-result.json")
assert receipt["Tag"] == "v0.2.10" and receipt["SourceCommit"] == SOURCE and receipt["SourceGitTree"] == TREE
assert receipt["Stage"] == "published-and-listed" and receipt["Public"] is True
assert receipt["VisibilityVerification"] == "completed" and receipt["PublicationCliConfirmed"] is True
assert receipt["ReleaseUrl"] == "https://github.com/Tmcity/Iris/releases/tag/v0.2.10"
files = [{"Path": path.relative_to(REPORT).as_posix(), "Bytes": path.stat().st_size, "Sha256": sha(path)}
         for path in sorted(REPORT.rglob("*")) if path.is_file()]
binding = {"RunId": 42042550153, "RunAttempt": 1, "SourceCommit": SOURCE, "SourceGitTree": TREE,
           "ArtifactDigestsMatchedOfficialPublicPage": True, "OfficialRestApiArtifactMetadataVerified": False,
           "FullTests": totals, "SkippedTests": skipped, "HostTests": 13, "ThemeTests": 8, "UpdateHttpTests": 15,
           "ThemeIntervalsDoNotOverlapOtherAppTests": True, "PublicValidationAssetsMatchOriginalCiBytes": True,
           "PublicationReceipt": receipt, "ArchiveExtractedFiles": len(extracted), "Files": files}
(REPORT / "binding.json").open("x", encoding="utf-8", newline="\n").write(json.dumps(binding, indent=2) + "\n")
web_binding = {"ReviewedUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(), "Version": "0.2.10",
               "SourceCommit": SOURCE, "SourceGitTree": TREE, "GitHubRunId": 42042550153, "GitHubRunAttempt": 1,
               "All20PublicAssetBytesMatchOfficialPageDigests": True, "Assets": list(assets.values()),
               "AssetPage": str(ASSET_PAGE), "AssetPageSha256": sha(ASSET_PAGE), "FullTests": totals,
               "OriginalCiAndPublicationReceiptVerified": True, "OfficialRestApiAssetMetadataVerified": False,
               "RawApiQuotaResponseRetained": True, "ReleasePublic": True, "RealUiUpgradeVerified": False}
(PUBLICATION / "official-website-asset-binding.json").open("x", encoding="utf-8", newline="\n").write(json.dumps(web_binding, indent=2) + "\n")
state_path = PUBLICATION / "state.json"
state = read(state_path)
state.update(Stage="published-official-website-and-anonymous-bytes-verified", CiStatus="completed", CiConclusion="success",
             CiRunId=42042550153, CiRunAttempt=1, PackageJobId=127296844225, PublishJobId=127301558754,
             ReleasePublic=True, ReleaseUrl=receipt["ReleaseUrl"], AllPublicAssetBytesVerified=True,
             PublicAssets=20, PayloadEntries=97, FullTests=totals, OfficialRestApiMetadataVerified=False)
state_path.write_text(json.dumps(state, indent=2) + "\n", encoding="utf-8", newline="\n")
print(json.dumps({"Version": "0.2.10", "FullTests": totals, "OriginalCiFiles": len(extracted),
                  "All20AssetsMatchOfficialWebsiteDigests": True, "PublicationReceiptVerified": True,
                  "OfficialApiMetadataVerified": False, "ReleasePublic": True}, indent=2))
