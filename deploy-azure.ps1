# =============================================================
# deploy-azure.ps1 — Provision the full Atheres Atlas stack
#                     in Azure with SSL on www.atlasdeliver.com
#
# Prerequisites:
#   - Azure CLI installed (az)
#   - .NET 8 SDK (for EF migrations)
#   - Node 20+ / npm (for frontend build)
#   - Azure Functions Core Tools v4 (func)
#
# Usage:
#   1. Fill in the secrets below
#   2. .\deploy-azure.ps1
# =============================================================
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# =============================================================
# 1. CONFIGURATION — fill in before running
# =============================================================

# Load secrets from .env (git-ignored)
$_envFile = Join-Path $PSScriptRoot ".env"
$_envVars = @{}
if (Test-Path $_envFile) {
    Get-Content $_envFile | Where-Object { $_ -match '^\s*[^#]' -and $_ -match '=' } | ForEach-Object {
        $p = $_ -split '=', 2; $_envVars[$p[0].Trim()] = $p[1].Trim().Trim('"')
    }
}

$Location           = "centralus"
$ResourceGroup      = "rg-atheres-atlas"
$CustomDomain       = "www.atlasdeliver.com"
$PublicUrl           = "https://www.atlasdeliver.com"

# SQL
$SqlServerName      = "atlas-sql-atheres"
$SqlDbName          = "AtheresAtlas"
$SqlAdminUser       = "atlas-admin"
$SqlAdminPasswordPlain = if ($_envVars["MSSQL_SA_PASSWORD"]) { $_envVars["MSSQL_SA_PASSWORD"] } else {
    $sp = Read-Host -Prompt "SQL admin password" -AsSecureString
    [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($sp))
}

# Storage
$StorageAccount     = "statheresatlasprod"

# Service Bus
$ServiceBusNs       = "sb-atheres-atlas"

# SignalR
$SignalRName        = "atlas-signalr"

# Function Apps
$FuncPlanName       = "plan-atlas-functions"
$FuncAppMain        = "func-atlas-main"
$FuncAppAuth        = "func-atlas-auth"

# Frontend
$FrontendStorage    = "statlasfe0001"

# Front Door
$FrontDoorName      = "fd-atheres-atlas"
$FrontDoorProfile   = "fdp-atheres-atlas"

# Monitoring
$LogWorkspace       = "log-atheres-atlas"
$AppInsights        = "ai-atheres-atlas"

# Third-party keys (from .env)
$GoogleMapsApiKey    = if ($_envVars["GOOGLE_MAPS_API_KEY"]) { $_envVars["GOOGLE_MAPS_API_KEY"] } else { "<your-google-maps-api-key>" }
$SendGridApiKey      = if ($_envVars["SENDGRID_API_KEY"])    { $_envVars["SENDGRID_API_KEY"] }    else { "<your-sendgrid-api-key>" }
$SendGridFromEmail   = "noreply@atlasdeliver.com"
$SendGridFromName    = "Atheres Atlas Delivery"

# Demo app
$DemoStorage         = "statlasdemo0001"

# JWT — generate a fresh secret for production
$JwtSecretKey       = [Convert]::ToBase64String((1..32 | ForEach-Object { Get-Random -Maximum 256 }) -as [byte[]])
$JwtIssuer          = $PublicUrl
$JwtAudience        = $PublicUrl
$JwtExpiryMinutes   = "15"
$JwtRefreshDays     = "7"

# Seed admin (first run only — clear after initial deploy)
$SeedAdminEmail     = "charles.murphy@atheres.com"
$SeedAdminPassword  = ""

# =============================================================
# 2. LOGIN
# =============================================================

Write-Host "`n=== Logging in to Azure ===" -ForegroundColor Cyan
az login
az account set --subscription "171bf58a-ae31-415c-ad50-85ec2b520edb"
az account show --output table

# =============================================================
# 3. CLEAN UP PREVIOUS DEPLOYMENT (idempotent)
# =============================================================

$rgExists = az group exists --name $ResourceGroup 2>$null
if ($rgExists -eq "true") {
    Write-Host "`n=== Existing deployment found: $ResourceGroup ===" -ForegroundColor Yellow
    Write-Host "  Deleting resource group to ensure clean deployment..." -ForegroundColor Yellow
    az group delete --name $ResourceGroup --yes --no-wait --output none

    # Wait for deletion to complete
    Write-Host "  Waiting for deletion..." -ForegroundColor Gray
    $retries = 60
    while ($retries -gt 0) {
        $still = az group exists --name $ResourceGroup 2>$null
        if ($still -ne "true") { break }
        Start-Sleep 10
        Write-Host "." -NoNewline
        $retries--
    }
    Write-Host ""
    if ($retries -eq 0) {
        Write-Host "  [WARN] Resource group still deleting. Proceeding anyway..." -ForegroundColor Yellow
    } else {
        Write-Host "  [OK] Previous deployment cleaned up." -ForegroundColor Green
    }
}

# =============================================================
# 4. RESOURCE GROUP
# =============================================================

Write-Host "`n=== Creating resource group: $ResourceGroup ===" -ForegroundColor Cyan
az group create --name $ResourceGroup --location $Location --output none

# =============================================================
# 4. SQL SERVER + DATABASE
# =============================================================

Write-Host "`n=== Creating SQL Server: $SqlServerName ===" -ForegroundColor Cyan
az sql server create `
    --resource-group $ResourceGroup `
    --name $SqlServerName `
    --location $Location `
    --admin-user $SqlAdminUser `
    --admin-password $SqlAdminPasswordPlain `
    --output none

# Allow Azure services to connect
az sql server firewall-rule create `
    --resource-group $ResourceGroup `
    --server $SqlServerName `
    --name "AllowAzureServices" `
    --start-ip-address 0.0.0.0 `
    --end-ip-address 0.0.0.0 `
    --output none

# Allow current machine (for running migrations)
$myIp = (Invoke-RestMethod -Uri "https://api.ipify.org")
az sql server firewall-rule create `
    --resource-group $ResourceGroup `
    --server $SqlServerName `
    --name "LocalDev" `
    --start-ip-address $myIp `
    --end-ip-address $myIp `
    --output none

Write-Host "  Creating database: $SqlDbName" -ForegroundColor Gray
az sql db create `
    --resource-group $ResourceGroup `
    --server $SqlServerName `
    --name $SqlDbName `
    --service-objective S1 `
    --backup-storage-redundancy Local `
    --output none

$SqlConnectionString = "Server=tcp:$SqlServerName.database.windows.net,1433;Database=$SqlDbName;User Id=$SqlAdminUser;Password=$SqlAdminPasswordPlain;Encrypt=True;TrustServerCertificate=False;Connection Timeout=60;"

# =============================================================
# 5. STORAGE ACCOUNT (Functions runtime)
# =============================================================

Write-Host "`n=== Creating Storage Account: $StorageAccount ===" -ForegroundColor Cyan
az storage account create `
    --resource-group $ResourceGroup `
    --name $StorageAccount `
    --location $Location `
    --sku Standard_LRS `
    --kind StorageV2 `
    --min-tls-version TLS1_2 `
    --allow-blob-public-access false `
    --output none

$StorageConnStr = (az storage account show-connection-string `
    --resource-group $ResourceGroup `
    --name $StorageAccount `
    --query connectionString -o tsv)

# =============================================================
# 6. SERVICE BUS NAMESPACE + QUEUES
# =============================================================

Write-Host "`n=== Creating Service Bus: $ServiceBusNs ===" -ForegroundColor Cyan
az servicebus namespace create `
    --resource-group $ResourceGroup `
    --name $ServiceBusNs `
    --location $Location `
    --sku Basic `
    --output none

# Queue definitions
$queues = @(
    "atlas-orders-ingest",
    "atlas-routes-optimize",
    "atlas-confirmations-send",
    "atlas-confirmations-received",
    "atlas-reschedule",
    "atlas-notifications",
    "atlas-audit"
)

foreach ($q in $queues) {
    Write-Host "  Creating queue: $q" -ForegroundColor Gray
    az servicebus queue create `
        --resource-group $ResourceGroup `
        --namespace-name $ServiceBusNs `
        --name $q `
        --output none
}

$ServiceBusConnStr = (az servicebus namespace authorization-rule keys list `
    --resource-group $ResourceGroup `
    --namespace-name $ServiceBusNs `
    --name RootManageSharedAccessKey `
    --query primaryConnectionString -o tsv)

# =============================================================
# 7. AZURE SIGNALR SERVICE
# =============================================================

Write-Host "`n=== Creating SignalR Service: $SignalRName ===" -ForegroundColor Cyan
az signalr create `
    --resource-group $ResourceGroup `
    --name $SignalRName `
    --location $Location `
    --sku Free_F1 `
    --service-mode Serverless `
    --output none

$SignalRConnStr = (az signalr key list `
    --resource-group $ResourceGroup `
    --name $SignalRName `
    --query primaryConnectionString -o tsv)

# =============================================================
# 8. LOG ANALYTICS + APPLICATION INSIGHTS
# =============================================================

Write-Host "`n=== Creating Application Insights ===" -ForegroundColor Cyan
az monitor log-analytics workspace create `
    --resource-group $ResourceGroup `
    --workspace-name $LogWorkspace `
    --location $Location `
    --output none

$LogWorkspaceId = (az monitor log-analytics workspace show `
    --resource-group $ResourceGroup `
    --workspace-name $LogWorkspace `
    --query id -o tsv)

az monitor app-insights component create `
    --resource-group $ResourceGroup `
    --app $AppInsights `
    --location $Location `
    --workspace $LogWorkspaceId `
    --output none

$AppInsightsConnStr = (az monitor app-insights component show `
    --resource-group $ResourceGroup `
    --app $AppInsights `
    --query connectionString -o tsv)

# =============================================================
# 9. FUNCTION APP — MAIN (6 agents)
# =============================================================

Write-Host "`n=== Creating Function App: $FuncAppMain ===" -ForegroundColor Cyan
az functionapp create `
    --resource-group $ResourceGroup `
    --name $FuncAppMain `
    --consumption-plan-location $Location `
    --storage-account $StorageAccount `
    --runtime dotnet-isolated `
    --runtime-version 8 `
    --functions-version 4 `
    --os-type Linux `
    --app-insights $AppInsights `
    --output none

Write-Host "  Configuring app settings..." -ForegroundColor Gray
az functionapp config appsettings set `
    --resource-group $ResourceGroup `
    --name $FuncAppMain `
    --settings `
        "SqlConnectionString=$SqlConnectionString" `
        "ServiceBusConnection=$ServiceBusConnStr" `
        "AzureSignalRConnectionString=$SignalRConnStr" `
        "GoogleMapsApiKey=$GoogleMapsApiKey" `
        "SendGridApiKey=$SendGridApiKey" `
        "SendGridFromEmail=$SendGridFromEmail" `
        "SendGridFromName=$SendGridFromName" `
        "JwtSecretKey=$JwtSecretKey" `
        "JwtIssuer=$JwtIssuer" `
        "JwtAudience=$JwtAudience" `
        "FunctionHostUrl=$PublicUrl" `
        "APPLICATIONINSIGHTS_CONNECTION_STRING=$AppInsightsConnStr" `
        "ASPNETCORE_ENVIRONMENT=Production" `
    --output none

# Enable CORS for the frontend
az functionapp cors add `
    --resource-group $ResourceGroup `
    --name $FuncAppMain `
    --allowed-origins $PublicUrl `
    --output none

# =============================================================
# 10. FUNCTION APP — AUTH (Identity / JWT)
# =============================================================

Write-Host "`n=== Creating Function App: $FuncAppAuth ===" -ForegroundColor Cyan
az functionapp create `
    --resource-group $ResourceGroup `
    --name $FuncAppAuth `
    --consumption-plan-location $Location `
    --storage-account $StorageAccount `
    --runtime dotnet-isolated `
    --runtime-version 8 `
    --functions-version 4 `
    --os-type Linux `
    --app-insights $AppInsights `
    --output none

Write-Host "  Configuring app settings..." -ForegroundColor Gray
az functionapp config appsettings set `
    --resource-group $ResourceGroup `
    --name $FuncAppAuth `
    --settings `
        "SqlConnectionString=$SqlConnectionString" `
        "JwtSecretKey=$JwtSecretKey" `
        "JwtIssuer=$JwtIssuer" `
        "JwtAudience=$JwtAudience" `
        "JwtExpiryMinutes=$JwtExpiryMinutes" `
        "JwtRefreshExpiryDays=$JwtRefreshDays" `
        "SeedAdminEmail=$SeedAdminEmail" `
        "SeedAdminPassword=$SeedAdminPassword" `
        "APPLICATIONINSIGHTS_CONNECTION_STRING=$AppInsightsConnStr" `
        "ASPNETCORE_ENVIRONMENT=Production" `
    --output none

az functionapp cors add `
    --resource-group $ResourceGroup `
    --name $FuncAppAuth `
    --allowed-origins $PublicUrl `
    --output none

# =============================================================
# 11. FRONTEND STORAGE (Static Website)
# =============================================================

Write-Host "`n=== Creating Frontend Storage: $FrontendStorage ===" -ForegroundColor Cyan
az storage account create `
    --resource-group $ResourceGroup `
    --name $FrontendStorage `
    --location $Location `
    --sku Standard_LRS `
    --kind StorageV2 `
    --min-tls-version TLS1_2 `
    --output none

az storage blob service-properties update `
    --account-name $FrontendStorage `
    --static-website `
    --index-document "index.html" `
    --404-document "index.html" `
    --output none

$FrontendOrigin = (az storage account show `
    --resource-group $ResourceGroup `
    --name $FrontendStorage `
    --query "primaryEndpoints.web" -o tsv).TrimEnd('/')

# =============================================================
# 12. DEMO APP STORAGE (Static Website)
# =============================================================

Write-Host "`n=== Creating Demo App Storage: $DemoStorage ===" -ForegroundColor Cyan
az storage account create `
    --resource-group $ResourceGroup `
    --name $DemoStorage `
    --location $Location `
    --sku Standard_LRS `
    --kind StorageV2 `
    --min-tls-version TLS1_2 `
    --output none

az storage blob service-properties update `
    --account-name $DemoStorage `
    --static-website `
    --index-document "index.html" `
    --404-document "index.html" `
    --output none

$DemoOrigin = (az storage account show `
    --resource-group $ResourceGroup `
    --name $DemoStorage `
    --query "primaryEndpoints.web" -o tsv).TrimEnd('/')

# =============================================================
# 13. AZURE FRONT DOOR (SSL + Custom Domain + Routing)
# =============================================================

Write-Host "`n=== Creating Front Door: $FrontDoorName ===" -ForegroundColor Cyan

# Create Front Door profile (Standard tier for managed SSL)
az afd profile create `
    --resource-group $ResourceGroup `
    --profile-name $FrontDoorProfile `
    --sku Standard_AzureFrontDoor `
    --output none

# ----- Endpoint -----
az afd endpoint create `
    --resource-group $ResourceGroup `
    --profile-name $FrontDoorProfile `
    --endpoint-name $FrontDoorName `
    --enabled-state Enabled `
    --output none

$FrontDoorHostname = (az afd endpoint show `
    --resource-group $ResourceGroup `
    --profile-name $FrontDoorProfile `
    --endpoint-name $FrontDoorName `
    --query hostName -o tsv)

# ----- Origin Groups -----

# Frontend origin group
az afd origin-group create `
    --resource-group $ResourceGroup `
    --profile-name $FrontDoorProfile `
    --origin-group-name "og-frontend" `
    --probe-request-type GET `
    --probe-protocol Https `
    --probe-path "/" `
    --probe-interval-in-seconds 120 `
    --sample-size 4 `
    --successful-samples-required 3 `
    --output none

$FrontendHost = ($FrontendOrigin -replace "https://", "")
az afd origin create `
    --resource-group $ResourceGroup `
    --profile-name $FrontDoorProfile `
    --origin-group-name "og-frontend" `
    --origin-name "origin-frontend" `
    --host-name $FrontendHost `
    --origin-host-header $FrontendHost `
    --http-port 80 `
    --https-port 443 `
    --priority 1 `
    --weight 1000 `
    --enabled-state Enabled `
    --output none

# Main Functions origin group
az afd origin-group create `
    --resource-group $ResourceGroup `
    --profile-name $FrontDoorProfile `
    --origin-group-name "og-functions-main" `
    --probe-request-type GET `
    --probe-protocol Https `
    --probe-path "/" `
    --probe-interval-in-seconds 120 `
    --sample-size 4 `
    --successful-samples-required 3 `
    --output none

az afd origin create `
    --resource-group $ResourceGroup `
    --profile-name $FrontDoorProfile `
    --origin-group-name "og-functions-main" `
    --origin-name "origin-func-main" `
    --host-name "$FuncAppMain.azurewebsites.net" `
    --origin-host-header "$FuncAppMain.azurewebsites.net" `
    --http-port 80 `
    --https-port 443 `
    --priority 1 `
    --weight 1000 `
    --enabled-state Enabled `
    --output none

# Auth Functions origin group
az afd origin-group create `
    --resource-group $ResourceGroup `
    --profile-name $FrontDoorProfile `
    --origin-group-name "og-functions-auth" `
    --probe-request-type GET `
    --probe-protocol Https `
    --probe-path "/" `
    --probe-interval-in-seconds 120 `
    --sample-size 4 `
    --successful-samples-required 3 `
    --output none

az afd origin create `
    --resource-group $ResourceGroup `
    --profile-name $FrontDoorProfile `
    --origin-group-name "og-functions-auth" `
    --origin-name "origin-func-auth" `
    --host-name "$FuncAppAuth.azurewebsites.net" `
    --origin-host-header "$FuncAppAuth.azurewebsites.net" `
    --http-port 80 `
    --https-port 443 `
    --priority 1 `
    --weight 1000 `
    --enabled-state Enabled `
    --output none

# ----- Routes (most specific first, all linked to default domain) -----

$routeDefs = @(
    @{ Name="route-signalr";     Group="og-functions-main"; Pattern="/api/negotiate/*" },
    @{ Name="route-auth";        Group="og-functions-auth"; Pattern="/api/auth/*" },
    @{ Name="route-users";       Group="og-functions-auth"; Pattern="/api/users/*" },
    @{ Name="route-companies";   Group="og-functions-auth"; Pattern="/api/companies/*" },
    @{ Name="route-warehouses";  Group="og-functions-auth"; Pattern="/api/warehouses/*" },
    @{ Name="route-hubs";        Group="og-functions-auth"; Pattern="/api/hubs/*" },
    @{ Name="route-api";         Group="og-functions-main"; Pattern="/api/*" },
    @{ Name="route-frontend";    Group="og-frontend";       Pattern="/*" }
)

foreach ($r in $routeDefs) {
    Write-Host "  Creating route: $($r.Name) -> $($r.Pattern)" -ForegroundColor Gray
    az afd route create `
        --resource-group $ResourceGroup `
        --profile-name $FrontDoorProfile `
        --endpoint-name $FrontDoorName `
        --route-name $r.Name `
        --origin-group $r.Group `
        --patterns-to-match $r.Pattern `
        --supported-protocols Https Http `
        --forwarding-protocol HttpsOnly `
        --link-to-default-domain Enabled `
        --output none
}

# ----- Custom Domain + Managed SSL Certificate -----

Write-Host "`n=== Adding custom domain: $CustomDomain ===" -ForegroundColor Cyan
az afd custom-domain create `
    --resource-group $ResourceGroup `
    --profile-name $FrontDoorProfile `
    --custom-domain-name "www-atlasdeliver" `
    --host-name $CustomDomain `
    --certificate-type ManagedCertificate `
    --minimum-tls-version TLS12 `
    --output none

# Associate the custom domain with the endpoint
az afd route update `
    --resource-group $ResourceGroup `
    --profile-name $FrontDoorProfile `
    --endpoint-name $FrontDoorName `
    --route-name "route-frontend" `
    --custom-domains "www-atlasdeliver" `
    --output none

# Get the validation token for DNS
$DomainValidation = (az afd custom-domain show `
    --resource-group $ResourceGroup `
    --profile-name $FrontDoorProfile `
    --custom-domain-name "www-atlasdeliver" `
    --query "validationProperties.validationToken" -o tsv)

# ----- Pause: wait for DNS records to be set -----
Write-Host ""
Write-Host "=============================================================" -ForegroundColor Yellow
Write-Host "  ACTION REQUIRED - Add DNS records in GoDaddy" -ForegroundColor Yellow
Write-Host "=============================================================" -ForegroundColor Yellow
Write-Host ""
Write-Host "  Go to GoDaddy DNS Management for atlasdeliver.com and add:" -ForegroundColor White
Write-Host ""
Write-Host "  Record 1:" -ForegroundColor Cyan
Write-Host "    Type:  CNAME"
Write-Host "    Name:  www"
Write-Host "    Value: $FrontDoorHostname"
Write-Host ""
Write-Host "  Record 2:" -ForegroundColor Cyan
Write-Host "    Type:  TXT"
Write-Host "    Name:  _dnsauth.www"
Write-Host "    Value: $DomainValidation"
Write-Host ""
Write-Host "  After adding both records, wait 1-2 minutes for propagation." -ForegroundColor Gray
Write-Host ""

Read-Host "  Press ENTER when DNS records are set (or Ctrl+C to abort)"

# Verify DNS propagation
Write-Host "`n  Checking DNS propagation..." -ForegroundColor Gray
$retries = 12
$validated = $false
while ($retries -gt 0 -and -not $validated) {
    $status = (az afd custom-domain show `
        --resource-group $ResourceGroup `
        --profile-name $FrontDoorProfile `
        --custom-domain-name "www-atlasdeliver" `
        --query "domainValidationState" -o tsv 2>$null)
    if ($status -eq "Approved") {
        $validated = $true
    } else {
        Write-Host "  Validation state: $status - waiting 30s..." -ForegroundColor Gray
        Start-Sleep 30
        $retries--
    }
}

if ($validated) {
    Write-Host "  DNS validated! SSL certificate is provisioning." -ForegroundColor Green
} else {
    Write-Host "  DNS not yet validated. SSL will provision automatically once records propagate." -ForegroundColor Yellow
    Write-Host "  Continuing with deployment..." -ForegroundColor Gray
}

# =============================================================
# 13. RUN EF MIGRATIONS
# =============================================================

Write-Host "`n=== Running EF Migrations ===" -ForegroundColor Cyan
$ProjectRoot = $PSScriptRoot

Push-Location $ProjectRoot
try {
    dotnet ef database update `
        --context AtlasDbContext `
        --startup-project Atheres.Atlas.Functions `
        --project Atheres.Atlas.Data `
        --connection $SqlConnectionString

    dotnet ef database update `
        --context AtlasIdentityDbContext `
        --startup-project Atheres.Atlas.Functions `
        --project Atheres.Atlas.Data `
        --connection $SqlConnectionString
}
finally {
    Pop-Location
}

# Import reference data
Write-Host "`n=== Importing Reference Data ===" -ForegroundColor Cyan
$importScript = Join-Path $ProjectRoot "import-data.ps1"
if (Test-Path $importScript) {
    pwsh -File $importScript -ConnectionString $SqlConnectionString
}

# Seed Secure Transport company
Write-Host "`n=== Seeding Secure Transport ===" -ForegroundColor Cyan
$seedScript = Join-Path $ProjectRoot "seed-secure-transport.ps1"
if (Test-Path $seedScript) {
    pwsh -File $seedScript -ConnectionString $SqlConnectionString -IdentityConnectionString $SqlConnectionString
}

# =============================================================
# 14. SUMMARY
# =============================================================

Write-Host "`n" -NoNewline
Write-Host "=============================================================" -ForegroundColor Green
Write-Host "  DEPLOYMENT COMPLETE" -ForegroundColor Green
Write-Host "=============================================================" -ForegroundColor Green

Write-Host "`nResource Group:      $ResourceGroup"
Write-Host "SQL Server:          $SqlServerName.database.windows.net"
Write-Host "Storage Account:     $StorageAccount"
Write-Host "Service Bus:         $ServiceBusNs.servicebus.windows.net"
Write-Host "SignalR:             $SignalRName"
Write-Host "Function App (Main): $FuncAppMain.azurewebsites.net"
Write-Host "Function App (Auth): $FuncAppAuth.azurewebsites.net"
Write-Host "Frontend:            $FrontendOrigin"
Write-Host "Demo App:            $DemoOrigin"
Write-Host "Front Door:          $FrontDoorHostname"
Write-Host "JWT Secret:          $JwtSecretKey"
Write-Host "Swagger:             https://$FuncAppMain.azurewebsites.net/api/swagger"

Write-Host "`n=============================================================" -ForegroundColor Yellow
Write-Host "  DNS RECORDS REQUIRED" -ForegroundColor Yellow
Write-Host "=============================================================" -ForegroundColor Yellow
Write-Host "`nAdd these records at your domain registrar for atlasdeliver.com:`n"
Write-Host "  TYPE   HOST   VALUE"
Write-Host "  ----   ----   -----"
Write-Host "  CNAME  www    $FrontDoorHostname"
Write-Host "  TXT    _dnsauth.www   $DomainValidation"
Write-Host ""
Write-Host "After DNS propagates, Front Door will auto-provision the SSL certificate." -ForegroundColor Gray
Write-Host "Certificate provisioning can take 10-15 minutes after validation." -ForegroundColor Gray

Write-Host "`n=============================================================" -ForegroundColor Yellow
Write-Host "  NEXT STEPS" -ForegroundColor Yellow
Write-Host "=============================================================" -ForegroundColor Yellow
Write-Host "
1. Add the DNS records above
2. Deploy Main Functions:
     cd Atheres.Atlas.Functions
     func azure functionapp publish $FuncAppMain
3. Deploy Auth Functions:
     cd Atheres.Atlas.Auth.Functions
     func azure functionapp publish $FuncAppAuth
4. Deploy Frontend:
     cd Atheres.Atlas.FrontEnd
     npm ci && npm run build
     az storage blob upload-batch -s dist -d `"`$web`" --account-name $FrontendStorage --overwrite
5. Deploy Demo App:
     cd Atheres.Atlas.Demo
     npm ci && npm run build
     az storage blob upload-batch -s dist -d `"`$web`" --account-name $DemoStorage --overwrite
6. Clear SeedAdminPassword after first deploy:
     az functionapp config appsettings set -g $ResourceGroup -n $FuncAppAuth --settings SeedAdminPassword=

  Users:
    SuperAdmin  charles.murphy@atheres.com  (password set during deploy)
    Admin       steven@gmail.com / Secure@1234567890  (Secure Transport)
" -ForegroundColor White
