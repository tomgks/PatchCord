# Removes the PatchCord autostart entry and stops the running app.
# Run:  powershell -ExecutionPolicy Bypass -File uninstall.ps1
# Note: this does NOT unpatch Discord; your client mod stays installed.

$ErrorActionPreference = 'SilentlyContinue'
$StartupDir = [Environment]::GetFolderPath('Startup')

cmd.exe /c "schtasks /Delete /TN PatchCord /F >nul 2>nul"
Remove-Item (Join-Path $StartupDir 'PatchCord.lnk') -Force
Remove-Item (Join-Path $StartupDir 'PatchCord.vbs') -Force

Get-Process -Name PatchCord -ErrorAction SilentlyContinue | Stop-Process -Force

Write-Host "Removed autostart and stopped the app." -ForegroundColor Green
Write-Host "Discord stays modded. To remove a client mod, use that mod's installer (or pick 'No client mod' in PatchCord)."
