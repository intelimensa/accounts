#!/usr/bin/env bash
# Start, inspect and stop a throwaway copy of the Accounts app for manual or scripted testing.
#
# Everything the test server touches lives in ONE temporary directory it creates itself: its own
# SQLite database, release storage, firmware storage, log and pidfile. It never reads or writes the
# development database (accounts.db), releases/ or firmware-files/, and `stop` only ever deletes that
# one directory (and only if it carries this script's marker file). Use it instead of starting ad-hoc
# servers by hand: sharing the dev storage folders is how uploads get lost.
#
#   test-server.sh start    build, migrate, seed a test user (Staff + Manufacturer), start on a free port
#   test-server.sh stop     stop the server and delete its temp directory
#   test-server.sh status   is it running, where, and the login
#   test-server.sh logs     tail the server log
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
PROJECT_DIR="$REPO_ROOT/Intelimensa.Accounts"
APP_DLL="$PROJECT_DIR/bin/Debug/net10.0/Intelimensa.Accounts.dll"

# One fixed location, so there is at most one test server and `stop` never has to guess a path.
DIR_NAME="intelimensa-accounts-test"
DIR="${TMPDIR:-/tmp}"
DIR="${DIR%/}/$DIR_NAME"
MARKER="$DIR/.test-server-marker"
PIDFILE="$DIR/server.pid"
URLFILE="$DIR/url"
LOGFILE="$DIR/server.log"

TEST_EMAIL="test@example.com"
TEST_PASSWORD="correct-horse-battery"
FIRST_PORT=5399

die() { echo "test-server: $*" >&2; exit 1; }

need() { command -v "$1" >/dev/null 2>&1 || die "'$1' is required but not installed."; }

# The pid recorded for the running server, if (and only if) that process is still our server.
running_pid() {
  [ -f "$PIDFILE" ] || return 1
  local pid
  pid="$(cat "$PIDFILE")"
  [[ "$pid" =~ ^[0-9]+$ ]] || return 1
  # Check the command line, not just that the pid exists: pids get reused.
  ps -o command= -p "$pid" 2>/dev/null | grep -q "Intelimensa.Accounts.dll" || return 1
  echo "$pid"
}

# Delete the temp directory, but only if it is unmistakably ours.
remove_dir() {
  [ -d "$DIR" ] || return 0
  [ "$(basename "$DIR")" = "$DIR_NAME" ] || die "refusing to delete '$DIR': unexpected name."
  [ -f "$MARKER" ] || die "refusing to delete '$DIR': no marker file (not created by this script)."
  rm -rf "$DIR"
}

free_port() {
  python3 - "$FIRST_PORT" <<'PY'
import socket, sys
port = int(sys.argv[1])
while True:
    with socket.socket() as s:
        try:
            s.bind(("127.0.0.1", port))
            print(port)
            break
        except OSError:
            port += 1
PY
}

# Pull the antiforgery token out of a page, using/updating the cookie jar.
token() {
  curl -fsS -b "$DIR/jar" -c "$DIR/jar" "$1" \
    | grep -o 'name="__RequestVerificationToken"[^>]*value="[^"]*"' | head -1 \
    | sed 's/.*value="//;s/"$//'
}

cmd_start() {
  need dotnet; need sqlite3; need curl; need python3; need openssl

  if pid="$(running_pid)"; then
    die "already running (pid $pid, $(cat "$URLFILE" 2>/dev/null)). Run 'stop' first."
  fi
  # A directory without a live server is a leftover (crash, reboot): clear it.
  remove_dir
  mkdir -p "$DIR"
  : > "$MARKER"

  echo "Building..."
  (cd "$PROJECT_DIR" && dotnet build -v q --nologo 2>&1 | tail -3) || die "build failed."
  [ -f "$APP_DLL" ] || die "build output not found at $APP_DLL"

  local port; port="$(free_port)"
  local url="http://127.0.0.1:$port"

  # Everything the app reads that could point at real data is set here, explicitly.
  export ASPNETCORE_ENVIRONMENT=Development
  export ASPNETCORE_URLS="$url"
  export ConnectionStrings__DefaultConnection="Data Source=$DIR/accounts.db"
  export Releases__StoragePath="$DIR/releases"
  export Firmware__StoragePath="$DIR/firmware-files"
  export Jwt__SigningKey; Jwt__SigningKey="$(openssl rand -base64 64 | tr -d '\n')"
  export Logging__LogLevel__Default=Warning

  echo "Migrating a fresh database..."
  (cd "$PROJECT_DIR" && dotnet ef database update --no-build >/dev/null 2>&1) \
    || { remove_dir; die "migration failed (run 'dotnet tool restore' once?)."; }

  # Run the dll directly (not 'dotnet run'): one process, so the recorded pid is the server itself
  # and 'stop' can't leave a child behind.
  # (The '&' must apply to dotnet alone, so '$!' is the server's pid and not a wrapper subshell's.)
  (cd "$PROJECT_DIR"; nohup dotnet "$APP_DLL" >"$LOGFILE" 2>&1 & echo $! >"$PIDFILE")
  echo "$url" > "$URLFILE"

  local i
  for i in $(seq 1 60); do
    if curl -fsS -o /dev/null "$url/" 2>/dev/null; then break; fi
    running_pid >/dev/null || { tail -20 "$LOGFILE" >&2; remove_dir; die "the server exited during startup."; }
    sleep 0.5
  done
  curl -fsS -o /dev/null "$url/" 2>/dev/null || { cmd_stop >/dev/null; die "the server did not come up in 30s."; }

  echo "Seeding test user..."
  local t
  t="$(token "$url/Account/Register")"
  curl -fsS -o /dev/null -b "$DIR/jar" -c "$DIR/jar" \
    --data-urlencode "__RequestVerificationToken=$t" \
    --data-urlencode "Input.Email=$TEST_EMAIL" \
    --data-urlencode "Input.Password=$TEST_PASSWORD" \
    --data-urlencode "Input.ConfirmPassword=$TEST_PASSWORD" \
    "$url/Account/Register" || { cmd_stop >/dev/null; die "could not register the test user."; }
  sqlite3 "$DIR/accounts.db" "INSERT INTO AspNetUserRoles (UserId, RoleId)
    SELECT u.Id, r.Id FROM AspNetUsers u, AspNetRoles r
    WHERE u.Email = '$TEST_EMAIL' AND r.Name IN ('Staff', 'Manufacturer');"
  rm -f "$DIR/jar"

  echo
  echo "Test server is up."
  print_info
}

print_info() {
  echo "  URL        $(cat "$URLFILE")"
  echo "  login      $TEST_EMAIL / $TEST_PASSWORD   (roles: Staff, Manufacturer)"
  echo "  data dir   $DIR   (database, releases, firmware-files: all throwaway)"
  echo "  log        $LOGFILE"
  echo "  stop with  scripts/test-server.sh stop"
}

cmd_stop() {
  local pid=""
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
    echo "Stopped test server (pid $pid)."
  else
    echo "No test server running."
  fi
  if [ -d "$DIR" ]; then
    remove_dir
    echo "Removed $DIR."
  fi
}

cmd_status() {
  if pid="$(running_pid)"; then
    echo "Running (pid $pid)."
    print_info
  elif [ -d "$DIR" ]; then
    echo "Not running, but a leftover directory exists: $DIR (run 'stop' to remove it)."
  else
    echo "Not running."
  fi
}

cmd_logs() {
  [ -f "$LOGFILE" ] || die "no log (not started)."
  tail -n 50 -f "$LOGFILE"
}

case "${1:-}" in
  start)  cmd_start ;;
  stop)   cmd_stop ;;
  status) cmd_status ;;
  logs)   cmd_logs ;;
  *)
    sed -n '2,13p' "$0" | sed 's/^# \{0,1\}//'
    exit 1
    ;;
esac
