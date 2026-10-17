#!/usr/bin/env bash
R="$(cd "$(dirname "$0")/../../.." && pwd)"   # the workspace folder (this script lives in accounts/docs/razor-flake/)
S="${TMPDIR:-/tmp}/razor-flake"; mkdir -p "$S"   # logs of failing builds are kept here
cd $R/accounts
summ() { # summ LOG LABEL
  local log="$1" label="$2" rc="$3"
  if [ "$rc" -eq 0 ]; then printf '   %-44s pass\n' "$label"; rm -f "$log"
  else printf '   %-44s FAIL  %s errors; first: %s [%s]\n' "$label" "$(grep -c ' error ' "$log")" "$(grep -m1 -o -E '[A-Za-z_/]+\.cshtml(\.g\.cs)?\([0-9,]+\): error [A-Z]+[0-9]+|[A-Za-z_]+_cshtml\.g\.cs\([0-9,]+\): error [A-Z]+[0-9]+|error [A-Z]+[0-9]+: [^[]*' "$log" | head -1 | cut -c1-110)" "$(basename "$log")"; fi
}
echo "# H: two builds of the same project at the same moment (an IDE's background build next to a test run)"
for i in 1 2 3 4 5 6; do
  touch Intelimensa.Accounts/Api/Auth/AuthContracts.cs
  dotnet build Intelimensa.Accounts.Tests/Intelimensa.Accounts.Tests.csproj --nologo -v q >$S/h$i-a.log 2>&1 & pa=$!
  dotnet build Intelimensa.Accounts/Intelimensa.Accounts.csproj --nologo -v q >$S/h$i-b.log 2>&1 & pb=$!
  wait $pa; ra=$?; wait $pb; rb=$?
  echo " round $i"; summ $S/h$i-a.log "tests project build" $ra; summ $S/h$i-b.log "web project build" $rb
done
echo "# I: a .cshtml rewritten in place (truncate, then write, as a non-atomic editor save) while a build runs"
F=Intelimensa.Accounts/Pages/Account/Login.cshtml; cp $F $S/login.bak
for i in 1 2 3 4 5; do
  ( for k in $(seq 1 200); do cp $S/login.bak $F; sleep 0.01; done ) & pw=$!
  dotnet build Intelimensa.Accounts.Tests/Intelimensa.Accounts.Tests.csproj --nologo -v q >$S/i$i.log 2>&1; rc=$?
  wait $pw; echo " round $i"; summ $S/i$i.log "build during rewrites of Login.cshtml" $rc
done
cp $S/login.bak $F
echo "# J: after all that, is the tree consistent? (clean-state rebuild must pass)"
dotnet build-server shutdown >/dev/null 2>&1
dotnet build Intelimensa.Accounts.Tests/Intelimensa.Accounts.Tests.csproj --nologo -v q >$S/j.log 2>&1; summ $S/j.log "final rebuild" $?
git status --short | head -3
echo "# done"
