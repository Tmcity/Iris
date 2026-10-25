import os
from pathlib import Path
import sys
import json
import shutil
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import publish_release

class PublishingAdmissionTests(unittest.TestCase):
    def test_local_process_cannot_publish(self):
        with patch.dict(os.environ, {}, clear=True), patch.object(publish_release.subprocess, "run") as invoke:
            with self.assertRaisesRegex(ValueError, "restricted"):
                publish_release.publish(Path("unused"), "0.2.0", Path("unused"))
            invoke.assert_not_called()
    def test_manual_workflow_cannot_publish(self):
        with patch.dict(os.environ, {"GITHUB_ACTIONS": "true", "GITHUB_EVENT_NAME": "workflow_dispatch"}, clear=True), patch.object(publish_release.subprocess, "run") as invoke:
            with self.assertRaisesRegex(ValueError, "restricted"):
                publish_release.publish(Path("unused"), "0.2.0", Path("unused"))
            invoke.assert_not_called()

    def test_wrong_repository_cannot_publish(self):
        with patch.dict(os.environ, {"GITHUB_ACTIONS": "true", "GITHUB_EVENT_NAME": "push", "GITHUB_REPOSITORY": "other/repository", "GITHUB_REF": "refs/tags/v0.2.0"}, clear=True), patch.object(publish_release.subprocess, "run") as invoke:
            with self.assertRaisesRegex(ValueError, "Unexpected repository"):
                publish_release.publish(Path("unused"), "0.2.0", Path("unused"))
            invoke.assert_not_called()

class PublicationReceiptTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="iris-publication-fault-")
        self.addCleanup(self.temporary.cleanup)
        self.directory = Path(self.temporary.name) / "assets"
        self.directory.mkdir()
        (self.directory / "release-provenance.json").write_text(json.dumps({"Validation": {}}))
        (self.directory / "SHA256.json").write_text("[]")
        self.receipt = Path(self.temporary.name) / "receipt.json"
        self.expected = {"SourceCommit": "a" * 40, "SourceGitTree": "b" * 40}
        self.environment = {"GITHUB_ACTIONS": "true", "GITHUB_EVENT_NAME": "push", "GITHUB_REPOSITORY": "Tmcity/Iris", "GITHUB_REF": "refs/tags/v0.2.0", "GITHUB_SHA": "a" * 40,
            "GH_TOKEN": "owned-test-placeholder-not-a-credential", "GITHUB_RUN_ID": "123"}
        self.calls = []
        self.fault = None

    def cli(self, *arguments):
        self.calls.append(arguments)
        if arguments[:2] == ("release", "download"):
            target = Path(arguments[arguments.index("--dir") + 1])
            for path in self.directory.iterdir():
                shutil.copyfile(path, target / path.name)
        if arguments[:2] == ("release", "edit"):
            persisted = json.loads(self.receipt.read_text())
            self.assertTrue(persisted["PublicationRequestAttempted"])
            self.assertIsNone(persisted["Public"])
            if self.fault == "edit-ambiguous":
                raise ValueError("owned simulated CLI response failure")
        if arguments[:2] == ("release", "view"):
            persisted = json.loads(self.receipt.read_text())
            self.assertTrue(persisted["PublicationCliConfirmed"])
            self.assertIsNone(persisted["Public"])
            if self.fault == "view-failed":
                raise ValueError("owned simulated visibility failure after successful edit")
            return json.dumps({"url": "https://github.com/Tmcity/Iris/releases/tag/v0.2.0", "isDraft": False,
                "assets": [{"name": path.name} for path in self.directory.iterdir()]}).encode()
        return b""

    def invoke(self, verify=None):
        with patch.dict(os.environ, self.environment, clear=True), patch.object(publish_release, "gh", side_effect=self.cli), patch.object(publish_release, "verify_directory", side_effect=verify or [self.expected, self.expected]):
            publish_release.publish(self.directory, "0.2.0", self.receipt)

    def test_draft_readback_failure_never_requests_publication(self):
        with self.assertRaisesRegex(ValueError, "owned readback mismatch"):
            self.invoke([self.expected, ValueError("owned readback mismatch")])
        self.assertFalse(any(call[:2] == ("release", "edit") for call in self.calls))
        self.assertFalse(json.loads(self.receipt.read_text())["PublicationRequestAttempted"])

    def test_edit_success_and_view_failure_retains_confirmed_request_unknown_visibility(self):
        self.fault = "view-failed"
        with self.assertRaisesRegex(ValueError, "visibility failure"):
            self.invoke()
        state = json.loads(self.receipt.read_text())
        self.assertTrue(state["PublicationRequestAttempted"])
        self.assertTrue(state["PublicationCliConfirmed"])
        self.assertIsNone(state["Public"])
        self.assertEqual("publication-cli-confirmed-visibility-unverified", state["Stage"])

    def test_ambiguous_edit_response_keeps_requested_outcome_unknown(self):
        self.fault = "edit-ambiguous"
        with self.assertRaisesRegex(ValueError, "response failure"):
            self.invoke()
        state = json.loads(self.receipt.read_text())
        self.assertTrue(state["PublicationRequestAttempted"])
        self.assertFalse(state["PublicationCliConfirmed"])
        self.assertIsNone(state["Public"])
        self.assertFalse(any(call[:2] == ("release", "view") for call in self.calls))

    def test_public_is_true_only_after_actual_visibility_response(self):
        self.invoke()
        self.assertTrue(json.loads(self.receipt.read_text())["Public"])
