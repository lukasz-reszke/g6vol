# Stops g6vol and removes the app and its logon entry.
$dir = Join-Path $env:LOCALAPPDATA 'Programs\g6vol'
$exe = Join-Path $dir 'g6vol.exe'
if (Test-Path $exe) { Start-Process $exe -ArgumentList '--stop' -Wait }
Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name g6vol -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force $dir -ErrorAction SilentlyContinue
Write-Host 'uninstalled'

# Running apps got their own volume back when g6vol stopped. Apps that weren't running keep
# the scaled volume Windows remembered for them.
$store = Join-Path $env:LOCALAPPDATA 'g6vol\sessions.tsv'
if (Test-Path $store) {
    $apps = Get-Content $store | ForEach-Object {
        if ($_ -match '([^\\|]+\.exe)') { $Matches[1] } else { 'System sounds' }
    } | Sort-Object -Unique
    if ($apps) {
        Write-Host "These apps may still play quieter on the G6: $($apps -join ', ')"
        Write-Host 'Fix: Settings > System > Sound > Volume mixer > Reset.'
    }
}
