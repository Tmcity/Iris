# Desktop release procedure

The public repository is `https://github.com/Tmcity/Iris`. PackageId `Iris`, channel `win`, RID `win-x64` and Velopack/vpk `1.2.161` are fixed. App and UiaHost remain framework-dependent; Maintenance and the outer installer remain self-contained. Windows x64 is the only release target. The current packages are unsigned.

## Review before remote changes

T-039C has a required architect checkpoint before the first push, asset upload, Release creation or workflow trigger. Local anonymous reads/fetch, normal history merges, builds and packaging are allowed before that checkpoint. Provide a concrete local review commit, the exact branch and tag targets, both version/asset manifests and the production verification plan. The architect coordinates this checkpoint; the already designated public repository does not require another user handoff.

The existing local history and initial public `main` history have no common ancestor. Merge them normally with both parents retained; keep the complete product README and archive the initial public README verbatim under `docs/history`. Never force-push, rewrite a tag, amend/rebase/reset/clean away history, or replace an existing Release. Use a `codex/` branch for the preparation work. Push the reviewed branch and fast-forward public `main` only after approval. If public refs changed since review, fetch and reconcile before any write.

## Source, assets and tags

`build/release-plan.json` initially contains `0.2.0` and `0.2.1`, with the latter depending on the former. Both tags may point to the same reviewed release-source commit because `build/pack.ps1 -Version` sets all product/package versions. Check public tags and Releases before choosing them; never reuse an occupied version. Evidence-only commits may follow the source commit; the provenance must name the actual source used for packaging, not a later report commit.

Create annotated local tags `v0.2.0` and `v0.2.1` at that exact source commit, but push them separately. After approval, publish `v0.2.0`, verify its completed CI/Release and anonymous asset bytes, and install it from the public Release in an owned clean Windows guest. Keep that guest's real old Iris alive with synthetic data. Only then push `v0.2.1` so the test has an actual publication boundary. Do not push both tags together and let jobs race.

The tag workflow records a Release build with zero warnings/errors, full xUnit results including actual skips, and offline publication/manifest regressions. It packages with Windows PowerShell 5.1 and downloads the previous public asset set before creating the newer full/delta/feed history. The build job has `contents: read`, checkout does not persist credentials, and only the separate publishing job has `contents: write`. That job uses the ephemeral workflow `GITHUB_TOKEN` through `GH_TOKEN`; the local SSH deployment key is only for Git and is never read, copied, sent to Release APIs or included in a product/guest. A manual dispatch has no publication path. No repository environment or PAT is required by this workflow.

The publisher creates a draft, uploads all validated public files without overwrite flags, downloads every draft asset into a new directory, checks path set/length/SHA/feed/payload/outer-envelope/source bindings, and only then publishes the draft. A failed draft remains for investigation. Do not delete a failed Release or rewrite its tag to make the attempt appear successful; if published bytes need a fix, use a new version and record the original failure. A workflow start or CLI exit alone is insufficient: record the final run/job status, public Release URL and actual read-back bytes. Draft read-back uses the workflow token; the later public client verification must use no token.

Run the same local pipeline from committed release sources:

```powershell
python -B build/release_pipeline.py validate --output F:/Iris/artifacts/t039c/validation-unique
python -B build/release_pipeline.py package --version 0.2.0 --output F:/Iris/artifacts/t039c/release-0.2.0-unique --logs F:/Iris/artifacts/t039c/package-0.2.0-unique --validation F:/Iris/artifacts/t039c/validation-unique
python -B build/release_pipeline.py package --version 0.2.1 --output F:/Iris/artifacts/t039c/release-0.2.1-unique --logs F:/Iris/artifacts/t039c/package-0.2.1-unique --validation F:/Iris/artifacts/t039c/validation-unique --previous F:/Iris/artifacts/t039c/release-0.2.0-unique
```

Directories must be new and unique. The local tool source option accepts the previously obtained pinned vpk package; its use is recorded separately. CI resolves the same version from NuGet. Local and CI packages are separate builds: compare each against its own provenance, and never claim their binary bytes match without checking.

Each exact public asset set contains the versioned outer `Iris-<version>-Setup.exe`, actual full package, feed/legacy index/asset index, inventories, source provenance, installation guide and release notes. Validated assets additionally contain original build, full-test, package and release-tool-test logs plus their summary. A newer feed also references the retained old full package and a newly generated delta. The verifier requires every referenced asset to exist with the exact length and SHA. `assets.win.json` must name the outer versioned Setup, never the removed internal `Iris-win-Setup.exe`. No portable ZIP, test worker, credential, dump or tool belongs in the public product payload. Public build logs contain paths/commands and validation results, not credential values; signed redirect queries are not logged.

## Production verification required after publication

Use only newly owned Windows Sandbox instances and task-specific input/output mappings. Download the old installer directly from the public Release without a token. Record actual clean OS/user/privilege state, an empty .NET 10 installation and empty runtime download cache. Invoke the unchanged public outer entry normally; preserve the real Setup/runtime chain, handles and exits. Do not prefill a runtime cache, install .NET by hand, change host networking or disable TLS/certificate validation for this default-bootstrap scenario. If it fails, retain the failure and send the architect an actionable diagnosis/fix proposal before changing prerequisite production behavior.

Once real old Iris runs, use owned guest-only synthetic settings, DPAPI secret, SQLite history and log data. Disable startup auto-check through the real saved preference so a manual check is observable. Publish the newer Release, then interact with the real production settings UI: Check, inspect the available state while confirming no package download, Download, inspect Ready while confirming no install/restart, then choose Install and restart and confirm. UI automation may invoke visible UI Automation patterns; it must not replace the update source/runtime, call internal coordinators or use GuestUpdateHost.

Retain current-language screenshots at each meaningful boundary, button labels/states, version, process IDs/start times/held handles/exit codes, package/feed hashes and the actual new process. Verify old Iris exits before native apply completes, exactly one new Iris starts, the installed version is new, the four synthetic data classes remain readable with before/after file hashes, the registered uninstall command points to Maintenance, transaction/work directories are cleared, and the updated application exits normally. UI screenshots only prove what is shown. Separate API/network failure, cancellation and no-compatible-release from being current. Respect anonymous GitHub rate limits; wait for reset in the same owned guest instead of injecting a token.

Archive workflow failures, unsuccessful native downloads and every candidate before corrections. A fallback/cache-assisted diagnostic is a separate condition and cannot satisfy the default-bootstrap requirement. Full-package success is not delta-apply success. Public distribution and complete real UI/default-runtime evidence must be reviewed before C or parent T-039 can close. T-040 and final NFR remain a later architect-issued task.
