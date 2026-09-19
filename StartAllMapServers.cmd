@echo off
title UNITY FPS - start all-map servers
echo.
echo   ==============================================
echo    UNITY FPS local launcher (ALL MAPS)
echo    backend + arena DS + map_01..04 DS x5
echo    repeat runs are idempotent
echo   ==============================================
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Tools\Server\Start-LocalServer.ps1" -AllMaps %*
set EC=%ERRORLEVEL%
echo.
if "%EC%"=="0" (
  echo   [OK] servers ready. Close this window; services keep running.
  echo       Stop via: StopLocalServer.cmd
) else (
  echo   [FAILED] send the FAILED lines above to the developer.
)
echo.
pause