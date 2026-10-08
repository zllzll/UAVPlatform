@echo off
rem 一键启动（Windows 双击即可）。参数原样透传给 run.ps1。
rem   run.cmd            生产模式
rem   run.cmd -Dev       开发模式（前端热更新）
rem   run.cmd -SkipBuild 跳过构建
rem   run.cmd -NoBrowser 不自动打开浏览器
setlocal
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run.ps1" %*
if errorlevel 1 (
  echo.
  echo [启动失败] 请查看上方错误信息。
  pause
)
endlocal
