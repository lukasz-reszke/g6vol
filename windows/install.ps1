# Installs g6vol.exe into %LOCALAPPDATA%\Programs\g6vol and starts it at logon.
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
if (-not (Test-Path build\g6vol.exe)) { & .\build.ps1 }
$dir = Join-Path $env:LOCALAPPDATA 'Programs\g6vol'
$exe = Join-Path $dir 'g6vol.exe'
# Stop a running instance (it hands every app its own volume back) so the exe can be replaced.
Start-Process build\g6vol.exe -ArgumentList '--stop' -Wait
New-Item -ItemType Directory -Force $dir | Out-Null
Copy-Item build\g6vol.exe $exe -Force
Set-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name g6vol -Value "`"$exe`""
Start-Process $exe
Write-Host "installed; log: $env:LOCALAPPDATA\g6vol\g6vol.log"
