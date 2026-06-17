# Removes the Vencord Auto-Updater autostart entry and stops the running app.
# Run:  powershell -ExecutionPolicy Bypass -File uninstall.ps1
# Note: this does NOT unpatch Discord; Vencord stays installed.

$ErrorActionPreference = 'SilentlyContinue'
$StartupDir = [Environment]::GetFolderPath('Startup')

cmd.exe /c "schtasks /Delete /TN VencordAutoUpdater /F >nul 2>nul"
Remove-Item (Join-Path $StartupDir 'VencordAutoUpdater.lnk') -Force
Remove-Item (Join-Path $StartupDir 'VencordAutoUpdater.vbs') -Force

# Stop the exe and any script/watcher instances.
Get-Process -Name VencordAutoUpdater -ErrorAction SilentlyContinue | Stop-Process -Force
Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" |
    Where-Object { $_.ProcessId -ne $PID -and ($_.CommandLine -like '*VencordAutoUpdaterApp.ps1*' -or $_.CommandLine -like '*watcher.ps1*') } |
    ForEach-Object { try { Stop-Process -Id $_.ProcessId -Force } catch { } }

Write-Host "Removed autostart and stopped the app." -ForegroundColor Green
Write-Host "Discord remains patched with Vencord. To unpatch, use the Vencord installer's Uninstall option."
