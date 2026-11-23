@echo off
rem Creates a new EF Core migration after you change the data model.
rem Usage: new-migration.cmd AddRecurrence
if "%~1"=="" (echo Usage: new-migration.cmd ^<Name^> & exit /b 1)
cd /d "%~dp0"
dotnet tool restore
dotnet ef migrations add %1 --project src\TaskHub.Storage --startup-project src\TaskHub.Storage --output-dir Migrations
pause
