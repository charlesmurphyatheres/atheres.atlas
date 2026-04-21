#Requires -Version 7.0
# =============================================================
# Atheres Atlas — Local Debug Deployment  (Windows PowerShell 7+)
#
# Runs infrastructure in Docker; Function apps + frontend run
# natively so you can attach a debugger from VS Code / Visual Studio.
#
# Usage:
#   .\deploy-debug.ps1                  # full local debug deploy
#   .\deploy-debug.ps1 -InfraOnly       # Docker infra only (SQL, Azurite, Service Bus)
#   .\deploy-debug.ps1 -NoFrontend      # skip the React dev server
#   .\deploy-debug.ps1 -SkipMigrations  # skip EF Core database update
#   .\deploy-debug.ps1 -WaitDebugger    # pause function hosts until debugger attaches
#   .\deploy-debug.ps1 -Stop            # stop all running debug services
#   .\deploy-debug.ps1 -Help            # show this help
# =============================================================

[CmdletBinding()]
param(
    [switch]$Stop,
    [switch]$InfraOnly,
    [switch]$NoFrontend,
    [switch]$SkipMigrations,
    [switch]$WaitDebugger,
    [switch]$Help
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$root = $PSScriptRoot
$pidFile = Join-Path $root ".debug-pids"

# ---- Helpers ------------------------------------------------
function Write-Header  { param($msg) Write-Host "`n=== $msg ===" -ForegroundColor Cyan }
function Write-Info    { param($msg) Write-Host "[INFO]  $msg"  -ForegroundColor Cyan }
function Write-Ok      { param($msg) Write-Host "[OK]    $msg"  -ForegroundColor Green }
function Write-Warn    { param($msg) Write-Host "[WARN]  $msg"  -ForegroundColor Yellow }
function Write-Err     { param($msg) Write-Host "[ERROR] $msg"  -ForegroundColor Red }

# ---- Help ---------------------------------------------------
if ($Help) {
    Write-Host @"
Atheres Atlas — Local Debug Deployment

  .\deploy-debug.ps1                  Full local debug deploy
  .\deploy-debug.ps1 -InfraOnly       Docker infra only
  .\deploy-debug.ps1 -NoFrontend      Skip React dev server
  .\deploy-debug.ps1 -SkipMigrations  Skip EF Core migrations
  .\deploy-debug.ps1 -WaitDebugger    Pause function hosts until debugger attaches
  .\deploy-debug.ps1 -Stop            Stop all running debug services

Infrastructure (Docker): SQL Server :1433, Azurite :10000-10002, Service Bus :5672
Emulators (native):      SignalR Emulator :8888
Applications (native):   Functions :7071, Auth Functions :7072, Frontend :3000
"@
    exit 0
}

# ---- Stop ---------------------------------------------------
if ($Stop) {
    Write-Header "Stopping debug services"

    # Kill tracked native processes
    if (Test-Path $pidFile) {
        $pids = Get-Content $pidFile
        foreach ($p in $pids) {
            try {
                $proc = Get-Process -Id $p -ErrorAction SilentlyContinue
                if ($proc -and -not $proc.HasExited) {
                    # Kill the process tree (func host spawns child dotnet processes)
                    Stop-Process -Id $p -Force -ErrorAction SilentlyContinue
                    # Also kill child processes
                    Get-CimInstance Win32_Process | Where-Object { $_.ParentProcessId -eq $p } | ForEach-Object {
                        Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
                    }
                    Write-Info "Stopped PID $p ($($proc.ProcessName))"
                }
            } catch { }
        }
        Remove-Item $pidFile -Force
    }

    # Stop Docker infrastructure
    Push-Location $root
    docker compose stop sql azurite servicebus-sql servicebus 2>$null
    Pop-Location
    Write-Ok "All debug services stopped."
    Write-Info "Run '.\deploy-debug.ps1 -Stop' with -Clean flag on deploy.ps1 to remove Docker volumes."
    exit 0
}

# ---- Prerequisites ------------------------------------------
Write-Header "Checking prerequisites"

$missing = @()
foreach ($cmd in @("docker", "dotnet", "func", "node", "npm")) {
    if (-not (Get-Command $cmd -ErrorAction SilentlyContinue)) {
        $missing += $cmd
    }
}

if ($missing.Count -gt 0) {
    Write-Err "Missing required commands: $($missing -join ', ')"
    if ($missing -contains "func") {
        Write-Info "Install Azure Functions Core Tools: npm install -g azure-functions-core-tools@4"
    }
    exit 1
}

$dotnetVer = (dotnet --version 2>$null)
$funcVer   = (func --version 2>$null)
$nodeVer   = (node --version 2>$null)
Write-Info ".NET SDK: $dotnetVer | func: $funcVer | Node: $nodeVer"

docker compose version 2>$null | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Err "Docker Compose v2 not found. Install Docker Desktop."
    exit 1
}

# ---- Environment file ---------------------------------------
Write-Header "Loading environment"

$envFile = Join-Path $root ".env"
if (Test-Path $envFile) {
    Get-Content $envFile | Where-Object { $_ -match '^\s*[^#]' -and $_ -match '=' } | ForEach-Object {
        $parts = $_ -split '=', 2
        $key   = $parts[0].Trim()
        $value = $parts[1].Trim().Trim('"')
        [System.Environment]::SetEnvironmentVariable($key, $value, "Process")
    }
    Write-Ok "Loaded .env"
} else {
    Write-Warn "No .env file found — using defaults from local.settings.json"
}

# ---- Start Docker infrastructure ----------------------------
Write-Header "Starting Docker infrastructure"

Push-Location $root
docker compose up -d sql azurite servicebus-sql servicebus
Pop-Location

if ($LASTEXITCODE -ne 0) {
    Write-Err "Failed to start Docker infrastructure."
    exit 1
}

# Wait for SQL Server
Write-Info "Waiting for SQL Server on localhost:1433..."
$retries = 30
$ready = $false
while ($retries -gt 0 -and -not $ready) {
    try {
        $tcp = New-Object System.Net.Sockets.TcpClient
        $tcp.Connect("localhost", 1433)
        $tcp.Close()
        $ready = $true
    } catch { }
    if (-not $ready) {
        Start-Sleep 3
        Write-Host "." -NoNewline
        $retries--
    }
}
Write-Host ""
if (-not $ready) {
    Write-Err "SQL Server did not become available on port 1433."
    exit 1
}
Write-Ok "SQL Server is ready."

# Wait for Service Bus emulator
Write-Info "Waiting for Service Bus emulator on localhost:5672..."
$retries = 30
$sbReady = $false
while ($retries -gt 0 -and -not $sbReady) {
    try {
        $tcp = New-Object System.Net.Sockets.TcpClient
        $tcp.Connect("localhost", 5672)
        $tcp.Close()
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
    Write-Err "Service Bus emulator did not start on port 5672."
    exit 1
}
Write-Ok "Service Bus emulator is ready."

# Wait for Azurite
Write-Info "Waiting for Azurite on localhost:10000..."
$retries = 15
$azReady = $false
while ($retries -gt 0 -and -not $azReady) {
    try {
        $tcp = New-Object System.Net.Sockets.TcpClient
        $tcp.Connect("localhost", 10000)
        $tcp.Close()
        $azReady = $true
    } catch { }
    if (-not $azReady) {
        Start-Sleep 2
        Write-Host "." -NoNewline
        $retries--
    }
}
Write-Host ""
if (-not $azReady) {
    Write-Err "Azurite did not start on port 10000."
    exit 1
}
Write-Ok "Azurite is ready."

if ($InfraOnly) {
    Write-Header "Infrastructure is running (InfraOnly mode)"
    Write-Host ""
    Write-Host "  SQL Server       " -NoNewline; Write-Host "localhost:1433" -ForegroundColor Green
    Write-Host "  Azurite          " -NoNewline; Write-Host "localhost:10000-10002" -ForegroundColor Green
    Write-Host "  Service Bus      " -NoNewline; Write-Host "localhost:5672" -ForegroundColor Green
    Write-Host "  SignalR Emulator " -NoNewline; Write-Host "localhost:8888 (start separately or use full deploy)" -ForegroundColor Green
    Write-Host ""
    exit 0
}

# ---- EF Core Migrations ------------------------------------
if (-not $SkipMigrations) {
    Write-Header "Running EF Core migrations"

    $dataDir = Join-Path $root "Atheres.Atlas.Data"
    $functionsProj = Join-Path $root "Atheres.Atlas.Functions"

    Write-Info "Migrating AtlasDbContext..."
    Push-Location $dataDir
    dotnet ef database update --context AtlasDbContext --startup-project $functionsProj --no-build 2>$null
    if ($LASTEXITCODE -ne 0) {
        Write-Warn "Migration with --no-build failed, retrying with build..."
        dotnet ef database update --context AtlasDbContext --startup-project $functionsProj
        if ($LASTEXITCODE -ne 0) {
            Write-Err "AtlasDbContext migration failed."
            Pop-Location
            exit 1
        }
    }
    Write-Ok "AtlasDbContext migrated."

    Write-Info "Migrating AtlasIdentityDbContext..."
    dotnet ef database update --context AtlasIdentityDbContext --startup-project $functionsProj --no-build 2>$null
    if ($LASTEXITCODE -ne 0) {
        Write-Warn "Migration with --no-build failed, retrying with build..."
        dotnet ef database update --context AtlasIdentityDbContext --startup-project $functionsProj
        if ($LASTEXITCODE -ne 0) {
            Write-Err "AtlasIdentityDbContext migration failed."
            Pop-Location
            exit 1
        }
    }
    Pop-Location
    Write-Ok "AtlasIdentityDbContext migrated."
} else {
    Write-Info "Skipping migrations (-SkipMigrations)"
}

# ---- Import reference data (stores + warehouses) ---------------
if (-not $SkipMigrations) {
    $importScript = Join-Path $root "import-data.ps1"
    if (Test-Path $importScript) {
        Write-Header "Importing reference data"
        & $importScript
    }

    $seedScript = Join-Path $root "seed-secure-transport.ps1"
    if (Test-Path $seedScript) {
        Write-Header "Seeding Secure Transport"
        & $seedScript
    }

    $seedUsersScript = Join-Path $root "seed-users.ps1"
    if (Test-Path $seedUsersScript) {
        Write-Header "Seeding additional users"
        & $seedUsersScript
    }
}

# ---- Kill stale func hosts before building -------------------
Write-Info "Stopping any stale hosts from previous runs..."
# Kill tracked PIDs from a previous deploy-debug run
if (Test-Path $pidFile) {
    Get-Content $pidFile | ForEach-Object {
        $ppid = [int]$_
        # Kill child processes first (dotnet workers spawned by func/pwsh)
        Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
            Where-Object { $_.ParentProcessId -eq $ppid } |
            ForEach-Object {
                Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
            }
        Stop-Process -Id $ppid -Force -ErrorAction SilentlyContinue
    }
    Remove-Item $pidFile -Force
}
Get-Process -Name "func" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep 2

# ---- Clean obj lock files ------------------------------------
$funcDir = Join-Path $root "Atheres.Atlas.Functions"
$authDir = Join-Path $root "Atheres.Atlas.Auth.Functions"

# Remove WorkerExtensions buildout to avoid file-lock build errors
$funcWorkerBuild = Join-Path $funcDir "obj\Debug\net8.0\WorkerExtensions\buildout"
$authWorkerBuild = Join-Path $authDir "obj\Debug\net8.0\WorkerExtensions\buildout"
if (Test-Path $funcWorkerBuild) { Remove-Item $funcWorkerBuild -Recurse -Force -ErrorAction SilentlyContinue }
if (Test-Path $authWorkerBuild) { Remove-Item $authWorkerBuild -Recurse -Force -ErrorAction SilentlyContinue }

# ---- Build .NET projects ------------------------------------
Write-Header "Building .NET projects"

dotnet build (Join-Path $funcDir "Atheres.Atlas.Functions.csproj") -c Debug
if ($LASTEXITCODE -ne 0) { Write-Err "Functions build failed."; exit 1 }

dotnet build (Join-Path $authDir "Atheres.Atlas.Auth.Functions.csproj") -c Debug
if ($LASTEXITCODE -ne 0) { Write-Err "Auth Functions build failed."; exit 1 }

Write-Ok "Build succeeded."

# ---- Prepare PID tracking -----------------------------------
$trackedPids = @()

# ---- Service Bus emulator connection string -----------------
# When running locally (outside Docker network), connect via localhost
$sbEmulatorConn = "Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;"

# ---- Start SignalR Emulator (port 8888) ---------------------
Write-Header "Starting Azure SignalR Emulator on port 8888"

$signalrTool = dotnet tool list -g 2>$null | Select-String "microsoft.azure.signalr.emulator"
if (-not $signalrTool) {
    Write-Info "Installing Azure SignalR Emulator..."
    dotnet tool install -g Microsoft.Azure.SignalR.Emulator
    if ($LASTEXITCODE -ne 0) {
        Write-Warn "Failed to install SignalR Emulator. SignalR features will not work locally."
        Write-Warn "Install manually: dotnet tool install -g Microsoft.Azure.SignalR.Emulator"
    }
}

$signalrCmd = Get-Command asrs-emulator -ErrorAction SilentlyContinue
if ($signalrCmd) {
    # Create upstream settings so the emulator knows about the Functions host
    $signalrSettingsDir = Join-Path $root ".signalr-emulator"
    if (-not (Test-Path $signalrSettingsDir)) { New-Item -ItemType Directory -Path $signalrSettingsDir | Out-Null }

    $signalrSettings = Join-Path $signalrSettingsDir "settings.json"
    @{
        Port = 8888
        AccessKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="
        Upstream = @{
            Templates = @(
                @{
                    UrlTemplate  = "http://localhost:7071/runtime/webhooks/signalr"
                    HubPattern   = "*"
                    EventPattern = "*"
                    CategoryPattern = "*"
                }
            )
        }
    } | ConvertTo-Json -Depth 4 | Set-Content $signalrSettings

    $signalrScript = @"
`$Host.UI.RawUI.WindowTitle = 'Atlas SignalR Emulator :8888'
Write-Host '=== Azure SignalR Emulator (port 8888) ===' -ForegroundColor Cyan
Write-Host 'Connection: Endpoint=http://localhost;Port=8888;AccessKey=...;Version=1.0;' -ForegroundColor Yellow
Write-Host ''
asrs-emulator start --settings '$signalrSettings'
"@

    $signalrProc = Start-Process pwsh -ArgumentList "-NoExit", "-Command", $signalrScript -PassThru
    $trackedPids += $signalrProc.Id
    Write-Ok "SignalR Emulator started (PID: $($signalrProc.Id))"
} else {
    Write-Warn "asrs-emulator command not found. SignalR features will not work."
    Write-Warn "Install: dotnet tool install -g Microsoft.Azure.SignalR.Emulator"
}

# ---- Start Functions (port 7071) ----------------------------
Write-Header "Starting Atheres.Atlas.Functions on port 7071"

$debugFlag = if ($WaitDebugger) { "--dotnet-isolated-debug" } else { "" }

$funcScript = @"
`$Host.UI.RawUI.WindowTitle = 'Atlas Functions :7071'
`$env:ServiceBusConnection = '$sbEmulatorConn'
Set-Location '$funcDir'
Write-Host '=== Atheres.Atlas.Functions (port 7071) ===' -ForegroundColor Cyan
Write-Host 'Attach debugger to this process for breakpoints.' -ForegroundColor Yellow
Write-Host ''
func start --port 7071 $debugFlag
"@

$funcProc = Start-Process pwsh -ArgumentList "-NoExit", "-Command", $funcScript -PassThru
$trackedPids += $funcProc.Id
Write-Ok "Functions started (PID: $($funcProc.Id))"

# ---- Start Auth Functions (port 7072) -----------------------
Write-Header "Starting Atheres.Atlas.Auth.Functions on port 7072"

$authScript = @"
`$Host.UI.RawUI.WindowTitle = 'Atlas Auth :7072'
Set-Location '$authDir'
Write-Host '=== Atheres.Atlas.Auth.Functions (port 7072) ===' -ForegroundColor Cyan
Write-Host 'Attach debugger to this process for breakpoints.' -ForegroundColor Yellow
Write-Host ''
func start --port 7072 $debugFlag
"@

$authProc = Start-Process pwsh -ArgumentList "-NoExit", "-Command", $authScript -PassThru
$trackedPids += $authProc.Id
Write-Ok "Auth Functions started (PID: $($authProc.Id))"

# ---- Start Frontend (port 3000) -----------------------------
if (-not $NoFrontend) {
    Write-Header "Starting React frontend on port 3000"

    $feDir = Join-Path $root "Atheres.Atlas.FrontEnd"

    # Ensure node_modules exist
    if (-not (Test-Path (Join-Path $feDir "node_modules"))) {
        Write-Info "Installing npm dependencies..."
        Push-Location $feDir
        npm install
        Pop-Location
    }

    $feScript = @"
`$Host.UI.RawUI.WindowTitle = 'Atlas Frontend :3000'
Set-Location '$feDir'
Write-Host '=== Atheres.Atlas.FrontEnd (port 3000) ===' -ForegroundColor Cyan
Write-Host 'Vite dev server with HMR — proxying /api to localhost:7071' -ForegroundColor Yellow
Write-Host ''
npm run dev
"@

    $feProc = Start-Process pwsh -ArgumentList "-NoExit", "-Command", $feScript -PassThru
    $trackedPids += $feProc.Id
    Write-Ok "Frontend started (PID: $($feProc.Id))"

    # ---- Start Demo App (port 3001) ----
    Write-Header "Starting Demo app on port 3001"

    $demoDir = Join-Path $root "Atheres.Atlas.Demo"

    if (-not (Test-Path (Join-Path $demoDir "node_modules"))) {
        Write-Info "Installing demo npm dependencies..."
        Push-Location $demoDir
        npm install
        Pop-Location
    }

    $demoScript = @"
`$Host.UI.RawUI.WindowTitle = 'Atlas Demo :3001'
Set-Location '$demoDir'
Write-Host '=== Atheres.Atlas.Demo (port 3001) ===' -ForegroundColor Cyan
Write-Host 'Demo order generator — separate from main application' -ForegroundColor Yellow
Write-Host ''
npm run dev
"@

    $demoProc = Start-Process pwsh -ArgumentList "-NoExit", "-Command", $demoScript -PassThru
    $trackedPids += $demoProc.Id
    Write-Ok "Demo app started (PID: $($demoProc.Id))"
}

# ---- Save PIDs for -Stop ------------------------------------
$trackedPids | Out-File $pidFile -Force
Write-Info "Process IDs saved to .debug-pids"

# ---- Quick health check (non-blocking) ----------------------
if (-not $WaitDebugger) {
    Write-Header "Checking Function hosts"
    Write-Info "Giving hosts a few seconds to start..."
    Start-Sleep 5

    foreach ($svc in @(
        @{ Name = "Functions";      Url = "http://localhost:7071/api/query/orders" },
        @{ Name = "Auth Functions";  Url = "http://localhost:7072/api/auth/me" }
    )) {
        try {
            $null = Invoke-WebRequest -Uri $svc.Url -UseBasicParsing -TimeoutSec 3 -ErrorAction Stop
            Write-Ok "$($svc.Name) is responding."
        } catch {
            Write-Warn "$($svc.Name) not responding yet — check its terminal window for errors."
        }
    }
}

# ---- Summary ------------------------------------------------
Write-Header "Atheres Atlas — Local Debug Environment"

Write-Host ""
Write-Host "  Infrastructure (Docker):"
Write-Host "    SQL Server       " -NoNewline; Write-Host "localhost:1433" -ForegroundColor Gray
Write-Host "    Azurite          " -NoNewline; Write-Host "localhost:10000-10002" -ForegroundColor Gray
Write-Host "    Service Bus      " -NoNewline; Write-Host "localhost:5672" -ForegroundColor Gray
Write-Host "    SignalR Emulator " -NoNewline; Write-Host "localhost:8888" -ForegroundColor Gray
Write-Host ""
Write-Host "  Applications (native — debuggable):"
Write-Host "    Functions API    " -NoNewline; Write-Host "http://localhost:7071/api" -ForegroundColor Green
Write-Host "    Auth API         " -NoNewline; Write-Host "http://localhost:7072/api" -ForegroundColor Green
if (-not $NoFrontend) {
    Write-Host "    Frontend         " -NoNewline; Write-Host "http://localhost:3000" -ForegroundColor Green
    Write-Host "    Demo App         " -NoNewline; Write-Host "http://localhost:3001" -ForegroundColor Yellow
}
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

if ($WaitDebugger) {
    Write-Host "  *** WAITING FOR DEBUGGER ***" -ForegroundColor Yellow
    Write-Host "  Function hosts are paused — attach a debugger to continue." -ForegroundColor Yellow
    Write-Host ""
}

Write-Host "  Debugging:" -ForegroundColor Cyan
Write-Host "    VS Code    : Use 'Attach to .NET Functions' launch config, or"
Write-Host "                 Ctrl+Shift+P > '.NET: Attach' > select func process"
Write-Host "    Visual Studio: Debug > Attach to Process > search 'dotnet'"
Write-Host ""
Write-Host "  Commands:" -ForegroundColor Cyan
Write-Host "    .\deploy-debug.ps1 -Stop            Stop all debug services"
Write-Host "    .\deploy-debug.ps1 -SkipMigrations  Restart without re-migrating"
Write-Host "    docker compose logs -f sql           Tail SQL Server logs"
Write-Host ""

# Open browsers and credentials
if (-not $NoFrontend -and -not $WaitDebugger) {
    $credFile = Join-Path $root "CREDENTIALS.txt"
    if (Test-Path $credFile) { Start-Process $credFile }
    Start-Process "http://localhost:3000"
    Start-Process "http://localhost:3001"
}
