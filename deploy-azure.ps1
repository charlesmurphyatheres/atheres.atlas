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

$Location           = "eastus2"
$ResourceGroup      = "rg-atheres-atlas"
$CustomDomain       = "www.atlasdeliver.com"
$PublicUrl           = "https://www.atlasdeliver.com"

# SQL
$SqlServerName      = "atlas-sql-$(Get-Random -Maximum 9999)"
$SqlDbName          = "AtheresAtlas"
$SqlAdminUser       = "atlas-admin"
$SqlAdminPassword   = Read-Host -Prompt "SQL admin password (min 8 chars, mixed case/digit/symbol)" -AsSecureString
$SqlAdminPasswordPlain = [Runtime.InteropServices.Marshal]::PtrToStringAuto(
    [Runtime.InteropServices.Marshal]::SecureStringToBSTR($SqlAdminPassword))

# Storage
$StorageAccount     = "statheresatlas$(Get-Random -Maximum 9999)"

# Service Bus
$ServiceBusNs       = "sb-atheres-atlas"

# SignalR
$SignalRName        = "atlas-signalr"

# Function Apps
$FuncPlanName       = "plan-atlas-functions"
$FuncAppMain        = "func-atlas-main"
$FuncAppAuth        = "func-atlas-auth"

# Frontend
$FrontendStorage    = "statlasfe$(Get-Random -Maximum 9999)"

# Front Door
$FrontDoorName      = "fd-atheres-atlas"
$FrontDoorProfile   = "fdp-atheres-atlas"

# Monitoring
$LogWorkspace       = "log-atheres-atlas"
$AppInsights        = "ai-atheres-atlas"

# Third-party keys — fill these in
$GoogleMapsApiKey    = "<your-google-maps-api-key>"
$SendGridApiKey      = "<your-sendgrid-api-key>"
$SendGridFromEmail   = "noreply@atlasdeliver.com"
$SendGridFromName    = "Atheres Atlas Delivery"
$TwilioAccountSid   = "<your-twilio-account-sid>"
$TwilioAuthToken    = "<your-twilio-auth-token>"
$TwilioFromNumber   = "<+15551234567>"

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
az account show --output table

# =============================================================
# 3. RESOURCE GROUP
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

# Queue definitions: Name, LockDuration (ISO 8601), MaxDeliveryCount, TTL, DeadLettering
$queues = @(
    @{ Name="atlas-orders-ingest";        Lock="PT60S";  MaxDel=10; TTL="PT1H";  DLQ=$true  },
    @{ Name="atlas-routes-optimize";      Lock="PT5M";   MaxDel=5;  TTL="PT1H";  DLQ=$true  },
    @{ Name="atlas-confirmations-send";   Lock="PT60S";  MaxDel=3;  TTL="PT1H";  DLQ=$true  },
    @{ Name="atlas-confirmations-received"; Lock="PT30S"; MaxDel=5;  TTL="PT1H";  DLQ=$true  },
    @{ Name="atlas-reschedule";           Lock="PT5M";   MaxDel=5;  TTL="PT1H";  DLQ=$true  },
    @{ Name="atlas-notifications";        Lock="PT30S";  MaxDel=3;  TTL="PT10M"; DLQ=$false },
    @{ Name="atlas-audit";                Lock="PT60S";  MaxDel=10; TTL="PT1H";  DLQ=$true  }
)

foreach ($q in $queues) {
    Write-Host "  Creating queue: $($q.Name)" -ForegroundColor Gray
    az servicebus queue create `
        --resource-group $ResourceGroup `
        --namespace-name $ServiceBusNs `
        --name $q.Name `
        --lock-duration $q.Lock `
        --max-delivery-count $q.MaxDel `
        --default-message-time-to-live $q.TTL `
        --dead-lettering-on-message-expiration $q.DLQ `
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
        "TwilioAccountSid=$TwilioAccountSid" `
        "TwilioAuthToken=$TwilioAuthToken" `
        "TwilioFromNumber=$TwilioFromNumber" `
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
# 12. AZURE FRONT DOOR (SSL + Custom Domain + Routing)
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
    --probe-interval-in-seconds 30 `
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
    --probe-path "/api/query/orders" `
    --probe-interval-in-seconds 30 `
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
    --probe-path "/api/auth/me" `
    --probe-interval-in-seconds 30 `
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

# ----- Routes (order matters — most specific first) -----

# /api/negotiate* → main functions (SignalR)
az afd route create `
    --resource-group $ResourceGroup `
    --profile-name $FrontDoorProfile `
    --endpoint-name $FrontDoorName `
    --route-name "route-signalr" `
    --origin-group "og-functions-main" `
    --patterns-to-match "/api/negotiate/*" `
    --supported-protocols Https `
    --forwarding-protocol HttpsOnly `
    --https-redirect Enabled `
    --output none

# /api/auth/* → auth functions
az afd route create `
    --resource-group $ResourceGroup `
    --profile-name $FrontDoorProfile `
    --endpoint-name $FrontDoorName `
    --route-name "route-auth" `
    --origin-group "og-functions-auth" `
    --patterns-to-match "/api/auth/*" `
    --supported-protocols Https `
    --forwarding-protocol HttpsOnly `
    --https-redirect Enabled `
    --output none

# /api/* → main functions
az afd route create `
    --resource-group $ResourceGroup `
    --profile-name $FrontDoorProfile `
    --endpoint-name $FrontDoorName `
    --route-name "route-api" `
    --origin-group "og-functions-main" `
    --patterns-to-match "/api/*" `
    --supported-protocols Https `
    --forwarding-protocol HttpsOnly `
    --https-redirect Enabled `
    --output none

# /* → frontend (catch-all, must be last)
az afd route create `
    --resource-group $ResourceGroup `
    --profile-name $FrontDoorProfile `
    --endpoint-name $FrontDoorName `
    --route-name "route-frontend" `
    --origin-group "og-frontend" `
    --patterns-to-match "/*" `
    --supported-protocols Https Http `
    --forwarding-protocol HttpsOnly `
    --https-redirect Enabled `
    --output none

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
Write-Host "Front Door:          $FrontDoorHostname"
Write-Host "JWT Secret:          $JwtSecretKey"

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
5. Clear SeedAdminPassword after first deploy:
     az functionapp config appsettings set -g $ResourceGroup -n $FuncAppAuth --settings SeedAdminPassword=
" -ForegroundColor White
