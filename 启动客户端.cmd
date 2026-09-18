@echo off
title UNITY FPS - Launch client (unique log)
echo.
echo   ==============================================
echo    UNITY FPS - SOLE client entry for dual-end testing
echo    Launches Builds\ReleaseClient\UnityFpsClient.exe
echo    Each run gets a unique -logFile (Tools\Client\Logs\). DO NOT launch the exe directly.
echo   ==============================================
echo.
rem 测试收口闸（2026-09-19）：源码比构建新时警告并要求确认，防止再测到旧构建
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Tools\BuildStatus.ps1" -Gate
if errorlevel 3 (
  echo.
  choice /C YN /M "  [!] 构建已落后于源码，仍要启动客户端测试吗 Y=继续 N=取消"
  if errorlevel 2 (
    echo   已取消。请先在 Unity 里重建：Tools/Client/Build Windows Client (Release)
    pause
    exit /b 1
  )
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Tools\Client\Start-LocalClient.ps1" %*
echo.
pause
