#!/usr/bin/env bash
# =============================================================
# Atheres Atlas — Docker Deployment Script  (Linux / macOS / WSL)
#
# Usage:
#   ./deploy.sh                          full deploy (build + start)
#   ./deploy.sh --build                  force rebuild all images before starting
#   ./deploy.sh --no-cache               rebuild without Docker layer cache
#   ./deploy.sh --push [REGISTRY]        push built images to a registry
#                                          REGISTRY defaults to $DOCKER_REGISTRY env var
#                                          e.g. --push myacr.azurecr.io/atheres
#   ./deploy.sh --service <name>         build + restart a single service
#                                          e.g. --service auth
#   ./deploy.sh --restart <name>         restart a running service (no rebuild)
#   ./deploy.sh --status                 show container status + ports
#   ./deploy.sh --down                   stop all containers (keep volumes)
#   ./deploy.sh --clean                  stop + delete all containers & volumes
#   ./deploy.sh --logs [service]         tail logs (optionally for one service)
# =============================================================

set -euo pipefail

# Anchor cwd to the repo root (parent of scripts/) so docker-compose.yml,
# .env, and every relative path below resolves regardless of where this
# script was invoked from.
cd "$(dirname "${BASH_SOURCE[0]}")/.."

# ---- Constants -----------------------------------------------
COMPOSE_PROJECT="atheres-atlas"
IMAGES=(migrations functions auth frontend)
ALL_SERVICES=(sql azurite servicebus-sql servicebus migrations functions auth frontend nginx)

# ---- Colours -------------------------------------------------
RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'
CYAN='\033[0;36m'; BOLD='\033[1m'; NC='\033[0m'

info()    { echo -e "${CYAN}[INFO]${NC}  $*"; }
ok()      { echo -e "${GREEN}[OK]${NC}    $*"; }
warn()    { echo -e "${YELLOW}[WARN]${NC}  $*"; }
err()     { echo -e "${RED}[ERROR]${NC} $*" >&2; }
header()  { echo -e "\n${BOLD}${CYAN}=== $* ===${NC}"; }
step()    { echo -e "${BOLD}--- $*${NC}"; }
elapsed() { echo -e "  ${NC}(${BOLD}$(($(date +%s) - START_TIME))s${NC} elapsed)"; }

START_TIME=$(date +%s)

# ---- Utility functions ---------------------------------------

# tcp_probe HOST PORT — returns 0 if port is open, 1 otherwise
tcp_probe() {
  local host="$1" port="$2"
  if $NC_AVAILABLE; then
    nc -z "$host" "$port" > /dev/null 2>&1
  else
    # bash built-in /dev/tcp fallback (works on bash 4+)
    (echo > /dev/tcp/"$host"/"$port") > /dev/null 2>&1
  fi
}

# wait_http LABEL URL MAX_RETRIES SLEEP_SEC
wait_http() {
  local label="$1" url="$2" retries="$3" delay="$4"
  step "Waiting for ${label} at ${url}..."
  local ready=false
  while [[ $retries -gt 0 ]]; do
    if curl -sf --max-time 3 "$url" > /dev/null 2>&1; then
      ready=true
      break
    fi
    retries=$((retries - 1))
    printf "."
    sleep "$delay"
  done
  echo ""
  if ! $ready; then
    err "${label} did not become healthy."
    docker compose logs --tail=30 "${label}"
    exit 1
  fi
  ok "${label} is healthy."
}

# ---- Argument parsing ----------------------------------------
BUILD_FLAG=""
NO_CACHE_FLAG=""
PUSH_REGISTRY=""
DO_PUSH=false
SINGLE_SERVICE=""
DO_RESTART=false
DO_STATUS=false
DO_DOWN=false
DO_CLEAN=false
TAIL_LOGS=false
TAIL_SERVICE=""

usage() {
  cat <<EOF
Usage: ./deploy.sh [options]

Options:
  (none)                    Full deploy — build images, run migrations, start all services
  --build                   Force rebuild all images (passes --build to docker compose)
  --no-cache                Rebuild without Docker layer cache
  --push [REGISTRY]         Push images after build to REGISTRY
                              (default: \$DOCKER_REGISTRY env var)
  --service <name>          Rebuild + restart a single service (e.g. auth, functions, frontend)
  --restart <name>          Restart a running service without rebuilding
  --status                  Show container status and exposed ports
  --down                    Stop all containers (volumes preserved)
  --clean                   Stop containers and DELETE all volumes (database data lost)
  --logs [service]          Tail logs for all services, or a named service
  -h, --help                Show this help
EOF
}

while [[ $# -gt 0 ]]; do
  case $1 in
    --build)     BUILD_FLAG="--build"; shift ;;
    --no-cache)  NO_CACHE_FLAG="--no-cache"; BUILD_FLAG="--build"; shift ;;
    --push)
      DO_PUSH=true
      if [[ ${2:-} != "" && ${2:-} != --* ]]; then
        PUSH_REGISTRY="$2"; shift
      elif [[ -n "${DOCKER_REGISTRY:-}" ]]; then
        PUSH_REGISTRY="$DOCKER_REGISTRY"
      fi
      shift ;;
    --service)
      SINGLE_SERVICE="${2:?'--service requires a service name'}"; shift 2 ;;
    --restart)
      SINGLE_SERVICE="${2:?'--restart requires a service name'}"
      DO_RESTART=true; shift 2 ;;
    --status) DO_STATUS=true; shift ;;
    --down)   DO_DOWN=true;  shift ;;
    --clean)  DO_CLEAN=true; shift ;;
    --logs)
      TAIL_LOGS=true
      if [[ ${2:-} != "" && ${2:-} != --* ]]; then
        TAIL_SERVICE="$2"; shift
      fi
      shift ;;
    -h|--help) usage; exit 0 ;;
    *) err "Unknown option: $1"; usage; exit 1 ;;
  esac
done

# ---- Status --------------------------------------------------
if $DO_STATUS; then
  header "Atheres Atlas — Container Status"
  docker compose ps --format "table {{.Name}}\t{{.Status}}\t{{.Ports}}"
  exit 0
fi

# ---- Down ----------------------------------------------------
if $DO_DOWN; then
  header "Stopping Atheres Atlas"
  docker compose down
  ok "All containers stopped."
  exit 0
fi

# ---- Clean ---------------------------------------------------
if $DO_CLEAN; then
  header "Removing Atheres Atlas (containers + volumes)"
  warn "This will PERMANENTLY DELETE all database data!"
  read -r -p "Type 'yes' to confirm: " confirm
  [[ "$confirm" == "yes" ]] || { info "Aborted."; exit 0; }
  docker compose down --volumes --remove-orphans
  ok "Cleanup complete."
  exit 0
fi

# ---- Restart (no rebuild) ------------------------------------
if $DO_RESTART && [[ -n "$SINGLE_SERVICE" ]]; then
  header "Restarting $SINGLE_SERVICE"
  docker compose restart "$SINGLE_SERVICE"
  ok "$SINGLE_SERVICE restarted."
  exit 0
fi

# ---- Tail logs -----------------------------------------------
if $TAIL_LOGS && [[ -z "$SINGLE_SERVICE" && $# -eq 0 ]]; then
  if [[ -n "$TAIL_SERVICE" ]]; then
    docker compose logs -f "$TAIL_SERVICE"
  else
    docker compose logs -f
  fi
  exit 0
fi

# ==============================================================
# Full deploy (or single-service rebuild)
# ==============================================================

# ---- Prerequisites -------------------------------------------
header "Checking prerequisites"

MISSING_CMDS=0
for cmd in docker curl; do
  if ! command -v "$cmd" &>/dev/null; then
    err "Required command not found: $cmd"
    MISSING_CMDS=$((MISSING_CMDS + 1))
  fi
done
[[ $MISSING_CMDS -eq 0 ]] || exit 1

DOCKER_VER=$(docker version --format '{{.Server.Version}}' 2>/dev/null || echo "unknown")
info "Docker: $DOCKER_VER"

if ! docker compose version &>/dev/null; then
  err "Docker Compose v2 not found. Install Docker Desktop or the 'docker compose' CLI plugin."
  exit 1
fi
COMPOSE_VER=$(docker compose version --short 2>/dev/null || echo "unknown")
info "Docker Compose: $COMPOSE_VER"

# Check for nc — used in TCP health probes (optional; fallback to bash /dev/tcp)
NC_AVAILABLE=false
command -v nc &>/dev/null && NC_AVAILABLE=true

ok "Prerequisites satisfied."

# ---- Environment file ----------------------------------------
header "Checking environment"

ENV_FILE=".env"
if [[ ! -f "$ENV_FILE" ]]; then
  if [[ -f ".env.docker" ]]; then
    warn ".env not found — copying .env.docker to .env"
    cp .env.docker "$ENV_FILE"
    warn "Open .env and replace all placeholder values before proceeding."
    read -r -p "Press Enter to continue with placeholder values, or Ctrl+C to abort..."
  else
    err "No .env or .env.docker file found."
    err "Copy .env.docker to .env and fill in your API keys."
    exit 1
  fi
fi

# Load env into current shell (so we can validate values)
set -a
# shellcheck disable=SC1090
source "$ENV_FILE"
set +a

REQUIRED_VARS=(
  MSSQL_SA_PASSWORD
  GOOGLE_MAPS_API_KEY
  SENDGRID_API_KEY
  TWILIO_ACCOUNT_SID
  AZURE_SIGNALR_CONNECTION
  JWT_SECRET_KEY
)

PLACEHOLDER_COUNT=0
for var in "${REQUIRED_VARS[@]}"; do
  val="${!var:-}"
  if [[ -z "$val" || "$val" == *"<your"* || "$val" == *"REPLACE"* ]]; then
    warn "  $var — not set or still a placeholder"
    PLACEHOLDER_COUNT=$((PLACEHOLDER_COUNT + 1))
  else
    ok "  $var — set"
  fi
done

# Extra check: JWT key must be ≥32 chars
JWT_LEN="${#JWT_SECRET_KEY}"
if [[ $JWT_LEN -lt 32 && $PLACEHOLDER_COUNT -eq 0 ]]; then
  warn "  JWT_SECRET_KEY is only $JWT_LEN chars — must be ≥32"
  PLACEHOLDER_COUNT=$((PLACEHOLDER_COUNT + 1))
fi

if [[ $PLACEHOLDER_COUNT -gt 0 ]]; then
  warn "$PLACEHOLDER_COUNT variable(s) need real values — some features will not work."
  read -r -p "Continue anyway? [y/N] " confirm
  [[ "$confirm" =~ ^[Yy]$ ]] || { info "Aborted."; exit 0; }
fi

# ---- Single-service deploy -----------------------------------
if [[ -n "$SINGLE_SERVICE" ]]; then
  header "Rebuilding and restarting: $SINGLE_SERVICE"
  # shellcheck disable=SC2086
  docker compose build $BUILD_FLAG $NO_CACHE_FLAG "$SINGLE_SERVICE"
  docker compose up -d --no-deps "$SINGLE_SERVICE"
  ok "$SINGLE_SERVICE rebuilt and restarted."
  elapsed
  if $TAIL_LOGS; then
    docker compose logs -f "$SINGLE_SERVICE"
  fi
  exit 0
fi

# ==============================================================
# FULL STACK DEPLOY
# ==============================================================

# ---- Pull base images (parallel) -----------------------------
header "Pulling base images"

BASE_IMAGES=(
  "mcr.microsoft.com/mssql/server:2022-latest"
  "mcr.microsoft.com/azure-storage/azurite:3.33.0"
  "mcr.microsoft.com/azure-sql-edge:latest"
  "mcr.microsoft.com/azure-messaging/servicebus-emulator:latest"
  "nginx:1.27-alpine"
  "node:20-alpine"
  "mcr.microsoft.com/dotnet/sdk:8.0"
  "mcr.microsoft.com/azure-functions/dotnet-isolated:4-dotnet-isolated8.0"
)

PULL_PIDS=()
for img in "${BASE_IMAGES[@]}"; do
  docker pull "$img" > /dev/null 2>&1 &
  PULL_PIDS+=($!)
done

info "Pulling ${#BASE_IMAGES[@]} images in parallel..."
PULL_ERRORS=0
for pid in "${PULL_PIDS[@]}"; do
  wait "$pid" || PULL_ERRORS=$((PULL_ERRORS + 1))
done

if [[ $PULL_ERRORS -gt 0 ]]; then
  warn "$PULL_ERRORS image pull(s) failed — may use existing cached versions."
else
  ok "All base images ready."
fi

# ---- Build application images --------------------------------
header "Building application images"

step "Building: migrations, functions, auth, frontend"
# shellcheck disable=SC2086
docker compose build \
  $BUILD_FLAG \
  $NO_CACHE_FLAG \
  --parallel \
  migrations functions auth frontend

ok "All application images built."
elapsed

# ---- Tag and push to registry --------------------------------
if $DO_PUSH; then
  header "Pushing images to registry: ${PUSH_REGISTRY:-<default>}"

  if [[ -z "$PUSH_REGISTRY" ]]; then
    err "--push requires a registry. Set \$DOCKER_REGISTRY or pass: --push myregistry.io/atheres"
    exit 1
  fi

  GIT_SHA=$(git rev-parse --short HEAD 2>/dev/null || echo "latest")
  IMAGE_TAG="${GIT_SHA}"

  PUSH_MAP=(
    "atheres-atlas-migrations:${PUSH_REGISTRY}/migrations"
    "atheres-atlas-functions:${PUSH_REGISTRY}/functions"
    "atheres-atlas-auth:${PUSH_REGISTRY}/auth"
    "atheres-atlas-frontend:${PUSH_REGISTRY}/frontend"
  )

  for entry in "${PUSH_MAP[@]}"; do
    LOCAL="${entry%%:*}"
    REMOTE="${entry##*:}"

    for tag in "$IMAGE_TAG" latest; do
      docker tag "$LOCAL" "${REMOTE}:${tag}"
      docker push "${REMOTE}:${tag}"
      ok "  Pushed ${REMOTE}:${tag}"
    done
  done

  ok "All images pushed to $PUSH_REGISTRY (tag: $IMAGE_TAG + latest)."
  elapsed
fi

# ---- Start infrastructure ------------------------------------
header "Starting infrastructure services"

step "Starting: sql, azurite, servicebus-sql, servicebus"
docker compose up -d sql azurite servicebus-sql servicebus

# ---- Wait: SQL Server ----------------------------------------
step "Waiting for SQL Server..."
SQL_RETRIES=40
SQL_READY=false
until $SQL_READY; do
  if docker compose exec -T sql \
       /opt/mssql-tools18/bin/sqlcmd \
       -S localhost -U sa -P "${MSSQL_SA_PASSWORD}" \
       -Q "SELECT 1" -b -No > /dev/null 2>&1; then
    SQL_READY=true
  else
    SQL_RETRIES=$((SQL_RETRIES - 1))
    if [[ $SQL_RETRIES -le 0 ]]; then
      err "SQL Server did not become healthy after 120 s."
      docker compose logs sql | tail -25
      exit 1
    fi
    printf "."
    sleep 3
  fi
done
echo ""
ok "SQL Server is ready."

# ---- Wait: Azurite -------------------------------------------
step "Waiting for Azurite (blob storage)..."
AZ_RETRIES=20
AZ_READY=false
until $AZ_READY; do
  if tcp_probe localhost 10000; then
    AZ_READY=true
  else
    AZ_RETRIES=$((AZ_RETRIES - 1))
    [[ $AZ_RETRIES -le 0 ]] && {
      err "Azurite did not start in time."
      docker compose logs azurite | tail -15
      exit 1
    }
    printf "."
    sleep 2
  fi
done
echo ""
ok "Azurite is ready."

# ---- Wait: Service Bus emulator ------------------------------
step "Waiting for Service Bus emulator..."
SB_RETRIES=40
SB_READY=false
until $SB_READY; do
  if tcp_probe localhost 5672; then
    SB_READY=true
  else
    SB_RETRIES=$((SB_RETRIES - 1))
    if [[ $SB_RETRIES -le 0 ]]; then
      err "Service Bus emulator did not start in time."
      docker compose logs servicebus | tail -25
      exit 1
    fi
    printf "."
    sleep 3
  fi
done
echo ""
ok "Service Bus emulator is ready."

# ---- Run EF Migrations ----------------------------------------
header "Running database migrations"

if ! docker compose run --rm migrations; then
  err "EF Core migrations failed."
  err "Inspect with: docker compose logs migrations"
  exit 1
fi
ok "Migrations applied."
elapsed

# ---- Start application services ------------------------------
header "Starting application services"

step "Starting: functions, auth, frontend, nginx"
docker compose up -d functions auth frontend nginx

# ---- Wait: functions -----------------------------------------
wait_http "functions" "http://localhost:7071/api/health" 40 3

# ---- Wait: auth ----------------------------------------------
wait_http "auth" "http://localhost:7072/api/auth/health" 40 3

# ---- Wait: nginx (overall health) ----------------------------
wait_http "nginx" "http://localhost/health" 30 2

# ---- Summary -------------------------------------------------
header "Atheres Atlas is running"

TOTAL_TIME=$(( $(date +%s) - START_TIME ))

echo ""
echo -e "  ${BOLD}Application${NC}       http://localhost"
echo -e "  ${BOLD}Auth API${NC}          http://localhost/api/auth"
echo -e "  ${BOLD}Functions API${NC}     http://localhost:7071/api   (direct)"
echo -e "  ${BOLD}Auth Functions${NC}    http://localhost:7072/api   (direct)"
echo -e "  ${BOLD}SQL Server${NC}        localhost:1433   (sa / \$MSSQL_SA_PASSWORD)"
echo -e "  ${BOLD}Service Bus${NC}       localhost:5672"
echo -e "  ${BOLD}Azurite Blob${NC}      localhost:10000"
echo ""
echo -e "  ${BOLD}Useful commands:${NC}"
echo -e "    ./deploy.sh --status                  → container status"
echo -e "    ./deploy.sh --service frontend        → rebuild & restart frontend only"
echo -e "    ./deploy.sh --service auth            → rebuild & restart auth only"
echo -e "    ./deploy.sh --restart nginx           → restart nginx (no rebuild)"
echo -e "    ./deploy.sh --logs auth               → tail auth logs"
echo -e "    ./deploy.sh --push myregistry.io/app  → tag & push images to registry"
echo -e "    ./deploy.sh --down                    → stop everything"
echo -e "    ./deploy.sh --clean                   → stop + wipe volumes"
echo ""
echo -e "  ${GREEN}Deployed in ${BOLD}${TOTAL_TIME}s${NC}"
echo ""

if $TAIL_LOGS; then
  info "Tailing logs (Ctrl+C to exit)..."
  if [[ -n "$TAIL_SERVICE" ]]; then
    docker compose logs -f "$TAIL_SERVICE"
  else
    docker compose logs -f
  fi
fi
