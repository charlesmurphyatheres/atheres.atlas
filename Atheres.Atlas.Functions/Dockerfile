# =============================================================
# Atheres.Atlas.Functions — Multi-stage Dockerfile
# Build context: solution root  (docker-compose sets this)
# =============================================================

# ---- Stage 1: Restore & Build --------------------------------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy project manifests first so Docker can cache the restore layer
COPY Atheres.Atlas.sln                                             ./
COPY Atheres.Atlas.Domain/Atheres.Atlas.Domain.csproj             Atheres.Atlas.Domain/
COPY Atheres.Atlas.Data/Atheres.Atlas.Data.csproj                 Atheres.Atlas.Data/
COPY Atheres.Atlas.Functions/Atheres.Atlas.Functions.csproj       Atheres.Atlas.Functions/

RUN dotnet restore Atheres.Atlas.Functions/Atheres.Atlas.Functions.csproj

# Copy all source
COPY Atheres.Atlas.Domain/   Atheres.Atlas.Domain/
COPY Atheres.Atlas.Data/     Atheres.Atlas.Data/
COPY Atheres.Atlas.Functions/ Atheres.Atlas.Functions/

RUN dotnet publish Atheres.Atlas.Functions/Atheres.Atlas.Functions.csproj \
        --configuration Release \
        --output /app/publish \
        --no-restore

# ---- Stage 2: Runtime ----------------------------------------
FROM mcr.microsoft.com/azure-functions/dotnet-isolated:4-dotnet-isolated8.0 AS runtime

ENV AzureWebJobsScriptRoot=/home/site/wwwroot \
    AzureFunctionsJobHost__Logging__Console__IsEnabled=true

WORKDIR /home/site/wwwroot
COPY --from=build /app/publish .

EXPOSE 80
