@echo off
title UNITY FPS - 停止联机服务器
echo.
echo   ==============================================
echo    UNITY FPS 本地联机服务器 一键停止
echo    （只停止本启动入口拉起的服务，其他进程不动）
echo   ==============================================
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Tools\Server\Stop-LocalServer.ps1" %*
echo.
pause
