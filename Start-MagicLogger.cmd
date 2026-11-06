@echo off
setlocal
if not exist "%~dp0MagicLogger.exe" (
    echo MagicLogger.exe was not found next to this launcher.
    exit /b 1
)
start "" /D "%~dp0" "%~dp0MagicLogger.exe"
