@echo off
REM FocusCapture dev entry point (human-facing / double-click).
REM NOTE: keep this file pure ASCII -- this machine corrupts non-ASCII in .bat.
REM Agents should call tools\dev.ps1 directly instead of this wrapper.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0dev.ps1" %*
set EC=%ERRORLEVEL%
echo.
echo [exit code: %EC%]
if "%~1"=="" pause
exit /b %EC%
