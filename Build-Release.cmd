@echo off
setlocal
powershell -ExecutionPolicy Bypass -File "%~dp0scripts\Build-Release.ps1" %*
exit /b %errorlevel%
