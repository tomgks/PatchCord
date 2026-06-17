# Installs the Vencord Auto-Updater so it runs in the system tray at every logon.
# Uses the per-user Startup folder, so NO admin rights are needed.
# Run:  powershell -ExecutionPolicy Bypass -File install.ps1

$ErrorActionPreference = 'Stop'
$ScriptDir  = Split-Path -Parent $MyInvocation.MyCommand.Definition
$Exe        = Join-Path $ScriptDir 'VencordAutoUpdater.exe'
$App        = Join-Path $ScriptDir 'VencordAutoUpdaterApp.ps1'
$Launcher   = Join-Path $ScriptDir 'launcher.vbs'
$StartupDir = [Environment]::GetFolderPath('Startup')
$StartupLnk = Join-Path $StartupDir 'VencordAutoUpdater.lnk'
$StartupVbs = Join-Path $StartupDir 'VencordAutoUpdater.vbs'

# Clean up any previous autostart entries / processes.
cmd.exe /c "schtasks /Delete /TN VencordAutoUpdater /F >nul 2>nul"
Remove-Item $StartupVbs -ErrorAction SilentlyContinue
Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" |
    Where-Object { $_.ProcessId -ne $PID -and $_.CommandLine -like '*watcher.ps1*' } |
    ForEach-Object { try { Stop-Process -Id $_.ProcessId -Force } catch { } }

if (Test-Path $Exe) {
    # Preferred: a Startup shortcut to the compiled exe, launched minimized to tray.
    $ws = New-Object -ComObject WScript.Shell
    $lnk = $ws.CreateShortcut($StartupLnk)
    $lnk.TargetPath = $Exe
    $lnk.Arguments = '-Tray'
    $lnk.WorkingDirectory = $ScriptDir
    $lnk.IconLocation = "$Exe,0"
    $lnk.Description = 'Vencord Auto-Updater'
    $lnk.Save()
    Write-Host "Installed startup shortcut: $StartupLnk" -ForegroundColor Green
    $already = (Get-Process -Name VencordAutoUpdater -ErrorAction SilentlyContinue)
    if (-not $already) { Start-Process -FilePath $Exe -ArgumentList '-Tray' -WorkingDirectory $ScriptDir }
}
elseif (Test-Path $App) {
    # Fallback: run the .ps1 hidden via a VBScript shim (needs -STA for WPF).
    Remove-Item $StartupLnk -ErrorAction SilentlyContinue
    $vbs = @"
CreateObject("Wscript.Shell").Run "powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -WindowStyle Hidden -File ""$App"" -Tray", 0, False
"@
    Set-Content -Path $Launcher   -Value $vbs -Encoding ASCII
    Set-Content -Path $StartupVbs -Value $vbs -Encoding ASCII
    Write-Host "Installed startup entry: $StartupVbs" -ForegroundColor Green
    $already = Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" | Where-Object { $_.ProcessId -ne $PID -and $_.CommandLine -like '*-File*VencordAutoUpdaterApp.ps1*' }
    if (-not $already) { Start-Process -FilePath 'wscript.exe' -ArgumentList "`"$Launcher`"" }
}
else { throw "Neither VencordAutoUpdater.exe nor VencordAutoUpdaterApp.ps1 found." }

Write-Host "Done. It will start automatically at every logon (in the tray)." -ForegroundColor Green
Write-Host "To remove it, run: powershell -ExecutionPolicy Bypass -File uninstall.ps1"
