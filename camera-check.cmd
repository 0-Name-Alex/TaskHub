@echo off
rem Double-click: records what Windows sees while you press the camera key (40 seconds).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0camera-check.ps1"
