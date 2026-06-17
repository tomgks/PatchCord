# Installs the PatchCord so it runs in the system tray at every logon.
# Uses the per-user Startup folder, so NO admin rights are needed.
# Run:  powershell -ExecutionPolicy Bypass -File install.ps1

$ErrorActionPreference = 'Stop'
$ScriptDir  = Split-Path -Parent $MyInvocation.MyCommand.Definition
$Exe        = Join-Path $ScriptDir 'PatchCord.exe'
$StartupDir = [Environment]::GetFolderPath('Startup')
$StartupLnk = Join-Path $StartupDir 'PatchCord.lnk'
$StartupVbs = Join-Path $StartupDir 'PatchCord.vbs'

# Clean up any previous autostart entries / processes.
cmd.exe /c "schtasks /Delete /TN PatchCord /F >nul 2>nul"
Remove-Item $StartupVbs -ErrorAction SilentlyContinue
Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" |
    Where-Object { $_.ProcessId -ne $PID -and $_.CommandLine -like '*watcher.ps1*' } |
    ForEach-Object { try { Stop-Process -Id $_.ProcessId -Force } catch { } }

if (Test-Path $Exe) {
    # Startup shortcut to the exe, launched minimized to the tray.
    $ws = New-Object -ComObject WScript.Shell
    $lnk = $ws.CreateShortcut($StartupLnk)
    $lnk.TargetPath = $Exe
    $lnk.Arguments = '-Tray'
    $lnk.WorkingDirectory = $ScriptDir
    $lnk.IconLocation = "$Exe,0"
    $lnk.Description = 'PatchCord'
    $lnk.Save()
    Write-Host "Installed startup shortcut: $StartupLnk" -ForegroundColor Green
    $already = (Get-Process -Name PatchCord -ErrorAction SilentlyContinue)
    if (-not $already) { Start-Process -FilePath $Exe -ArgumentList '-Tray' -WorkingDirectory $ScriptDir }
}
else { throw "PatchCord.exe not found in $ScriptDir." }

Write-Host "Done. It will start automatically at every logon (in the tray)." -ForegroundColor Green
Write-Host "To remove it, run: powershell -ExecutionPolicy Bypass -File uninstall.ps1"
