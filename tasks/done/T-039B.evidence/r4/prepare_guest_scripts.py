from pathlib import Path

destination = Path(__file__).resolve().parent
legacy = destination.parent
install = (legacy / "guest_install.ps1").read_bytes().decode("utf-8-sig")
install = install.replace("    Add-Type -AssemblyName System.Drawing", "    Start-Sleep -Milliseconds 600\n    Add-Type -AssemblyName System.Drawing", 1)
update = (legacy / "guest_update.ps1").read_bytes().decode("utf-8-sig")
update = update.replace("try {\n    Require 'install-before-update'", """try {
    $os=Get-CimInstance Win32_OperatingSystem
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
    $principal=New-Object Security.Principal.WindowsPrincipal($identity)
    Record 'environment' @{OS=$os.Caption;Version=$os.Version;User=$identity.Name;Administrator=$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator);RuntimesBefore=@(Runtimes)}
    Require 'clean-no-shared-dotnet10' (@(Runtimes | Where-Object {$_.Version -like '10.*'}).Count -eq 0)
    Require 'install-before-update'""")
update = update.replace("    Add-Type -AssemblyName System.Drawing", "    Start-Sleep -Milliseconds 600\n    Add-Type -AssemblyName System.Drawing", 1)
update = update.replace("($workDirs.Count -le 1)", "($workDirs.Count -eq 0)")
for name, contents in [("guest_install_r4.ps1", install), ("guest_update_r4.ps1", update)]:
    path = destination / name
    if path.exists():
        raise FileExistsError(path)
    path.write_text(contents, encoding="utf-8-sig")
