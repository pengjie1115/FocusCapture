@echo off
REM Clipboard holder probe (diagnostic only).
REM Usage: double-click, then reproduce the copy failure while it samples.
cd /d "%~dp0"
dotnet run --project "clipdiag\ClipDiag.csproj" -c Release -- --seconds 90 --interval 500
echo.
echo Log written to: %TEMP%\fc-clipdiag.log
pause
