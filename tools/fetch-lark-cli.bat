@echo off
REM FocusCapture skill-runtime bootstrap (human-facing / double-click).
REM NOTE: keep this file pure ASCII -- this machine corrupts non-ASCII in .bat.
REM Downloads the official lark-cli binary into runtime\lark-cli\ (about 47MB).
REM Needed by Skills that talk to Feishu/Lark (e.g. feishu-kb-manager).
REM Agents: this machine's policy is "Restricted", so calling the .ps1 directly
REM is blocked. Run inside one shell session:
REM     Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
REM     & tools\fetch-lark-cli.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0fetch-lark-cli.ps1" %*
set EC=%ERRORLEVEL%
echo.
echo [exit code: %EC%]
if "%~1"=="" pause
exit /b %EC%
