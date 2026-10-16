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

The Desktop release workflow builds and uploads artifacts by default. Publishing requires the explicit workflow input and the `desktop-release` environment. Client updates are anonymous and contain no repository credentials. T-039B prepares these materials; actual public distribution and a production anonymous online upgrade remain the T-039C verification step. Do not equate local build/test results with that online validation.

The metadata's Windows API floor is 10.0.19041; Microsoft OS support also depends on Windows edition and lifecycle. The signed work report records the actual Windows build and runtime tested. A runtime's ability to start on a system is not a claim of Microsoft support for an expired OS release.
