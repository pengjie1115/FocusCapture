@echo off
chcp 65001 >nul
cd /d "%~dp0"

echo.
echo  ===== FocusCapture automated checks =====
echo.

dotnet run --project "%~dp0FocusCapture.Tests.csproj" --nologo -v q

set RESULT=%ERRORLEVEL%

echo.
if %RESULT%==0 (
    echo  [RESULT] ALL CHECKS PASSED
) else (
    echo  [RESULT] SOME CHECKS FAILED  -  exit code %RESULT%
)
echo.
pause
