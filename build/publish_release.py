"""Publish only a verified tag build, using the workflow token. Never overwrite a Release or asset."""
from pathlib import Path
import argparse
import json
import os
import subprocess
import tempfile
from release_manifest import read_json, require, verify_directory

def gh(*arguments):
    result = subprocess.run(["gh", *arguments], stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    # Keep credentials and signed redirect URLs out of logs, including CLI error messages.
    require(result.returncode == 0, "GitHub CLI operation failed (exit " + str(result.returncode) + "); no overwrite or cleanup was attempted")
    return result.stdout

def publish(directory, version, receipt):
    require(os.environ.get("GITHUB_ACTIONS") == "true" and os.environ.get("GITHUB_EVENT_NAME") == "push", "Publishing is restricted to the reviewed tag-push workflow")
    require(os.environ.get("GITHUB_REPOSITORY") == "Tmcity/Iris" and os.environ.get("GITHUB_REF") == "refs/tags/v" + version, "Unexpected repository or tag")
    require(bool(os.environ.get("GH_TOKEN")), "Workflow token is unavailable")
    directory = Path(directory)
    expected = verify_directory(directory, version)
    require("Validation" in read_json(directory / "release-provenance.json"), "Publishing requires actual build and full-test evidence")
    require(expected["SourceCommit"] == os.environ.get("GITHUB_SHA"), "Tag and packaged source commit differ")
    tag = "v" + version
    state = {"Tag": tag, "SourceCommit": expected["SourceCommit"], "SourceGitTree": expected["SourceGitTree"],
        "Stage": "verified-local-assets", "Public": False, "ReleaseUrl": None,
        "RunUrl": "https://github.com/Tmcity/Iris/actions/runs/" + os.environ["GITHUB_RUN_ID"]}
    try:
        gh("release", "create", tag, "--repo", "Tmcity/Iris", "--verify-tag", "--draft", "--target", expected["SourceCommit"],
            "--title", "Iris " + version, "--notes-file", str(directory / "RELEASE_NOTES.md"))
        state["Stage"] = "draft-created"
        gh("release", "upload", tag, "--repo", "Tmcity/Iris", *[str(path) for path in sorted(directory.iterdir())])
        state["Stage"] = "draft-assets-uploaded"
        with tempfile.TemporaryDirectory(prefix="iris-draft-readback-") as temporary:
            gh("release", "download", tag, "--repo", "Tmcity/Iris", "--dir", temporary)
            require(verify_directory(Path(temporary), version) == expected, "Draft readback differs from packaged assets")
            require(read_json(Path(temporary) / "SHA256.json") == read_json(directory / "SHA256.json"), "Draft hash manifest differs")
        state["Stage"] = "draft-readback-verified"
        gh("release", "edit", tag, "--repo", "Tmcity/Iris", "--draft=false", "--latest")
        actual = json.loads(gh("release", "view", tag, "--repo", "Tmcity/Iris", "--json", "url,isDraft,assets"))
        require(not actual["isDraft"] and {item["name"] for item in actual["assets"]} == {path.name for path in directory.iterdir()}, "Published asset set differs")
        state.update(Stage="published-and-listed", Public=True, ReleaseUrl=actual["url"])
        print(json.dumps(state, indent=2))
    finally:
        Path(receipt).write_text(json.dumps(state, indent=2) + "\n", encoding="utf-8", newline="\n")

if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--directory", type=Path, required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--receipt", type=Path, required=True)
    args = parser.parse_args()
    publish(args.directory, args.version, args.receipt)
