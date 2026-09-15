@echo off
chcp 65001 >nul
title Baidu Netdisk Open API Speed Test
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0baidu-speedtest.ps1"
echo.
echo Press any key to close this window...
pause >nul
