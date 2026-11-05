"""Prepare exact 0.2.10 official website/CI review from its actual successful downloads."""
from pathlib import Path

ROOT = Path("F:/Iris/tasks/T-039C.coordination")
text = (ROOT / "review_v029_official_web_and_ci_20261105.py").read_text(encoding="utf-8-sig")
text = text.replace("0.2.9", "0.2.10").replace("v029", "v0210").replace("42028799541", "42042550153")
text = text.replace("0b3267f1f6cdc1a9fb8cfe1c93dde22da273ecd2", "227b3d88938543766a27d86a94ade52f9e5b89b7")
text = text.replace("fcb432c3df3b5930bbc2900f43235384130b97ae", "69d4faf228d9d0b98d77ecb4a8dac582ad33421b")
replacements = {
    "public-0.2.10-download-20261105-1": "public-0.2.10-download-20261105-2",
    "public-download-20261105-1/review.json": "public-download-20261105-2/review.json",
    "20261105T124757627859Z-web-assets": "20261105T142255665078Z-web-assets",
    r"/v0\.2\.9/": r"/v0\.2\.10/",
    "len(assets) == 19": "len(assets) == 20",
    'public_review["PublicAssets"] == 19': 'public_review["PublicAssets"] == 20',
    "All19": "All20",
    "All19Assets": "All20Assets",
    "42c32435e5883608c77f097befd2b9a7f91845b35942a09966018361c40a779e": "dca307a0afc1827e27bc7ec2614e5ead3a30978d4c5d318e4d0d842ebc73f136",
    "69ab96bb1f700b21fa430d69eab85d6d4a5c606c377424222f0f97af0e7f2891": "56166ec3e71a0fad72b1eb135e119635e63720ebe0a8fcf301f58d7ef8e053c7",
    "ecc5a3ebf25404603d84c9aa729c74c35602664db122779ce6da095cbe641888": "237f8958d4e8ddbf6ae28bddbf38c5c63598673a57d2413dbc6017105ae48e36",
    "9b194a0b291c07961af420ca0bef219fddbb7899ef2a968c1e4c71df509b2c0a": "d08fde86b418f15fafbe090e0d62e504350e2f1520fdfcffbbab61ee410ec55d",
    '"Passed": 3770, "Failed": 0, "Skipped": 1': '"Passed": 3771, "Failed": 0, "Skipped": 0',
    'assert len(skipped) == 1 and "error=183" in skipped[0]["Reason"]': 'assert not skipped',
    "PackageJobId=127249641900, PublishJobId=127253344007": "PackageJobId=127296844225, PublishJobId=127301558754",
    "PublicAssets=19": "PublicAssets=20",
}
for before, after in replacements.items():
    if before in text:
        text = text.replace(before, after)
assert "len(assets) == 20" in text and '"Passed": 3771' in text
compile(text, "review_v0210_official_web_and_ci_20261105.py", "exec")
(ROOT / "review_v0210_official_web_and_ci_20261105.py").open("x", encoding="utf-8", newline="\n").write(text)
print("Prepared exact 0.2.10 official review; no release acceptance performed yet.")
