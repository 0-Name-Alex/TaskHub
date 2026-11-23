@echo off
rem Double-click to build and test. Full output goes to build-log.txt.
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
echo.
echo Finished with exit code %ERRORLEVEL%. See build-log.txt
timeout /t 15
