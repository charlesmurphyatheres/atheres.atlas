#Requires -Version 7.0
# =============================================================
# Atheres Atlas — Docker Deployment Script  (Windows PowerShell 7+)
#
# Usage:
#   .\deploy.ps1              # full deploy
#   .\deploy.ps1 -Build       # force rebuild all images
#   .\deploy.ps1 -Down        # stop and remove all containers
#   .\deploy.ps1 -Clean       # stop + remove containers + volumes
#   .\deploy.ps1 -Logs        # tail logs after deploy
# =============================================================

[CmdletBinding()]
param(
    [switch]$Build,
    [switch]$Down,
    [switch]$Clean,
    [switch]$Logs,
    [switch]$Help
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# ---- Helpers ------------------------------------------------
function Write-Header  { param($msg) Write-Host "`n=== $msg ===" -ForegroundColor Cyan }
function Write-Info    { param($msg) Write-Host "[INFO]  $msg"  -ForegroundColor Cyan }
function Write-Ok      { param($msg) Write-Host "[OK]    $msg"  -ForegroundColor Green }
function Write-Warn    { param($msg) Write-Host "[WARN]  $msg"  -ForegroundColor Yellow }
function Write-Err     { param($msg) Write-Host "[ERROR] $msg"  -ForegroundColor Red }

# ---- Help ---------------------------------------------------
if ($Help) {
    Write-Host "Usage: .\deploy.ps1 [-Build] [-Down] [-Clean] [-Logs]"
    exit 0
}

# ---- Down ---------------------------------------------------
if ($Down) {
    Write-Header "Stopping Atheres Atlas"
    docker compose down
    Write-Ok "All containers stopped."
    exit 0
}

# ---- Clean --------------------------------------------------
if ($Clean) {
    Write-Header "Removing Atheres Atlas (containers + volumes)"
    Write-Warn "This will DELETE all database data!"
    $confirm = Read-Host "Are you sure? [y/N]"
    if ($confirm -notmatch '^[Yy]$') { Write-Info "Aborted."; exit 0 }
    docker compose down --volumes --remove-orphans
    Write-Ok "Cleanup complete."
    exit 0
}

# ---- Prerequisites ------------------------------------------
Write-Header "Checking prerequisites"

foreach ($cmd in @("docker", "curl")) {
    if (-not (Get-Command $cmd -ErrorAction SilentlyContinue)) {
        Write-Err "Required command not found: $cmd"
        exit 1
    }
}

$dockerVer = (docker version --format '{{.Server.Version}}' 2>$null)
Write-Info "Docker version: $dockerVer"

docker compose version 2>$null | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Err "Docker Compose v2 not found. Install Docker Desktop."
    exit 1
}

# ---- Environment file ---------------------------------------
Write-Header "Checking environment"

$envFile = ".\.env"
if (-not (Test-Path $envFile)) {
    if (Test-Path ".\.env.docker") {
        Write-Warn ".env not found — copying .env.docker to .env"
        Copy-Item ".\.env.docker" $envFile
        Write-Warn "Edit .env and fill in real API keys."
        Read-Host "Press Enter to continue with placeholder values, or Ctrl+C to abort"
    } else {
        Write-Err "No .env or .env.docker file found."
        exit 1
    }
}

# Load env file into current process
Get-Content $envFile | Where-Object { $_ -match '^\s*[^#]' -and $_ -match '=' } | ForEach-Object {
    $parts = $_ -split '=', 2
    $key   = $parts[0].Trim()
    $value = $parts[1].Trim().Trim('"')
    [System.Environment]::SetEnvironmentVariable($key, $value, "Process")
}

$requiredVars = @(
    "MSSQL_SA_PASSWORD", "GOOGLE_MAPS_API_KEY", "SENDGRID_API_KEY",
    "AZURE_SIGNALR_CONNECTION", "JWT_SECRET_KEY"
)

$missing = 0
foreach ($var in $requiredVars) {
    $val = [System.Environment]::GetEnvironmentVariable($var)
    if ([string]::IsNullOrWhiteSpace($val) -or $val -like "*<your*") {
        Write-Warn "  $var is not set or still a placeholder"
        $missing++
    }
}
if ($missing -gt 0) {
    Write-Warn "$missing variable(s) need real values — some features may not work."
    $confirm = Read-Host "Continue anyway? [y/N]"
    if ($confirm -notmatch '^[Yy]$') { Write-Info "Aborted."; exit 0 }
}

# ---- Pull base images ---------------------------------------
Write-Header "Pulling base images"

$images = @(
    "mcr.microsoft.com/mssql/server:2022-latest",
    "mcr.microsoft.com/azure-storage/azurite:3.33.0",
    "mcr.microsoft.com/azure-sql-edge:latest",
    "mcr.microsoft.com/azure-messaging/servicebus-emulator:latest",
    "nginx:1.27-alpine",
    "node:20-alpine",
    "mcr.microsoft.com/dotnet/sdk:8.0",
    "mcr.microsoft.com/azure-functions/dotnet-isolated:4-dotnet-isolated8.0"
)

$jobs = $images | ForEach-Object {
    $img = $_
    Start-Job -ScriptBlock { docker pull $using:img }
}
$jobs | Wait-Job | Out-Null
$jobs | Receive-Job
$jobs | Remove-Job
Write-Ok "Base images ready."

# ---- Build application images -------------------------------
Write-Header "Building application images"

$buildArg = if ($Build) { "--build" } else { "" }

Invoke-Expression "docker compose build $buildArg --parallel migrations functions auth frontend demo"
if ($LASTEXITCODE -ne 0) { Write-Err "Build failed."; exit 1 }
Write-Ok "Images built."

# ---- Start infrastructure -----------------------------------
Write-Header "Starting infrastructure services"

docker compose up -d sql azurite servicebus-sql servicebus

# Wait for SQL Server
Write-Info "Waiting for SQL Server..."
$saPassword = [System.Environment]::GetEnvironmentVariable("MSSQL_SA_PASSWORD")
$retries = 30
$ready = $false
while ($retries -gt 0 -and -not $ready) {
    try {
        $result = docker compose exec -T sql `
            /opt/mssql-tools18/bin/sqlcmd `
            -S localhost -U sa -P $saPassword `
            -Q "SELECT 1" -b -No 2>&1
        if ($LASTEXITCODE -eq 0) { $ready = $true }
    } catch { }
    if (-not $ready) {
        Start-Sleep 3
        Write-Host "." -NoNewline
        $retries--
    }
}
Write-Host ""
if (-not $ready) {
    Write-Err "SQL Server did not become healthy."
    docker compose logs sql | Select-Object -Last 20
    exit 1
}
Write-Ok "SQL Server is ready."

# Wait for Service Bus emulator
Write-Info "Waiting for Service Bus emulator..."
$retries = 30
$sbReady = $false
while ($retries -gt 0 -and -not $sbReady) {
    try {
        $tcpClient = New-Object System.Net.Sockets.TcpClient
        $tcpClient.Connect("localhost", 5672)
        $tcpClient.Close()
        $sbReady = $true
    } catch { }
    if (-not $sbReady) {
        Start-Sleep 3
        Write-Host "." -NoNewline
        $retries--
    }
}
Write-Host ""
if (-not $sbReady) {
    Write-Err "Service Bus emulator did not start."
    docker compose logs servicebus | Select-Object -Last 20
    exit 1
}
Write-Ok "Service Bus emulator is ready."

# ---- Run EF Migrations --------------------------------------
Write-Header "Running database migrations"

docker compose run --rm migrations
if ($LASTEXITCODE -ne 0) {
    Write-Err "Migration failed. Run: docker compose logs migrations"
    exit 1
}
Write-Ok "Migrations applied."

# ---- Import reference data (stores + warehouses) ---------------
Write-Header "Importing reference data"
$importScript = Join-Path $PSScriptRoot "import-data.ps1"
if (Test-Path $importScript) {
    $saPassword = [System.Environment]::GetEnvironmentVariable("MSSQL_SA_PASSWORD")
    $dockerConnStr = "Server=localhost;Database=AtheresAtlas;User Id=sa;Password=$saPassword;TrustServerCertificate=True;"
    & $importScript -ConnectionString $dockerConnStr
}

# ---- Start application services -----------------------------
Write-Header "Starting application services"

docker compose up -d functions auth frontend demo nginx

# Wait for nginx health
Write-Info "Waiting for application to be healthy..."
$retries = 40
$appReady = $false
while ($retries -gt 0 -and -not $appReady) {
    try {
        $response = Invoke-WebRequest -Uri "http://localhost:9708/health" -UseBasicParsing -TimeoutSec 3
        if ($response.StatusCode -eq 200) { $appReady = $true }
    } catch { }
    if (-not $appReady) {
        Start-Sleep 3
        Write-Host "." -NoNewline
        $retries--
    }
}
Write-Host ""
if (-not $appReady) {
    Write-Err "Application did not become healthy. Dumping logs:"
    docker compose logs --tail 30 functions auth frontend nginx
    exit 1
}

# ---- Seed Secure Transport company ----------------------------
Write-Header "Seeding Secure Transport"
$seedScript = Join-Path $PSScriptRoot "seed-secure-transport.ps1"
if (Test-Path $seedScript) {
    $saPassword = [System.Environment]::GetEnvironmentVariable("MSSQL_SA_PASSWORD")
    $dockerBusinessConn = "Server=localhost;Database=AtheresAtlas;User Id=sa;Password=$saPassword;TrustServerCertificate=True;"
    $dockerIdentityConn = "Server=localhost;Database=AtheresAtlas;User Id=sa;Password=$saPassword;TrustServerCertificate=True;"
    & $seedScript -ConnectionString $dockerBusinessConn -IdentityConnectionString $dockerIdentityConn
}

# ---- Seed additional global users ------------------------------
Write-Header "Seeding additional users"
$seedUsersScript = Join-Path $PSScriptRoot "seed-users.ps1"
if (Test-Path $seedUsersScript) {
    & $seedUsersScript -AuthApiUrl "http://localhost:7072"
}

# ---- Summary ------------------------------------------------
Write-Header "Atheres Atlas is running"

Write-Host ""
Write-Host "  Application      " -NoNewline; Write-Host "http://localhost:9708"              -ForegroundColor Green
Write-Host "  Demo Simulator   " -NoNewline; Write-Host "http://localhost:3001"              -ForegroundColor Green
Write-Host "  Swagger UI       " -NoNewline; Write-Host "http://localhost:7071/api/swagger"  -ForegroundColor Green
Write-Host "  Functions API    " -NoNewline; Write-Host "http://localhost:7071/api"          -ForegroundColor Gray
Write-Host "  Auth Functions   " -NoNewline; Write-Host "http://localhost:7072/api"          -ForegroundColor Gray
Write-Host "  SQL Server       " -NoNewline; Write-Host "localhost:1433  (sa / `$MSSQL_SA_PASSWORD)" -ForegroundColor Gray
Write-Host "  Service Bus      " -NoNewline; Write-Host "localhost:5672"                      -ForegroundColor Gray
Write-Host "  Azurite Blob     " -NoNewline; Write-Host "localhost:10000"                     -ForegroundColor Gray
Write-Host ""

# ---- Print all seeded user credentials --------------------------
Write-Host "==============================================================" -ForegroundColor Yellow
Write-Host "  SEEDED USERS" -ForegroundColor Yellow
Write-Host "==============================================================" -ForegroundColor Yellow
Write-Host ""
Write-Host "  Role        Email                        Password                 Scope"             -ForegroundColor White
Write-Host "  ----------  ---------------------------  -----------------------  ------------------"  -ForegroundColor DarkGray
Write-Host "  SuperAdmin  charles.murphy@atheres.com   Albeniz<18651909>        (global)"           -ForegroundColor Green
Write-Host "  SuperAdmin  ken@atheres.com              Phone@3313059708         (global)"           -ForegroundColor Green
Write-Host "  Admin       steven@gmail.com             Secure@1234567890        Secure Transport"   -ForegroundColor Green
Write-Host ""

Write-Host "  Useful commands:"
Write-Host "    docker compose ps                   -> container status"
Write-Host "    docker compose logs -f              -> tail all logs"
Write-Host "    .\deploy.ps1 -Down                  -> stop everything"
Write-Host "    .\deploy.ps1 -Clean                 -> stop + wipe volumes"
Write-Host ""

$credFile = Join-Path $PSScriptRoot "CREDENTIALS.txt"
if (Test-Path $credFile) { Start-Process $credFile }
Start-Process "http://localhost:9708"
Start-Process "http://localhost:3001"

if ($Logs) {
    Write-Info "Tailing logs (Ctrl+C to exit)..."
    docker compose logs -f
}
