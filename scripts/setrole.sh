#!/usr/bin/env bash
# Grant or revoke an ASP.NET Core Identity role for an account, directly
# against the production SQLite database. Replaces the one-off
# `INSERT INTO AspNetUserRoles ...` snippets from docs/deployment-vps.md.
set -euo pipefail

DB_PATH="${SETROLE_DB:-/var/www/accounts/data/accounts.db}"

usage() {
  cat <<EOF
Usage: setrole.sh --account <email> --role <RoleName> (--enabled | --disabled)

  --account   Email address of the account (AspNetUsers.Email)
  --role      Role name (AspNetRoles.Name), e.g. Staff, Manufacturer
  --enabled   Grant the role (no-op if already granted)
  --disabled  Revoke the role (no-op if not currently granted)

Examples:
  setrole.sh --account someone@example.com --role Staff --enabled
  setrole.sh --account someone@example.com --role Manufacturer --disabled

Reads the DB at \$SETROLE_DB if set, otherwise $DB_PATH.
EOF
}

sql_escape() { printf '%s' "$1" | sed "s/'/''/g"; }

ACCOUNT=""
ROLE=""
ACTION=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --account)
      ACCOUNT="${2:-}"
      shift 2
      ;;
    --role)
      ROLE="${2:-}"
      shift 2
      ;;
    --enabled)
      [[ -n "$ACTION" ]] && { echo "Error: --enabled and --disabled are mutually exclusive" >&2; exit 1; }
      ACTION="enable"
      shift
      ;;
    --disabled)
      [[ -n "$ACTION" ]] && { echo "Error: --enabled and --disabled are mutually exclusive" >&2; exit 1; }
      ACTION="disable"
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "Unknown argument: $1" >&2
      usage
      exit 1
      ;;
  esac
done

if [[ -z "$ACCOUNT" || -z "$ROLE" || -z "$ACTION" ]]; then
  echo "Error: --account, --role, and one of --enabled/--disabled are all required" >&2
  usage
  exit 1
fi

if [[ ! -f "$DB_PATH" ]]; then
  echo "Error: database not found at $DB_PATH (set \$SETROLE_DB to override)" >&2
  exit 1
fi

ACCOUNT_ESC=$(sql_escape "$ACCOUNT")
ROLE_ESC=$(sql_escape "$ROLE")

USER_ID=$(sqlite3 "$DB_PATH" "SELECT Id FROM AspNetUsers WHERE Email = '${ACCOUNT_ESC}';")
if [[ -z "$USER_ID" ]]; then
  echo "Error: no account found with email '$ACCOUNT'" >&2
  exit 1
fi

ROLE_ID=$(sqlite3 "$DB_PATH" "SELECT Id FROM AspNetRoles WHERE Name = '${ROLE_ESC}';")
if [[ -z "$ROLE_ID" ]]; then
  echo "Error: no role found named '$ROLE'" >&2
  exit 1
fi

if [[ "$ACTION" == "enable" ]]; then
  EXISTS=$(sqlite3 "$DB_PATH" "SELECT COUNT(*) FROM AspNetUserRoles WHERE UserId = '${USER_ID}' AND RoleId = '${ROLE_ID}';")
  if [[ "$EXISTS" -gt 0 ]]; then
    echo "'$ACCOUNT' already has role '$ROLE' — nothing to do."
  else
    sqlite3 "$DB_PATH" "INSERT INTO AspNetUserRoles (UserId, RoleId) VALUES ('${USER_ID}', '${ROLE_ID}');"
    echo "Granted role '$ROLE' to '$ACCOUNT'."
  fi
else
  CHANGED=$(sqlite3 "$DB_PATH" "DELETE FROM AspNetUserRoles WHERE UserId = '${USER_ID}' AND RoleId = '${ROLE_ID}'; SELECT changes();")
  if [[ "$CHANGED" -gt 0 ]]; then
    echo "Revoked role '$ROLE' from '$ACCOUNT'."
  else
    echo "'$ACCOUNT' did not have role '$ROLE' — nothing to do."
  fi
fi

echo "Note: role claims are baked into the auth cookie/JWT at sign-in — the" \
     "account must log out and back in (web) or refresh (API) to see this change."
