# Builds and tests the solution. Output is also written to build-log.txt.
# Usage:  .\build.ps1            (build + test)
#         .\build.ps1 -Run       (build + test, then start the app)
#         .\build.ps1 -Publish   (self-contained single-file publish to .\artifacts)
param([switch]$Run, [switch]$Publish)

$ErrorActionPreference = 'Continue'
Set-Location $PSScriptRoot
$log = Join-Path $PSScriptRoot 'build-log.txt'
"=== $(Get-Date -Format s) ===" | Out-File $log -Encoding utf8

# Windows PowerShell's Tee-Object writes UTF-16; keep the log UTF-8.
function Write-Log([string]$line) { Write-Host $line; Add-Content -Path $log -Value $line -Encoding utf8 }

function Step($title, [scriptblock]$cmd) {
    Write-Log "`n### $title"
    & $cmd 2>&1 | ForEach-Object { Write-Log "$_" }
    if ($LASTEXITCODE -ne 0) {
        Write-Log "### FAILED: $title (exit $LASTEXITCODE)"
        exit $LASTEXITCODE
    }
}

Step 'dotnet --version' { dotnet --version }
Step 'restore' { dotnet restore TaskHub.slnx }

Step 'build'   { dotnet build TaskHub.slnx -c Debug --no-restore -v:minimal }

# Database migrations: if the EF model changed, generate a migration automatically, then rebuild.
# The name comes from src/TaskHub.Storage/next-migration.txt (or a timestamp).
$efArgs = @('--project', 'src/TaskHub.Storage', '--startup-project', 'src/TaskHub.Storage', '--no-build')
Step 'tool restore' { dotnet tool restore }
$addedMigration = $false
if (-not (Test-Path 'src/TaskHub.Storage/Migrations')) {
    Step 'create initial database migration' { dotnet ef migrations add InitialCreate @efArgs --output-dir Migrations }
    $addedMigration = $true
} else {
    Write-Log "`n### check for model changes"
    dotnet ef migrations has-pending-model-changes @efArgs 2>&1 | ForEach-Object { Write-Log "$_" }
    if ($LASTEXITCODE -ne 0) {
        $migration = if (Test-Path 'src/TaskHub.Storage/next-migration.txt') { (Get-Content 'src/TaskHub.Storage/next-migration.txt' -Raw).Trim() } else { '' }
        if (-not $migration -or (Get-ChildItem 'src/TaskHub.Storage/Migrations' -Filter "*_$migration.cs" -ErrorAction SilentlyContinue)) {
            $migration = 'Update' + (Get-Date -Format 'yyyyMMddHHmm')
        }
        Step "add database migration $migration" { dotnet ef migrations add $migration @efArgs --output-dir Migrations }
        $addedMigration = $true
    }
}
if ($addedMigration) {
    Step 'rebuild with new migration' { dotnet build TaskHub.slnx -c Debug --no-restore -v:minimal }
}

Step 'test'    { dotnet test TaskHub.slnx -c Debug --no-build -v:minimal --logger "console;verbosity=normal" }

if ($Publish) {
    Step 'publish (standalone)' {
        dotnet publish src/TaskHub.App/TaskHub.App.csproj -c Release -r win-x64 --self-contained `
            -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/TaskHub
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        dotnet publish src/TaskHub.Cli/TaskHub.Cli.csproj -c Release -r win-x64 --self-contained `
            -p:PublishSingleFile=true -o artifacts/TaskHub
    }
    Step 'publish (lite without .NET runtime)' {
        if (Test-Path artifacts/TaskHub-lite) { Remove-Item artifacts/TaskHub-lite -Recurse -Force }
        dotnet publish src/TaskHub.App/TaskHub.App.csproj -c Release -r win-x64 --no-self-contained `
            -p:DebugType=none -p:DebugSymbols=false -o artifacts/TaskHub-lite
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        dotnet publish src/TaskHub.Cli/TaskHub.Cli.csproj -c Release -r win-x64 --no-self-contained `
            -p:DebugType=none -p:DebugSymbols=false -o artifacts/TaskHub-lite
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        New-Item -ItemType Directory -Path release -Force | Out-Null
        Compress-Archive -Path artifacts/TaskHub-lite/* -DestinationPath release/TaskHub-win-x64-net10-lite.zip -Force
    }
}

Write-Log "`n### ALL OK"

if ($Run) { Start-Process (Join-Path $PSScriptRoot 'src/TaskHub.App/bin/Debug/net10.0-windows/TaskHub.exe') }
