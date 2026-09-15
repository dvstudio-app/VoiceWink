@echo off
:: VoiceWink Installer -- double-click to install
powershell -ExecutionPolicy Bypass -File "%~dp0VoiceWinkSetup.ps1"
if %ERRORLEVEL% NEQ 0 (
    echo.
    echo Installation encountered an error.
    pause
)
