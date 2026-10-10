# Registers the service from a publish folder without building the installer. Run from an elevated prompt.
#   .\scripts\publish.ps1 -NoInstaller
#   .\scripts\install-dev.ps1            # install or update
#   .\scripts\install-dev.ps1 -Remove    # stop and unregister
param([switch]$Remove, [string]$PublishDir = (Join-Path (Split-Path $PSScriptRoot) 'artifacts\publish\win-x64'))

$ErrorActionPreference = 'Stop'
$identity = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this from an elevated (administrator) PowerShell.'
}

$name = 'NetLedger'
$existing = Get-Service $name -ErrorAction SilentlyContinue
if ($existing) {
    Stop-Service $name -ErrorAction SilentlyContinue
    if ($Remove) {
        sc.exe delete $name | Out-Null
        logman.exe stop NetLedger-KernelNetwork -ets 2>$null | Out-Null
        Write-Host 'Service removed. Recorded data is kept in' (Join-Path $env:ProgramData 'NetLedger')
        return
    }
}
elseif ($Remove) {
    Write-Host 'Service not installed.'
    return
}

$exe = Join-Path (Resolve-Path $PublishDir) 'NetLedger.Service.exe'
if (-not (Test-Path $exe)) { throw "No published service at $exe; run scripts\publish.ps1 -NoInstaller first." }

# The image path keeps its own quotes; an unquoted path with spaces lets Windows try C:\Program.exe first.
$imagePath = "`"$exe`""
if ($existing) {
    Set-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\$name" -Name ImagePath -Value $imagePath
}
else {
    New-Service -Name $name -BinaryPathName $imagePath -DisplayName 'NetLedger' -StartupType Automatic `
        -Description 'Records network usage per hour, adapter and app for NetLedger.' | Out-Null
    sc.exe failure $name reset= 86400 actions= restart/5000/restart/10000/restart/60000 | Out-Null
}

Start-Service $name
Get-Service $name | Format-Table Name, Status, StartType
