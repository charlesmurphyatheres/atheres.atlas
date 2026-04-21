#Requires -Version 7.0
# =============================================================
# Seed additional global users (SuperAdmins and test accounts)
# =============================================================

[CmdletBinding()]
param(
    [string]$AuthApiUrl = "http://localhost:7072"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Write-Header  { param($msg) Write-Host "`n=== $msg ===" -ForegroundColor Cyan }
function Write-Info    { param($msg) Write-Host "[INFO]  $msg"  -ForegroundColor Cyan }
function Write-Ok      { param($msg) Write-Host "[OK]    $msg"  -ForegroundColor Green }
function Write-Warn    { param($msg) Write-Host "[WARN]  $msg"  -ForegroundColor Yellow }

# Users to seed (all are SuperAdmins unless specified)
$usersToSeed = @(
    @{
        Email     = "ken@atheres.com"
        Password  = "Phone@3313059708"
        FirstName = "Ken"
        LastName  = "Administrator"
        Role      = "SuperAdmin"
    }
)

Write-Header "Seeding additional users"

# Login as bootstrap SuperAdmin to get a token for user creation
try {
    $loginBody = @{ email = "charles.murphy@atheres.com"; password = "Albeniz<18651909>" } | ConvertTo-Json
    $loginResp = Invoke-RestMethod -Uri "$AuthApiUrl/api/auth/login" -Method POST `
        -Body $loginBody -ContentType "application/json" -ErrorAction Stop
    $token = $loginResp.accessToken
} catch {
    Write-Warn "Could not log in as bootstrap SuperAdmin. Auth Functions may not be running."
    Write-Warn "Skipping user seeding. Error: $_"
    exit 0
}

$headers = @{ Authorization = "Bearer $token" }

foreach ($u in $usersToSeed) {
    try {
        $body = @{
            email     = $u.Email
            password  = $u.Password
            firstName = $u.FirstName
            lastName  = $u.LastName
            role      = $u.Role
        } | ConvertTo-Json

        $null = Invoke-RestMethod -Uri "$AuthApiUrl/api/auth/register" -Method POST `
            -Body $body -ContentType "application/json" -Headers $headers -ErrorAction Stop
        Write-Ok "Created $($u.Role): $($u.Email)"
    } catch {
        $msg = $_.ErrorDetails.Message
        if ($msg -match "already" -or $msg -match "duplicate") {
            Write-Warn "User $($u.Email) already exists. Skipping."
        } else {
            Write-Warn "Failed to create $($u.Email): $msg"
        }
    }
}

Write-Ok "User seeding complete."
