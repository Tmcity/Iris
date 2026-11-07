$ErrorActionPreference='Stop'
& (Join-Path $PSScriptRoot 't040_capture_live_capi2_readonly.ps1') -DurationSeconds 15 -ValidateWithExistingMsi
