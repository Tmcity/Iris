"""Read-only verification of the delivered R3 source, package and guest records."""
from pathlib import Path
from collections import Counter
import hashlib
import json
import subprocess
import zipfile

ROOT = Path(__file__).resolve().parents[4]
OUT = Path(__file__).resolve().parent
BINDING = ROOT / "tasks/done/T-039B.evidence/delivery-binding.json"
records = []


def sha(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def check(name, condition, **detail):
    records.append(dict(name=name, passed=bool(condition), **detail))


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def git(*args):
    return subprocess.check_output(["git", *args], cwd=ROOT)


binding = read_json(BINDING)
head = git("rev-parse", "HEAD").decode().strip()
tree = {}
for item in git("ls-tree", "-rz", "HEAD").split(b"\0"):
    if item:
        meta, path = item.split(b"\t", 1)
        tree[path.decode("utf-8")] = meta.decode().split()[2]

for group, hashkey in [("SourceFiles", "WorkingSha256"), ("Evidence", "Sha256"), ("FinalGuestImages", "Sha256")]:
    for item in binding[group]:
        path = ROOT / item["Path"]
        actual = sha(path) if path.is_file() else None
        check(f"{group}:sha:{item['Path']}", actual == item[hashkey].lower(), expected=item[hashkey], actual=actual)
        if "GitBlob" in item:
            check(f"{group}:blob:{item['Path']}", tree.get(item["Path"]) == item["GitBlob"], expected=item["GitBlob"], actual=tree.get(item["Path"]))
        if "Bytes" in item:
            check(f"{group}:size:{item['Path']}", path.is_file() and path.stat().st_size == item["Bytes"])

tree_diff = git("diff", "--name-status", binding["GitTree"], "HEAD").decode("utf-8").splitlines()
check("binding-tree-product-files", not any(x.split("\t")[-1].startswith(("src/", "tests/", "build/", ".github/", ".config/", "docs/")) for x in tree_diff), differences=tree_diff)
check("archived-r3-order", sha(ROOT / "tasks/done/T-039B.evidence/signed-order-r3.md") == binding["ArchivedOrderSha256"])

package_results = []
for package in binding["ReleasePackages"]:
    directory = Path(package["Directory"])
    names = {p.name for p in directory.iterdir() if p.is_file()}
    check(f"package:{package['Version']}:top-level-set", names == {p["Name"] for p in package["Files"]})
    for item in package["Files"]:
        path = directory / item["Name"]
        check(f"package:{package['Version']}:{item['Name']}", path.is_file() and path.stat().st_size == item["Bytes"] and sha(path) == item["Sha256"].lower())
    hashes = read_json(directory / "SHA256.json")
    check(f"package:{package['Version']}:SHA256-set", {p["Name"] for p in hashes} == names - {"SHA256.json"})
    for item in hashes:
        path = directory / item["Name"]
        check(f"SHA256:{package['Version']}:{item['Name']}", path.stat().st_size == item["Bytes"] and sha(path) == item["Sha256"].lower())
    manifest = read_json(directory / "payload-manifest.json")
    inventory = {p["Path"].replace("\\", "/"): p for p in manifest}
    with zipfile.ZipFile(directory / f"Iris-{package['Version']}-full.nupkg") as archive:
        payload = {p.filename[8:]: p for p in archive.infolist() if p.filename.startswith("lib/app/") and not p.is_dir()}
        check(f"payload:{package['Version']}:set", len(inventory) == len(manifest) == package["PayloadManifestEntries"] and set(inventory) == set(payload), manifestEntries=len(manifest), zipEntries=len(payload))
        for name, item in payload.items():
            with archive.open(item) as stream:
                digest = hashlib.file_digest(stream, "sha256").hexdigest()
            expected = inventory.get(name, {})
            check(f"payload:{package['Version']}:{name}", expected.get("Bytes") == item.file_size and expected.get("Sha256", "").lower() == digest)
        maintenance = archive.read("lib/app/maintenance/Iris.Maintenance.exe")
    outer = (directory / f"Iris-{package['Version']}-Setup.exe").read_bytes()
    footer_size = len(b"IRIS-INSTALL-V1!") + 8 + 32
    length = int.from_bytes(outer[-40:-32], "little", signed=True)
    offset = len(outer) - footer_size - length
    valid = 0 < length <= 1024**3 and offset > 0
    check(f"bundle:{package['Version']}:magic", outer[-footer_size:-40] == b"IRIS-INSTALL-V1!")
    check(f"bundle:{package['Version']}:length", valid, setupBytes=length, hostBytes=offset)
    check(f"bundle:{package['Version']}:setup-sha", valid and hashlib.sha256(outer[offset:-footer_size]).digest() == outer[-32:])
    check(f"bundle:{package['Version']}:maintenance", valid and outer[:offset] == maintenance)
    package_results.append(dict(version=package["Version"], payloadEntries=len(manifest), topLevelEntries=len(names)))

guest_results = []
for label, relative in [("guest-install-r3-2", "output/result.json"), ("guest-update-r3-1", "output/update-r3/result.json")]:
    folder = ROOT / "tasks/done/T-039B.evidence" / label
    intent = read_json(folder / "intent.json")
    inputs = Path(intent["ReadOnlyInput"])
    rows = intent["Files"]
    expected = Counter((p["Name"], p["Sha256"].lower()) for p in rows)
    actual = Counter((p.name, sha(p)) for p in inputs.rglob("*") if p.is_file())
    check(f"guest:{label}:input-multiset", expected == actual, expectedCount=sum(expected.values()), actualCount=sum(actual.values()), missing=list((expected-actual).elements()), extra=list((actual-expected).elements()))
    result = read_json(folder / relative)
    check(f"guest:{label}:failure-null", result.get("Failure") is None)
    false_checks = [p for p in result["Checks"] if p["Value"] is False]
    check(f"guest:{label}:no-false", not false_checks, failures=false_checks)
    check(f"guest:{label}:execute-exit", read_json(folder / "execute.command.json")["ExitCode"] == 0)
    check(f"guest:{label}:stopped", read_json(folder / "stop.command.json")["ExitCode"] == 0 and not read_json(folder / "list-after.log")["WindowsSandboxEnvironments"])
    guest_results.append(dict(label=label, sandboxId=intent["SandboxId"], observationCount=len(result["Checks"]), booleanAssertionCount=sum(isinstance(p["Value"], bool) for p in result["Checks"]), failures=false_checks))

result = dict(head=head, bindingSha256=sha(BINDING), checks=len(records), passed=sum(p["passed"] for p in records), failures=[p for p in records if not p["passed"]], packageResults=package_results, guestResults=guest_results, bindingTreeDifferences=tree_diff, records=records)
with (OUT / "delivery-check.json").open("x", encoding="utf-8") as output:
    json.dump(result, output, indent=2, ensure_ascii=False)
print(json.dumps({k:v for k,v in result.items() if k != "records"}, indent=2, ensure_ascii=False))
raise SystemExit(bool(result["failures"]))
