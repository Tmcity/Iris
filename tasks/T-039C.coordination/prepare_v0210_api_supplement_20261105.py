"""Prepare exact official API bindings for the newly published 0.2.10."""
from pathlib import Path

ROOT = Path("F:/Iris/tasks/T-039C.coordination")
text = (ROOT / "supplement_v029_official_api_20261105.py").read_text(encoding="utf-8-sig")
text = text.replace("0.2.9", "0.2.10").replace("v029", "v0210").replace("42028799541", "42042550153")
text = text.replace("0b3267f1f6cdc1a9fb8cfe1c93dde22da273ecd2", "227b3d88938543766a27d86a94ade52f9e5b89b7")
text = text.replace("public-0.2.10-download-20261105-1", "public-0.2.10-download-20261105-2")
text = text.replace("127249641900", "127296844225").replace("127253344007", "127301558754")
text = text.replace("13414499847", "13423677433").replace("13415502207", "13423731984")
text = text.replace("ecc5a3ebf25404603d84c9aa729c74c35602664db122779ce6da095cbe641888", "237f8958d4e8ddbf6ae28bddbf38c5c63598673a57d2413dbc6017105ae48e36")
text = text.replace("9b194a0b291c07961af420ca0bef219fddbb7899ef2a968c1e4c71df509b2c0a", "d08fde86b418f15fafbe090e0d62e504350e2f1520fdfcffbbab61ee410ec55d")
text = text.replace('len(release["assets"]) == 19', 'len(release["assets"]) == 20').replace("All19", "All20")
compile(text, "supplement_v0210_official_api_20261105.py", "exec")
(ROOT / "supplement_v0210_official_api_20261105.py").open("x", encoding="utf-8", newline="\n").write(text)
print("Prepared exact 20-asset and original CI API supplement.")
