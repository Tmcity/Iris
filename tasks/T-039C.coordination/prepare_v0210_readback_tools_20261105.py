"""Prepare original CI and anonymous asset readback tools for the approved 0.2.10."""
from pathlib import Path

ROOT = Path("F:/Iris/tasks/T-039C.coordination")
for before_name, after_name in (("download_public_v029_20261105.py", "download_public_v0210_20261105.py"),
                              ("download_v029_ci_evidence_20261105.py", "download_v0210_ci_evidence_20261105.py")):
    text = (ROOT / before_name).read_text(encoding="utf-8-sig")
    text = text.replace("0.2.9", "0.2.10").replace("v029", "v0210").replace("42028799541", "42042550153")
    text = text.replace("0b3267f1f6cdc1a9fb8cfe1c93dde22da273ecd2", "227b3d88938543766a27d86a94ade52f9e5b89b7")
    text = text.replace("fcb432c3df3b5930bbc2900f43235384130b97ae", "69d4faf228d9d0b98d77ecb4a8dac582ad33421b")
    if before_name.startswith("download_public"):
        text = text.replace('"Iris-0.2.7-full.nupkg", "release-manifest-tests.log",', '"Iris-0.2.7-full.nupkg", "Iris-0.2.9-full.nupkg", "release-manifest-tests.log",')
        text = text.replace("len(manifest) == 18", "len(manifest) == 19")
        text = text.replace('for item in manifest}) == 18', 'for item in manifest}) == 19')
        text = text.replace('verified["PublicAssets"] == 19', 'verified["PublicAssets"] == 20')
        marker = 'assert sha(DESTINATION / "Iris-0.2.7-full.nupkg") == "f8f9de897b637f8c98a07d601420885bf65529701d014fc52b59f87ce75bace8"'
        assert marker in text
        text = text.replace(marker, marker + '\nassert sha(DESTINATION / "Iris-0.2.9-full.nupkg") == "42c32435e5883608c77f097befd2b9a7f91845b35942a09966018361c40a779e"')
        assert '"Iris-0.2.9-full.nupkg"' in text and 'len(manifest) == 19' in text
    compile(text, after_name, "exec")
    (ROOT / after_name).open("x", encoding="utf-8", newline="\n").write(text)
print("Prepared 0.2.10 original CI and 20-asset public readback tools; not executed.")
