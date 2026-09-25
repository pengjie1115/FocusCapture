@echo off
chcp 65001 >nul
cd /d "%~dp0"

echo.
echo  ===== FocusCapture automated checks =====
echo.

rem %1 is forwarded to the test app (--all = also run the out-of-bucket groups).
rem Keep added lines ASCII-only: Chinese inside .bat gets mangled by cmd.
if "%1"=="" goto fc_noargs
dotnet run --project "%~dp0FocusCapture.Tests.csproj" --nologo -v q -- %1
goto fc_afterrun
:fc_noargs
dotnet run --project "%~dp0FocusCapture.Tests.csproj" --nologo -v q
:fc_afterrun

set RESULT=%ERRORLEVEL%

echo.
if %RESULT%==0 (
    echo  [RESULT] ALL CHECKS PASSED
) else (
    echo  [RESULT] SOME CHECKS FAILED  -  exit code %RESULT%
)
echo.
pause

rem 把检查点程序的退出码原样传出去（2026-09-20 补）。
rem 没有这一行时，本脚本的退出码 = pause 的退出码（恒为 0）→ 调用方（tools\dev.ps1）
rem 永远认为"全部通过"，AGENTS.md 里"退出码 0 = 检查点全过"这条契约就是假的。
rem pause 放前面：人双击时仍能看完结果再按键；RESULT 在 pause 之前就存好了，不受影响。
exit /b %RESULT%
