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
PUBLIC = ROOT / "artifacts/t039c/public-0.2.9-download-20261105-1"
PUBLICATION = ROOT / "tasks/done/T-039C.evidence/publication-v0.2.9-20261105-1"
REPORT = ROOT / "tasks/done/T-039C.evidence/ci-v029-original-42028799541-1"
ASSET_PAGE = PUBLICATION / "observations/20261105T124757627859Z-web-assets/response.body"
SOURCE = "0b3267f1f6cdc1a9fb8cfe1c93dde22da273ecd2"
TREE = "fcb432c3df3b5930bbc2900f43235384130b97ae"


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def sha(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


assets = {}
for row in re.findall(r"<li\b[^>]*>.*?</li>", ASSET_PAGE.read_text(encoding="utf-8"), re.S):
    names = re.findall(r'/Tmcity/Iris/releases/download/v0\.2\.9/([^"<>?]+)', row)
    if not names:
        continue
    assert len(set(names)) == 1
    name = html.unescape(names[0])
    digests = set(re.findall(r"sha256:([a-f0-9]{64})", row))
    assert len(digests) == 1 and name not in assets
    path = PUBLIC / name
    assert path.is_file() and sha(path) == next(iter(digests))
    assets[name] = {"Name": name, "Bytes": path.stat().st_size, "Sha256": sha(path)}
assert len(assets) == 19 and set(assets) == {path.name for path in PUBLIC.iterdir()}
public_review = read(PUBLICATION / "public-download-20261105-1/review.json")
assert public_review["SourceCommit"] == SOURCE and public_review["SourceGitTree"] == TREE
assert public_review["PublicAssets"] == 19 and public_review["PayloadEntries"] == 97
assert public_review["FullSha256"] == "42c32435e5883608c77f097befd2b9a7f91845b35942a09966018361c40a779e"
assert public_review["OuterSha256"] == "69ab96bb1f700b21fa430d69eab85d6d4a5c606c377424222f0f97af0e7f2891"
extracted = []
specs = (("ci-v029-original-42028799541-1.zip", "validation", "ecc5a3ebf25404603d84c9aa729c74c35602664db122779ce6da095cbe641888"),
         ("ci-v029-publication-42028799541-1.zip", "publication", "9b194a0b291c07961af420ca0bef219fddbb7899ef2a968c1e4c71df509b2c0a"))
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
assert totals == public_review["FullTests"] == {"Total": 3771, "Passed": 3770, "Failed": 0, "Skipped": 1}
skipped = [{"Name": row.get("name"), "Reason": row.findtext("reason", "")} for row in tests if row.get("result") == "Skip"]
assert len(skipped) == 1 and "error=183" in skipped[0]["Reason"]
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
assert receipt["Tag"] == "v0.2.9" and receipt["SourceCommit"] == SOURCE and receipt["SourceGitTree"] == TREE
assert receipt["Stage"] == "published-and-listed" and receipt["Public"] is True
assert receipt["VisibilityVerification"] == "completed" and receipt["PublicationCliConfirmed"] is True
assert receipt["ReleaseUrl"] == "https://github.com/Tmcity/Iris/releases/tag/v0.2.9"
files = [{"Path": path.relative_to(REPORT).as_posix(), "Bytes": path.stat().st_size, "Sha256": sha(path)}
         for path in sorted(REPORT.rglob("*")) if path.is_file()]
binding = {"RunId": 42028799541, "RunAttempt": 1, "SourceCommit": SOURCE, "SourceGitTree": TREE,
           "ArtifactDigestsMatchedOfficialPublicPage": True, "OfficialRestApiArtifactMetadataVerified": False,
           "FullTests": totals, "SkippedTests": skipped, "HostTests": 13, "ThemeTests": 8, "UpdateHttpTests": 15,
           "ThemeIntervalsDoNotOverlapOtherAppTests": True, "PublicValidationAssetsMatchOriginalCiBytes": True,
           "PublicationReceipt": receipt, "ArchiveExtractedFiles": len(extracted), "Files": files}
(REPORT / "binding.json").open("x", encoding="utf-8", newline="\n").write(json.dumps(binding, indent=2) + "\n")
web_binding = {"ReviewedUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(), "Version": "0.2.9",
               "SourceCommit": SOURCE, "SourceGitTree": TREE, "GitHubRunId": 42028799541, "GitHubRunAttempt": 1,
               "All19PublicAssetBytesMatchOfficialPageDigests": True, "Assets": list(assets.values()),
               "AssetPage": str(ASSET_PAGE), "AssetPageSha256": sha(ASSET_PAGE), "FullTests": totals,
               "OriginalCiAndPublicationReceiptVerified": True, "OfficialRestApiAssetMetadataVerified": False,
               "RawApiQuotaResponseRetained": True, "ReleasePublic": True, "RealUiUpgradeVerified": False}
(PUBLICATION / "official-website-asset-binding.json").open("x", encoding="utf-8", newline="\n").write(json.dumps(web_binding, indent=2) + "\n")
state_path = PUBLICATION / "state.json"
state = read(state_path)
state.update(Stage="published-official-website-and-anonymous-bytes-verified", CiStatus="completed", CiConclusion="success",
             CiRunId=42028799541, CiRunAttempt=1, PackageJobId=127249641900, PublishJobId=127253344007,
             ReleasePublic=True, ReleaseUrl=receipt["ReleaseUrl"], AllPublicAssetBytesVerified=True,
             PublicAssets=19, PayloadEntries=97, FullTests=totals, OfficialRestApiMetadataVerified=False)
state_path.write_text(json.dumps(state, indent=2) + "\n", encoding="utf-8", newline="\n")
print(json.dumps({"Version": "0.2.9", "FullTests": totals, "OriginalCiFiles": len(extracted),
                  "All19AssetsMatchOfficialWebsiteDigests": True, "PublicationReceiptVerified": True,
                  "OfficialApiMetadataVerified": False, "ReleasePublic": True}, indent=2))
