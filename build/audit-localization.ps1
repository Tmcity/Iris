param([string]$EvidenceDirectory)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
if (-not $EvidenceDirectory) { $EvidenceDirectory = Join-Path $workspace 'artifacts\localization-audit' }
New-Item -ItemType Directory -Path $EvidenceDirectory -Force | Out-Null
$inventory = Join-Path $EvidenceDirectory 'source-inventory.json'
$result = Join-Path $EvidenceDirectory 'audit-result.json'
dotnet run --project (Join-Path $PSScriptRoot 'LocalizationAudit\LocalizationAudit.csproj') -c Release -- $workspace $inventory --compiled
if ($LASTEXITCODE -ne 0) { throw 'Compiled resource or syntax audit failed.' }
python (Join-Path $PSScriptRoot 'localization\audit.py') --root $workspace --inventory $inventory --output $result
if ($LASTEXITCODE -ne 0) { throw 'Unreviewed source text or resource mismatch.' }
