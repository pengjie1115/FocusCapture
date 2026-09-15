@echo off
chcp 65001 >nul
title Baidu Netdisk Speed Diag
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0baidu-diag.ps1"
echo.
echo Press any key to close this window...
pause >nul
