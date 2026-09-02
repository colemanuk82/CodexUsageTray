@echo off
setlocal
set "installDir=%LOCALAPPDATA%\CodexUsageTray"
taskkill /IM CodexUsageTray.exe /F >nul 2>&1
if not exist "%installDir%" mkdir "%installDir%"
xcopy "%~dp0*" "%installDir%\" /E /I /Y >nul
start "" "%installDir%\CodexUsageTray.exe"
exit /b 0
