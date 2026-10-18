#!/usr/bin/env bash
# Deploys this repo's app to the production VPS (see docs/deployment-vps.md for how the server is set up).
#
#   deploy.sh plan                      read-only: what is deployed, what would change, which migrations are pending
#   deploy.sh deploy [--allow-dirty] [--yes]
#                                       publish, upload, then (service stopped) back up the database, apply the
#                                       pending migrations, switch to the new version and start it
#   deploy.sh rollback [--yes]          put the previous version back (and the pre-deploy database, if that deploy
#                                       changed the schema)
#   deploy.sh status                    what is deployed and whether the service is up
#   deploy.sh log [N]                   the last N (default 20) lines of the server's deploy log
#
# What it guarantees:
#   - The database is backed up (sqlite3 .backup) before every deploy, with the service stopped.
#   - Only the migrations the server hasn't run are applied, and with 'sqlite3 -bail': the first error stops
#     the script, the backup is restored and the old version is started again. Nothing is half-applied.
#   - The new version is uploaded next to the running one (app.new) and switched in by a rename, so the
#     service is down only for the backup, the migration and the rename. The old one is kept as app.prev.
#   - Every deploy and rollback appends a line to $DEPLOY_ROOT/backups/deploy.log on the server (time, who,
#     what, migrations, backup file, and whether the service came up). Nothing reads it back but 'log'.
#   - It refuses to deploy if the server has run a migration this checkout doesn't know (older code), or if
#     the app directory holds data (a database, uploaded releases or firmware) that the switch would lose.
#
# It needs ssh access as the deploy user, with sudo for 'systemctl' and 'journalctl' on the server (sudo may
# ask for a password once per run). First-time server setup is not this script's job: follow the runbook.
#
# Settings (environment variables, defaults are the production server):
#   DEPLOY_HOST        ssh target                         harald@accounts.intelimensa.com
#   DEPLOY_ROOT        directory holding app/ and data/   /var/www/accounts
#   DEPLOY_SERVICE     systemd unit                       intelimensa-accounts
#   DEPLOY_HEALTH_URL  checked on the server after start  http://127.0.0.1:5000/
#   DEPLOY_HEALTH_HOST Host header for that check         accounts.intelimensa.com
#                      (production's AllowedHosts rejects any other host with a 400)
set -euo pipefail

HOST="${DEPLOY_HOST:-harald@accounts.intelimensa.com}"
ROOT="${DEPLOY_ROOT:-/var/www/accounts}"
SERVICE="${DEPLOY_SERVICE:-intelimensa-accounts}"
HEALTH_URL="${DEPLOY_HEALTH_URL:-http://127.0.0.1:5000/}"
HEALTH_HOST="${DEPLOY_HEALTH_HOST:-accounts.intelimensa.com}"

REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT_DIR="$REPO_DIR/Intelimensa.Accounts"
REMOTE_SCRIPT="/tmp/intelimensa-deploy-$$.sh"
REMOTE_SQL="/tmp/intelimensa-migrate-$$.sql"

die() { echo "error: $*" >&2; exit 1; }
need() { command -v "$1" >/dev/null 2>&1 || die "'$1' is required but not installed."; }

WORK=""
cleanup() {
  [ -n "$WORK" ] && rm -rf "$WORK"
  ssh -o BatchMode=yes "$HOST" "rm -f '$REMOTE_SCRIPT' '$REMOTE_SQL'" >/dev/null 2>&1 || true
}

confirm() {
  local answer
  read -r -p "$1 [y/N] " answer
  [[ "$answer" =~ ^[Yy]$ ]] || die "cancelled."
}

# The half that runs on the server. Uploaded for each run, so the server never holds a stale copy.
write_remote_script() {
  cat >"$1" <<'REMOTE'
#!/usr/bin/env bash
set -euo pipefail
cmd="$1"; ROOT="$2"; SERVICE="$3"; HEALTH_URL="$4"; HEALTH_HOST="$5"; SQL="${6:-}"
APP="$ROOT/app"; NEW="$ROOT/app.new"; PREV="$ROOT/app.prev"
DB="$ROOT/data/accounts.db"; BACKUPS="$ROOT/backups"; STATE="$BACKUPS/last-deploy"
LOG="$BACKUPS/deploy.log"

die() { echo "error: $*" >&2; exit 1; }

healthy() {
  local i code
  for i in $(seq 1 30); do
    code="$(curl -s -o /dev/null -w '%{http_code}' --max-time 5 -H "Host: $HEALTH_HOST" "$HEALTH_URL" || true)"
    case "$code" in 2??|3??) return 0 ;; esac
    sleep 1
  done
  return 1
}

# One line per event: time, user, event, the commit now in $APP, then any extra fields.
log_event() {
  local event="$1"; shift
  local commit
  commit="$(sed -n 's/^commit=//p' "$APP/deploy-info.txt" 2>/dev/null | head -1)"
  mkdir -p "$BACKUPS"
  printf '%s %s %s commit="%s" %s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "${SUDO_USER:-${USER:-$(id -un)}}" "$event" "${commit:-unknown}" "$*" >>"$LOG"
}

# $1: event name for the log, $2: extra log fields, $3: what to say if the service doesn't come up.
start_and_check() {
  sudo systemctl start "$SERVICE"
  if healthy; then
    log_event "$1" "$2 service=up"
    echo "Service is up ($HEALTH_URL answers)."
  else
    log_event "$1" "$2 service=DOWN"
    sudo journalctl -u "$SERVICE" -n 40 --no-pager || true
    die "the service did not answer at $HEALTH_URL within 30s (log above). $3"
  fi
}

case "$cmd" in
  check)
    command -v sqlite3 >/dev/null || die "sqlite3 is not installed on the server."
    command -v curl >/dev/null || die "curl is not installed on the server."
    command -v rsync >/dev/null || die "rsync is not installed on the server."
    [ -d "$APP" ] || die "no app directory at $APP (first deploy? follow docs/deployment-vps.md)."
    [ -f "$DB" ] || die "no database at $DB (first deploy? follow docs/deployment-vps.md)."
    # Anything in the app directory that isn't the published app would be lost when versions are switched.
    stray="$(find "$APP" -maxdepth 1 \( -name '*.db' -o -name '*.db-wal' -o -name '*.db-shm' \
              -o -name releases -o -name firmware-files \) | head -5)"
    [ -z "$stray" ] || die "the app directory holds data that a deploy would lose; move it under $ROOT/data first:
$stray"
    sqlite3 "$DB" "select 'migration:' || MigrationId from __EFMigrationsHistory order by MigrationId"
    [ -f "$APP/deploy-info.txt" ] && sed 's/^/deployed:/' "$APP/deploy-info.txt"
    [ -f "$STATE" ] && sed 's/^/last:/' "$STATE"
    [ -d "$PREV" ] && echo "previous:yes"
    echo "service:$(systemctl is-active "$SERVICE" || true)"
    ;;

  apply)
    [ -d "$NEW" ] || die "nothing uploaded at $NEW."
    sudo -v
    mkdir -p "$BACKUPS"
    backup="$BACKUPS/accounts.pre-deploy-$(date -u +%Y%m%d-%H%M%S).db"

    echo "Stopping $SERVICE..."
    sudo systemctl stop "$SERVICE"
    # .backup is consistent even with the WAL in play, unlike copying the file.
    sqlite3 "$DB" ".backup '$backup'" || { sudo systemctl start "$SERVICE"; die "could not back up the database; nothing changed, service restarted."; }
    echo "Database backed up to $backup"

    migrated=0
    if [ -n "$SQL" ]; then
      echo "Applying migrations..."
      if ! sqlite3 -bail "$DB" <"$SQL"; then
        echo "Migration failed; restoring the database." >&2
        sqlite3 "$DB" ".restore '$backup'"
        sudo systemctl start "$SERVICE"
        die "migration failed. The database was restored and the old version is running again."
      fi
      migrated=1
    fi

    rm -rf "$PREV"
    mv "$APP" "$PREV"
    mv "$NEW" "$APP"
    printf 'backup=%s\nmigrated=%s\n' "$backup" "$migrated" >"$STATE"

    migrations=""
    [ -z "$SQL" ] || migrations="$(grep -o "VALUES ('[0-9]\{14\}_[A-Za-z0-9_]*'" "$SQL" | sed "s/VALUES ('//;s/'\$//" | paste -sd, -)"
    start_and_check deploy "migrations=${migrations:-none} backup=$backup" \
      "The new version is in place; run 'deploy.sh rollback' to go back."
    ;;

  rollback)
    [ -d "$PREV" ] || die "no previous version is kept at $PREV."
    [ -f "$STATE" ] || die "no record of the last deploy at $STATE."
    backup="$(sed -n 's/^backup=//p' "$STATE")"
    migrated="$(sed -n 's/^migrated=//p' "$STATE")"
    sudo -v

    echo "Stopping $SERVICE..."
    sudo systemctl stop "$SERVICE"
    if [ "$migrated" = "1" ]; then
      [ -f "$backup" ] || { sudo systemctl start "$SERVICE"; die "the pre-deploy backup $backup is gone; nothing changed, service restarted."; }
      # Keep what is being thrown away: rows written since the deploy live only here.
      discarded="$BACKUPS/accounts.pre-rollback-$(date -u +%Y%m%d-%H%M%S).db"
      sqlite3 "$DB" ".backup '$discarded'"
      sqlite3 "$DB" ".restore '$backup'"
      echo "Database restored from $backup (the replaced one is kept as $discarded)"
    fi

    rm -rf "$ROOT/app.rolledback"
    mv "$APP" "$ROOT/app.rolledback"
    mv "$PREV" "$APP"
    rm -f "$STATE"

    start_and_check rollback "database_restored=$([ "$migrated" = "1" ] && echo yes || echo no)" \
      "The previous version is in place; the one rolled back is at $ROOT/app.rolledback."
    ;;

  *) die "unknown remote command '$cmd'." ;;
esac
REMOTE
}

# Uploads the server half and runs its read-only check. Sets APPLIED (one migration id per line), DEPLOYED
# (commit line), LAST_MIGRATED, HAS_PREVIOUS and SERVICE_STATE.
remote_check() {
  need ssh; need scp
  WORK="$(mktemp -d)"
  trap cleanup EXIT
  write_remote_script "$WORK/remote.sh"
  scp -q "$WORK/remote.sh" "$HOST:$REMOTE_SCRIPT" || die "could not reach $HOST."

  local out
  out="$(ssh "$HOST" "bash '$REMOTE_SCRIPT' check '$ROOT' '$SERVICE' '$HEALTH_URL' '$HEALTH_HOST'")" || die "the server check failed."
  APPLIED="$(echo "$out" | sed -n 's/^migration://p')"
  DEPLOYED="$(echo "$out" | sed -n 's/^deployed:commit=//p')"
  LAST_MIGRATED="$(echo "$out" | sed -n 's/^last:migrated=//p')"
  HAS_PREVIOUS="$(echo "$out" | sed -n 's/^previous://p')"
  SERVICE_STATE="$(echo "$out" | sed -n 's/^service://p')"
  [ -n "$APPLIED" ] || die "the server's database has no migration history."
}

# Migration ids in this checkout, oldest first (the file names are the ids).
local_migrations() {
  find "$PROJECT_DIR/Migrations" -maxdepth 1 -name '[0-9]*_*.cs' ! -name '*.Designer.cs' \
    -exec basename {} .cs \; | sort
}

# Sets PENDING (ids to apply, oldest first) and LAST_APPLIED. Refuses anything that isn't "the server is
# simply behind this checkout".
work_out_migrations() {
  local ours unknown
  ours="$(local_migrations)"
  unknown="$(comm -13 <(echo "$ours") <(echo "$APPLIED"))"
  [ -z "$unknown" ] || die "the server has run migrations this checkout doesn't have (older code?):
$unknown"

  PENDING="$(comm -23 <(echo "$ours") <(echo "$APPLIED"))"
  LAST_APPLIED="$(echo "$APPLIED" | tail -1)"
  if [ -n "$PENDING" ] && [[ "$(echo "$PENDING" | head -1)" < "$LAST_APPLIED" ]]; then
    die "a pending migration is older than one the server has already run; sort that out by hand:
$PENDING"
  fi
}

print_state() {
  echo "Server:     $HOST ($ROOT), service $SERVICE is ${SERVICE_STATE:-unknown}"
  echo "Deployed:   ${DEPLOYED:-unknown (not deployed by this script yet)}"
  echo "Schema at:  $LAST_APPLIED"
}

cmd_status() {
  remote_check
  work_out_migrations
  print_state
  [ "$HAS_PREVIOUS" = "yes" ] && echo "Rollback:   a previous version is kept$([ "$LAST_MIGRATED" = "1" ] && echo " (rolling back also restores the pre-deploy database)")"
  return 0
}

describe_plan() {
  print_state
  echo "To deploy:  $(git -C "$REPO_DIR" log -1 --format='%h %s')$([ -n "$(git -C "$REPO_DIR" status --porcelain)" ] && echo "  + uncommitted changes")"
  if [ -n "$PENDING" ]; then
    echo "Migrations to apply:"
    echo "$PENDING" | sed 's/^/  /'
  else
    echo "Migrations to apply: none"
  fi
}

cmd_plan() {
  need git
  remote_check
  work_out_migrations
  describe_plan
}

cmd_deploy() {
  local allow_dirty=0 yes=0
  while [ $# -gt 0 ]; do
    case "$1" in
      --allow-dirty) allow_dirty=1 ;;
      --yes) yes=1 ;;
      *) die "unknown option '$1'." ;;
    esac
    shift
  done
  need git; need dotnet; need rsync

  if [ -n "$(git -C "$REPO_DIR" status --porcelain)" ] && [ "$allow_dirty" -ne 1 ]; then
    die "there are uncommitted changes. Commit them, or pass --allow-dirty to deploy the working tree as it is."
  fi

  remote_check
  work_out_migrations

  echo "Publishing..."
  (cd "$PROJECT_DIR" && dotnet publish -c Release -o "$WORK/publish" -v q --nologo 2>&1 | tail -3) || die "publish failed."
  [ -f "$WORK/publish/Intelimensa.Accounts.dll" ] || die "publish output not found."
  # An old ./publish folder in the project gets swept into the output as content; it isn't part of the app.
  rm -rf "$WORK/publish/publish"
  {
    echo "commit=$(git -C "$REPO_DIR" log -1 --format='%h %s')$([ -n "$(git -C "$REPO_DIR" status --porcelain)" ] && echo " + uncommitted changes")"
    echo "at=$(date -u +%Y-%m-%dT%H:%M:%SZ)"
  } >"$WORK/publish/deploy-info.txt"

  local remote_sql=""
  if [ -n "$PENDING" ]; then
    echo "Generating the migration script (everything after $LAST_APPLIED)..."
    (cd "$PROJECT_DIR" && dotnet ef migrations script "$LAST_APPLIED" -o "$WORK/migrate.sql" >"$WORK/ef.log" 2>&1) \
      || { tail -5 "$WORK/ef.log" >&2; die "could not generate the migration script (run 'dotnet tool restore' once?)."; }
    # The script must record exactly the pending migrations, or it isn't the one we planned.
    local scripted
    scripted="$(grep -o "VALUES ('[0-9]\{14\}_[A-Za-z0-9_]*'" "$WORK/migrate.sql" | sed "s/VALUES ('//;s/'$//" | sort)"
    [ "$scripted" = "$PENDING" ] || die "the generated script doesn't match the pending migrations:
$scripted"
    remote_sql="$REMOTE_SQL"
  fi

  echo
  describe_plan
  echo
  [ "$yes" -eq 1 ] || confirm "Deploy this to $HOST? The service is briefly stopped."

  echo "Uploading..."
  rsync -az --delete "$WORK/publish/" "$HOST:$ROOT/app.new/" || die "upload failed; nothing on the server changed."
  if [ -n "$remote_sql" ]; then
    scp -q "$WORK/migrate.sql" "$HOST:$REMOTE_SQL" || die "upload failed; nothing on the server changed."
  fi

  # -t: sudo on the server may need to ask for a password.
  ssh -t "$HOST" "bash '$REMOTE_SCRIPT' apply '$ROOT' '$SERVICE' '$HEALTH_URL' '$HEALTH_HOST' '$remote_sql'" || die "deploy failed (see above)."
  echo "Deployed."
}

cmd_rollback() {
  local yes=0
  [ "${1:-}" = "--yes" ] && yes=1
  remote_check
  work_out_migrations
  print_state
  [ "$HAS_PREVIOUS" = "yes" ] || die "no previous version is kept on the server."

  echo
  if [ "$LAST_MIGRATED" = "1" ]; then
    echo "The last deploy changed the database schema, so rolling back also restores the database from"
    echo "just before it. Anything written since (sign-ups, registrations, uploads' records) is removed from"
    echo "the live database; a copy of the replaced database is kept in $ROOT/backups."
  else
    echo "The last deploy did not change the schema: only the app goes back, the database is untouched."
  fi
  [ "$yes" -eq 1 ] || confirm "Roll back $HOST to the previous version?"

  ssh -t "$HOST" "bash '$REMOTE_SCRIPT' rollback '$ROOT' '$SERVICE' '$HEALTH_URL' '$HEALTH_HOST'" || die "rollback failed (see above)."
  echo "Rolled back."
}

cmd_log() {
  local n="${1:-20}"
  [[ "$n" =~ ^[0-9]+$ ]] || die "log takes a number of lines."
  need ssh
  ssh "$HOST" "tail -n $n '$ROOT/backups/deploy.log'" || die "no deploy log yet at $ROOT/backups/deploy.log (or the server is unreachable)."
}

case "${1:-}" in
  plan)     cmd_plan ;;
  deploy)   shift; cmd_deploy "$@" ;;
  rollback) shift; cmd_rollback "$@" ;;
  status)   cmd_status ;;
  log)      shift; cmd_log "$@" ;;
  *) sed -n '2,13p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 1 ;;
esac
