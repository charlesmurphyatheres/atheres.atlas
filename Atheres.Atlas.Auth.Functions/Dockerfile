# =============================================================
# Atheres.Atlas.Auth.Functions — Docker multi-stage build
# Build context: solution root  (docker compose build auth)
# =============================================================

# ---- Stage 1: build -------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore — layer-cache friendly
COPY Atheres.Atlas.Domain/Atheres.Atlas.Domain.csproj           Atheres.Atlas.Domain/
COPY Atheres.Atlas.Data/Atheres.Atlas.Data.csproj               Atheres.Atlas.Data/
COPY Atheres.Atlas.Auth.Functions/Atheres.Atlas.Auth.Functions.csproj  Atheres.Atlas.Auth.Functions/

RUN dotnet restore Atheres.Atlas.Auth.Functions/Atheres.Atlas.Auth.Functions.csproj

# Copy source and publish
COPY Atheres.Atlas.Domain/    Atheres.Atlas.Domain/
COPY Atheres.Atlas.Data/      Atheres.Atlas.Data/
COPY Atheres.Atlas.Auth.Functions/ Atheres.Atlas.Auth.Functions/

RUN dotnet publish Atheres.Atlas.Auth.Functions/Atheres.Atlas.Auth.Functions.csproj \
    -c Release \
    -o /publish \
    --no-restore

# ---- Stage 2: runtime -----------------------------------------------
FROM mcr.microsoft.com/azure-functions/dotnet-isolated:4-dotnet-isolated8.0 AS runtime
WORKDIR /home/site/wwwroot

COPY --from=build /publish .

ENV AzureWebJobsScriptRoot=/home/site/wwwroot \
    AzureFunctionsJobHost__Logging__Console__IsEnabled=true \
    FUNCTIONS_WORKER_RUNTIME=dotnet-isolated
