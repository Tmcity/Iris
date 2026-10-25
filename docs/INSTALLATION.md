# Iris for Windows

The official release location is https://github.com/Tmcity/Iris/releases. Use the versioned `Iris-<version>-Setup.exe` installer. An internal Velopack Setup or Portable ZIP is not the Iris installation entry.

Iris requires Windows x64 and the .NET 10 Windows Desktop Runtime. The outer installation entry includes its own runtime so it can start before .NET 10 is installed. Setup downloads the Microsoft desktop runtime when missing; that prerequisite installation may require administrator approval. Iris itself is installed for the current user. No shared runtime is removed during uninstall.

Current packages are unsigned. A GitHub deployment key is not a Windows code-signing certificate. Check the release's SHA256 manifest before running an installer; the Windows reputation warning is not evidence of a signed release.

The installer offers confirmation before changing the installation. `--silent` performs an unattended install without starting Iris. `--no-start` prevents automatic startup while retaining the normal installer confirmation. Do not close or remove an in-progress maintenance worker. If setup cannot finish, run the same or a newer official installer again to repair its retained transaction.

The General page shows the running version, startup-check preference and update status. Checking does not download; downloading does not install. Choose **Install and restart** to authorize a verified update. Unsaved settings are not saved by that action. A failed check, missing compatible release and being up to date are different results. Development and portable runs do not automatically update.

Uninstall through Windows Installed apps. Before uninstalling, Iris asks whether to keep settings/history/logs (default), delete them after successful removal, or cancel. Silent uninstall keeps data and does not show a prompt. Data normally lives in `%APPDATA%\Iris`; a portable `data` folder is not removed by this installer. Data left after uninstall can be used on reinstall.

If requested data cleanup fails, retry in the maintenance window. The recovery copy under `%LOCALAPPDATA%\Iris.Maintenance\recovery\Iris.Maintenance.exe` remains available for the retained cleanup transaction; it does not reinstall or rerun a completed uninstall. Do not manually remove transaction records to bypass an unknown installation state.

## Build and distribution

Run Windows PowerShell 5.1 or later:

```powershell
./build/pack.ps1 -Version 0.2.0 -OutputDirectory F:/Iris/artifacts/my-empty-release-directory
```

The script requires the .NET 10 SDK, pins Velopack/vpk to 1.2.161 in a task-specific tool directory, publishes App and UiaHost as framework-dependent, and publishes Maintenance as a self-contained single file. It refuses to overwrite a nonempty release directory. Keep the generated file lists and SHA256 manifest with the release.

The Desktop release workflow accepts only reviewed numeric versions from `build/release-plan.json`. A manual workflow run builds artifacts only. A pushed version tag builds, tests and verifies assets, then uses a separate job with `contents: write` to create a draft Release, upload the exact files, download and verify them, and finally make the Release public. An existing Release is never overwritten. Publishing instructions and the architect review checkpoint are in [RELEASING.md](RELEASING.md).

Each Release includes the actual full package and feed, the versioned outer Setup, `SHA256.json`, payload/staging inventories, `release-provenance.json`, this guide and release notes. The provenance names the source commit and an explicitly scoped source tree, SDK/vpk/PowerShell environment, payload and outer installer hashes. Validated releases also contain the original build/test/package logs and their summary. The latest release carries earlier full packages referenced by its feed; a generated delta is not evidence that delta application was tested. Do not run the `.nupkg` or any internal Setup file as an installation entry.

Client updates are anonymous and contain no repository credentials. T-039C separately verifies public downloads and the real settings UI upgrade; a successful workflow or an asset hash alone is not proof of that user path. The B validation used an official runtime download cache in its owned guest after the vendor's default complete download reported `Peer disconnected`. That cache-assisted result does not prove default prerequisite downloading. C must verify the public installer in a clean guest with no shared .NET 10, no manually filled runtime cache and no manual runtime installation. A failure remains a failure and must be investigated; do not disable certificate/TLS validation or delete maintenance state to bypass it.

The metadata's Windows API floor is 10.0.19041; Microsoft OS support also depends on Windows edition and lifecycle. The signed work report records the actual Windows build and runtime tested. A runtime's ability to start on a system is not a claim of Microsoft support for an expired OS release.
