@echo off
rem Runs a separate dev instance with its own config/data/logs in .devhome (does not touch your real TaskHub data).
set TASKHUB_HOME=%~dp0.devhome
start "" "%~dp0src\TaskHub.App\bin\Debug\net10.0-windows\TaskHub.exe" %*
