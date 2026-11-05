"""Prepare independent anonymous downloads for the expected official 0.2.9 asset set."""
from pathlib import Path

ROOT = Path("F:/Iris/tasks/T-039C.coordination")
source = ROOT / "download_public_v027_20261105.py"
target = ROOT / "download_public_v029_20261105.py"
text = source.read_text(encoding="utf-8-sig").replace("0.2.7", "0.2.9")
text = text.replace("fc44a3a78c2729c25dfb91296c51602b843a94f9", "0b3267f1f6cdc1a9fb8cfe1c93dde22da273ecd2")
text = text.replace("81aea2fa6cad8a3dcde34b081185dd03aae9c31c", "fcb432c3df3b5930bbc2900f43235384130b97ae")
text = text.replace("41976966535", "42028799541")
text = text.replace("publication-v0.2.9-20261105-2", "publication-v0.2.9-20261105-1")
text = text.replace('"Iris-0.2.9-full.nupkg", "Iris-0.2.9-delta.nupkg", "Iris-0.2.2-full.nupkg",',
                    '"Iris-0.2.9-full.nupkg", "Iris-0.2.9-delta.nupkg", "Iris-0.2.2-full.nupkg", "Iris-0.2.7-full.nupkg",')
text = text.replace("len(manifest) == 17", "len(manifest) == 18")
text = text.replace('for item in manifest}) == 17', 'for item in manifest}) == 18')
text = text.replace('verified["PublicAssets"] == 18', 'verified["PublicAssets"] == 19')
text = text.replace('summary["FullTests"]["Total"] == 3759', 'summary["FullTests"]["Total"] == 3771')
marker = 'assert sha(DESTINATION / "Iris-0.2.2-full.nupkg") == "c86982f930aafcd142167bbd871d03b588630f7d1d625da968a811daa2dd5d14"'
assert marker in text
text = text.replace(marker, marker + '\nassert sha(DESTINATION / "Iris-0.2.7-full.nupkg") == "f8f9de897b637f8c98a07d601420885bf65529701d014fc52b59f87ce75bace8"')
assert "3759" not in text and "41976966535" not in text and "PublicAssets\"] == 18" not in text
target.open("x", encoding="utf-8", newline="\n").write(text)
print("Prepared all 19 anonymous official asset downloads; this script has not been executed.")
