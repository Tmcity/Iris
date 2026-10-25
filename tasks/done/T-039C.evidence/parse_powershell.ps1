$ErrorActionPreference='Stop'
$tokens=$null; $parseErrors=$null
$null=[System.Management.Automation.Language.Parser]::ParseFile((Join-Path (Get-Location) 'build\pack.ps1'),[ref]$tokens,[ref]$parseErrors)
if ($parseErrors.Count -ne 0) { $parseErrors | Format-List; throw 'Packaging script syntax failed.' }
[pscustomobject]@{PowerShell=$PSVersionTable.PSVersion.ToString();Edition=$PSVersionTable.PSEdition;Script='build/pack.ps1';SyntaxErrors=$parseErrors.Count} | ConvertTo-Json
