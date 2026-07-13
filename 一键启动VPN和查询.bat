@echo off
chcp 65001 >nul
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0start-vpn-and-monitor.ps1"
if errorlevel 1 (
  echo.
  echo 程序异常退出，请查看上面的错误信息。
  pause
)
