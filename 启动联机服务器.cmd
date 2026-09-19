@echo off
title UNITY FPS - 启动联机服务器
echo.
echo   ==============================================
echo    UNITY FPS 本地联机服务器 一键启动
echo    （后端控制面 + 专用服务器，重复双击不会重启）
echo   ==============================================
echo.
echo   正在检查并启动，首次启动后端可能需要约 1 分钟……
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Tools\Server\Start-LocalServer.ps1" %*
set EC=%ERRORLEVEL%
echo.
if "%EC%"=="0" (
  echo   [完成] 服务器已就绪，本窗口可以关闭，服务在后台继续运行。
  echo          客户端在大厅点"建房开战 / 加入房间"即可联机。
  echo          停止服务请双击：停止联机服务器.cmd
) else (
  echo   [失败] 启动未完成。请把上方 FAILED 开头的行及其明细发给开发者排查。
)
echo.
pause
