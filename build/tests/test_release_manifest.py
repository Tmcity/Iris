import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location("release_manifest", Path(__file__).resolve().parents[1] / "release_manifest.py")
release = importlib.util.module_from_spec(spec)
spec.loader.exec_module(release)

class PublicAssetTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="iris-release-assets-")
        self.addCleanup(self.temporary.cleanup)
        self.directory = Path(self.temporary.name)
        self.name = "Iris-0.2.0-full.nupkg"
        self.body = b"synthetic owned package bytes; never executed"
        (self.directory / self.name).write_bytes(self.body)
        (self.directory / "Iris-0.2.0-Setup.exe").write_bytes(b"synthetic outer entry; never executed")
        self.entry = {"PackageId": "Iris", "Version": "0.2.0", "Type": "Full", "FileName": self.name,
            "Size": len(self.body), "SHA256": hashlib.sha256(self.body).hexdigest(), "SHA1": hashlib.sha1(self.body).hexdigest()}
        self.write("releases.win.json", {"Assets": [self.entry]})
        self.write("assets.win.json", [{"RelativeFileName": self.name, "Type": "Full"}, {"RelativeFileName": "Iris-0.2.0-Setup.exe", "Type": "Installer"}])
        (self.directory / "RELEASES").write_text(f'{self.entry["SHA1"]} {self.name} {len(self.body)}\n')

    def write(self, name, value):
        (self.directory / name).write_text(json.dumps(value), encoding="utf-8")

    def test_public_index_uses_outer_installer_and_matches_actual_package(self):
        self.assertEqual(1, len(release.verify_indexes(self.directory, "0.2.0")))

    def test_original_internal_setup_reference_is_rejected(self):
        self.write("assets.win.json", [{"RelativeFileName": "Iris-win-Setup.exe", "Type": "Installer"}])
        with self.assertRaisesRegex(ValueError, "missing public file"):
            release.verify_indexes(self.directory, "0.2.0")

    def test_sdk_index_is_normalized_to_existing_outer_entry(self):
        self.write("assets.win.json", [{"RelativeFileName": self.name, "Type": "Full"}, {"RelativeFileName": "Iris-win-Setup.exe", "Type": "Installer"}])
        result = release.normalize_assets(self.directory, "0.2.0")
        self.assertEqual("Iris-0.2.0-Setup.exe", result["IndexedOuterInstaller"])
        self.assertEqual(1, len(release.verify_indexes(self.directory, "0.2.0")))

    def test_internal_setup_is_rejected_even_if_accidentally_present(self):
        (self.directory / "Iris-win-Setup.exe").write_bytes(b"wrong entry")
        self.write("assets.win.json", [{"RelativeFileName": "Iris-win-Setup.exe", "Type": "Installer"}])
        with self.assertRaisesRegex(ValueError, "versioned outer"):
            release.verify_indexes(self.directory, "0.2.0")

    def test_corrupted_full_package_is_rejected(self):
        (self.directory / self.name).write_bytes(self.body[:-1] + b"!")
        with self.assertRaisesRegex(ValueError, "Feed does not match"):
            release.verify_indexes(self.directory, "0.2.0")

    def test_unsafe_feed_name_is_rejected_before_opening_it(self):
        self.entry["FileName"] = "../outside.nupkg"
        self.write("releases.win.json", {"Assets": [self.entry]})
        with self.assertRaisesRegex(ValueError, "basename"):
            release.verify_indexes(self.directory, "0.2.0")

    def test_hash_manifest_requires_exact_asset_set(self):
        self.write("SHA256.json", [])
        with self.assertRaisesRegex(ValueError, "asset/hash set"):
            release.verify_hashes(self.directory)

    def test_json_duplicate_property_is_rejected(self):
        (self.directory / "duplicate.json").write_text('{"Assets":[],"Assets":[]}')
        with self.assertRaisesRegex(ValueError, "Duplicate JSON"):
            release.read_json(self.directory / "duplicate.json")

if __name__ == "__main__":
    unittest.main()
