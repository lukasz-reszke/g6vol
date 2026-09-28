# Builds build\g6vol.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
New-Item -ItemType Directory -Force build | Out-Null
# The compiler's note about only supporting C# 5 is expected; everything else is shown.
& $csc -nologo -optimize+ -codepage:65001 -target:winexe -out:build\g6vol.exe g6vol.cs |
    Where-Object { $_ -notmatch 'only supports language versions up to C# 5|go\.microsoft\.com/fwlink/\?LinkID=533240|^\s*$' }
if ($LASTEXITCODE) { throw 'build failed' }
Write-Host 'built build\g6vol.exe'
