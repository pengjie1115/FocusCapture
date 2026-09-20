@echo off
REM FocusCapture skill-runtime bootstrap (human-facing / double-click).
REM NOTE: keep this file pure ASCII -- this machine corrupts non-ASCII in .bat.
REM Downloads the portable Python runtime into runtime\python\ (about 11MB).
REM Agents: this machine's policy is "Restricted", so calling the .ps1 directly
REM is blocked. Run inside one shell session:
REM     Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
REM     & tools\fetch-python-runtime.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0fetch-python-runtime.ps1" %*
set EC=%ERRORLEVEL%
echo.
echo [exit code: %EC%]
if "%~1"=="" pause
exit /b %EC%
