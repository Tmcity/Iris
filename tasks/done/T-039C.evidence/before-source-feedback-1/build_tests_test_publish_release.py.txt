import os
from pathlib import Path
import sys
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
