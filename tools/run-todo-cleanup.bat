@echo off
chcp 65001 >nul
echo ==================================================
echo  FocusCapture note/todo duplicate cleanup
echo  MODE: PREVIEW (reports only, changes nothing)
echo ==================================================
echo.
echo To actually apply the cleanup, run in a terminal:
echo   powershell -NoProfile -ExecutionPolicy Bypass -File tools\todo-cleanup.ps1 -Apply
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0todo-cleanup.ps1"
echo.
pause
