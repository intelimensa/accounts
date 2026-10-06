#!/usr/bin/env bash
# Start, stop and inspect the DEVELOPMENT Accounts server: the real dev database (accounts.db), the
# real releases/ and firmware-files/, the user-secrets JWT key, on the launch profile's port (5295).
# For throwaway testing use scripts/test-server.sh instead: it never touches any of this.
#
#   dev-server.sh start [--migrate]   build and start; refuses if migrations are pending unless --migrate
#   dev-server.sh stop                stop the server (never deletes any data)
#   dev-server.sh restart [--migrate] stop, then start
#   dev-server.sh status              is it running, where
#   dev-server.sh logs                tail the server log
#
# Only a process that is verifiably this repo's app is ever stopped; whatever else happens to hold the
# port is reported and left alone.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
PROJECT_DIR="$REPO_ROOT/Intelimensa.Accounts"
APP_DLL="$PROJECT_DIR/bin/Debug/net10.0/Intelimensa.Accounts.dll"
DEV_DB="$PROJECT_DIR/accounts.db"

# Pid and log live outside the repo; they hold no data, so losing them is harmless (stop falls back
# to finding the app by its port).
STATE_DIR="${TMPDIR:-/tmp}"
STATE_DIR="${STATE_DIR%/}/intelimensa-accounts-dev"
PIDFILE="$STATE_DIR/server.pid"
LOGFILE="$STATE_DIR/server.log"

die() { echo "dev-server: $*" >&2; exit 1; }

need() { command -v "$1" >/dev/null 2>&1 || die "'$1' is required but not installed."; }

command -v python3 >/dev/null 2>&1 || die "'python3' is required but not installed."

# The environment and URL the "http" launch profile gives `dotnet run`, read from launchSettings.json so
# the two never drift apart.
PROFILE_ENV="$(python3 - "$PROJECT_DIR/Properties/launchSettings.json" <<'PY'
import json, sys
profile = json.load(open(sys.argv[1]))["profiles"]["http"]
print(profile["environmentVariables"].get("ASPNETCORE_ENVIRONMENT", "Development"))
print(profile["applicationUrl"].split(";")[0])
PY
)"
DEV_ENVIRONMENT="$(echo "$PROFILE_ENV" | sed -n 1p)"
DEV_URL="$(echo "$PROFILE_ENV" | sed -n 2p)"
DEV_PORT="${DEV_URL##*:}"

# Is this pid unmistakably *this repo's* app (the dll we run, or the apphost `dotnet run` starts)?
is_our_app() {
  local cmd
  cmd="$(ps -o command= -p "$1" 2>/dev/null)" || return 1
  case "$cmd" in
    *"$PROJECT_DIR/bin/"*"Intelimensa.Accounts"*) return 0 ;;
    *) return 1 ;;
  esac
}

port_pids() { lsof -nP -tiTCP:"$DEV_PORT" -sTCP:LISTEN 2>/dev/null || true; }

# The pid of our running dev app: from the pidfile if valid, else whatever of ours holds the port (an
# instance started some other way, or after the pidfile was lost).
running_pid() {
  local pid
  if [ -f "$PIDFILE" ]; then
    pid="$(cat "$PIDFILE")"
    if [[ "$pid" =~ ^[0-9]+$ ]] && is_our_app "$pid"; then echo "$pid"; return 0; fi
  fi
  for pid in $(port_pids); do
    if is_our_app "$pid"; then echo "$pid"; return 0; fi
  done
  return 1
}

# Pending EF migrations, one per line (empty when the dev database is up to date). If the tool itself
# fails, that is an error, never "nothing pending".
pending_migrations() {
  local out
  out="$(cd "$PROJECT_DIR" && dotnet ef migrations list --no-build 2>&1)" \
    || { echo "$out" | tail -5 >&2; die "could not list migrations (run 'dotnet tool restore' once?)."; }
  echo "$out" | grep "(Pending)" | sed 's/ (Pending)//' || true
}

# Everything that can redirect the app away from the dev data is cleared; the app then reads only
# appsettings, user-secrets and the launch profile's values below. (An exported test-server variable in
# the shell would otherwise silently repoint this server.)
scrub_env() {
  unset ConnectionStrings__DefaultConnection Releases__StoragePath Firmware__StoragePath \
        Jwt__SigningKey Jwt__Issuer Jwt__Audience Logging__LogLevel__Default
  export ASPNETCORE_ENVIRONMENT="$DEV_ENVIRONMENT"
  export ASPNETCORE_URLS="$DEV_URL"
}

cmd_start() {
  local migrate=0
  [ "${1:-}" = "--migrate" ] && migrate=1
  need dotnet; need curl; need python3

  if pid="$(running_pid)"; then
    die "already running (pid $pid, $DEV_URL). Use 'restart' to pick up new code."
  fi
  # Something else on the port: say what, and don't touch it.
  for pid in $(port_pids); do
    die "port $DEV_PORT is held by pid $pid, which isn't this repo's app: $(ps -o command= -p "$pid" | cut -c1-120)"
  done

  echo "Building..."
  (cd "$PROJECT_DIR" && dotnet build -v q --nologo 2>&1 | tail -3) || die "build failed."
  [ -f "$APP_DLL" ] || die "build output not found at $APP_DLL"

  scrub_env

  local pending; pending="$(pending_migrations)"
  if [ -n "$pending" ]; then
    if [ "$migrate" -ne 1 ]; then
      echo "Pending migrations on the dev database:" >&2
      echo "$pending" | sed 's/^/  /' >&2
      die "the app would fail against this schema. Run 'start --migrate' (it backs the database up first)."
    fi
    need sqlite3
    if [ -f "$DEV_DB" ]; then
      local backup; backup="$PROJECT_DIR/accounts.pre-migrate-$(date +%Y%m%d-%H%M%S).db"
      # .backup is consistent even with the WAL in play, unlike copying the file.
      sqlite3 "$DEV_DB" ".backup '$backup'" || die "could not back up the dev database; not migrating."
      echo "Backed up the dev database to $backup"
    fi
    (cd "$PROJECT_DIR" && dotnet ef database update --no-build 2>&1 | tail -1) || die "migration failed."
  fi

  mkdir -p "$STATE_DIR"
  # Run the dll directly (not 'dotnet run'): one process, so the recorded pid is the server itself.
  # (The '&' must apply to dotnet alone, so '$!' is its pid and not a wrapper subshell's.)
  (cd "$PROJECT_DIR"; nohup dotnet "$APP_DLL" >"$LOGFILE" 2>&1 & echo $! >"$PIDFILE")

  local i
  for i in $(seq 1 60); do
    if curl -fsS -o /dev/null "$DEV_URL/" 2>/dev/null; then break; fi
    running_pid >/dev/null || { tail -20 "$LOGFILE" >&2; die "the server exited during startup."; }
    sleep 0.5
  done
  curl -fsS -o /dev/null "$DEV_URL/" 2>/dev/null || { cmd_stop >/dev/null; die "the server did not come up in 30s (see $LOGFILE)."; }

  echo "Dev server is up."
  print_info
}

print_info() {
  echo "  URL        $DEV_URL"
  echo "  data       $PROJECT_DIR/{accounts.db, releases/, firmware-files/}   (REAL dev data)"
  echo "  log        $LOGFILE"
  echo "  stop with  scripts/dev-server.sh stop"
}

cmd_stop() {
  local pid
  if pid="$(running_pid)"; then
    kill "$pid" 2>/dev/null || true
    local i
    for i in $(seq 1 20); do
      kill -0 "$pid" 2>/dev/null || break
      sleep 0.5
    done
    if kill -0 "$pid" 2>/dev/null; then
      echo "Server ignored SIGTERM; forcing."
      kill -9 "$pid" 2>/dev/null || true
    fi
    echo "Stopped dev server (pid $pid)."
  else
    echo "No dev server of this repo is running."
    for pid in $(port_pids); do
      echo "(Port $DEV_PORT is held by pid $pid, which isn't this repo's app, so it was left alone.)"
    done
  fi
  rm -f "$PIDFILE"
}

cmd_status() {
  local pid
  if pid="$(running_pid)"; then
    echo "Running (pid $pid)."
    print_info
  else
    echo "Not running."
    for pid in $(port_pids); do
      echo "Port $DEV_PORT is held by pid $pid, which isn't this repo's app: $(ps -o command= -p "$pid" | cut -c1-120)"
    done
  fi
}

cmd_logs() {
  [ -f "$LOGFILE" ] || die "no log (not started by this script)."
  tail -n 50 -f "$LOGFILE"
}

case "${1:-}" in
  start)   shift; cmd_start "${1:-}" ;;
  stop)    cmd_stop ;;
  restart) shift; cmd_stop; cmd_start "${1:-}" ;;
  status)  cmd_status ;;
  logs)    cmd_logs ;;
  *)
    sed -n '2,13p' "$0" | sed 's/^# \{0,1\}//'
    exit 1
    ;;
esac
