@echo off
setlocal
set "installDir=%LOCALAPPDATA%\CodexUsageTray"
taskkill /IM CodexUsageTray.exe /F >nul 2>&1
if not exist "%installDir%" mkdir "%installDir%"
xcopy "%~dp0*" "%installDir%\" /E /I /Y >nul
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'; New-Item -ItemType Directory -Path $startMenu -Force | Out-Null; $link = Join-Path $startMenu 'Codex Usage Tray.lnk'; $shell = New-Object -ComObject WScript.Shell; $shortcut = $shell.CreateShortcut($link); $shortcut.TargetPath = Join-Path $env:LOCALAPPDATA 'CodexUsageTray\CodexUsageTray.exe'; $shortcut.WorkingDirectory = Join-Path $env:LOCALAPPDATA 'CodexUsageTray'; $shortcut.IconLocation = $shortcut.TargetPath; $shortcut.Description = 'Open Codex Usage Tray'; $shortcut.Save()" >nul 2>&1
start "" "%installDir%\CodexUsageTray.exe"
exit /b 0
