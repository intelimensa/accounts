#!/usr/bin/env bash
# Razor compile flake experiments (see REPORT.md). Run from anywhere; needs the workspace checkout with accounts, firmware, axosync.
# It touches files' mtimes and, in a few steps, edits and restores one file; it ends with `git status` of accounts, which must be clean.
# Razor compile flake experiments. Builds the accounts test project (which builds the web project) under different conditions.
R="$(cd "$(dirname "$0")/../../.." && pwd)"   # the workspace folder (this script lives in accounts/docs/razor-flake/)
S="${TMPDIR:-/tmp}/razor-flake"; mkdir -p "$S"   # logs of failing builds are kept here
N=0
build() { # build LABEL [extra dotnet args]
  local label="$1"; shift; N=$((N+1)); local log="$S/run-$(printf '%02d' $N).log" t0=$SECONDS
  (cd $R/accounts && dotnet build Intelimensa.Accounts.Tests/Intelimensa.Accounts.Tests.csproj --nologo -v q "$@" >"$log" 2>&1); local rc=$?
  local errs; errs=$(grep -c ' error ' "$log")
  local first; first=$(grep -m1 -o -E '[A-Za-z_/]+\.cshtml(\.g\.cs)?\([0-9,]+\): error [A-Z]+[0-9]+|[A-Za-z_]+_cshtml\.g\.cs\([0-9,]+\): error [A-Z]+[0-9]+' "$log")
  if [ $rc -eq 0 ]; then printf '%2d %-52s pass  (%ds)\n' $N "$label" $((SECONDS-t0)); rm -f "$log"
  else printf '%2d %-52s FAIL  %s errors, first: %s  (%ds) [%s]\n' $N "$label" "$errs" "$first" $((SECONDS-t0)) "$(basename $log)"; fi
}
touch_accounts() { touch $R/accounts/Intelimensa.Accounts/Api/Auth/AuthContracts.cs; }
touch_others()  { touch $R/firmware/quarry/src/Intelimensa.Quarry.Core/Accounts/AccountsModels.cs
  (cd $R/firmware/quarry && dotnet build tests/Intelimensa.Quarry.Core.Tests/Intelimensa.Quarry.Core.Tests.csproj --nologo -v q >/dev/null 2>&1)
  (cd $R/axosync && dotnet build AxoSync.Tests.Unit/AxoSync.Tests.Unit.csproj --nologo -v q >/dev/null 2>&1); }

echo "# env"; dotnet --version; sw_vers -productVersion 2>/dev/null; uname -m
echo "# A: fresh servers, cold build, then 6 warm rebuilds of accounts only (an mtime change each time)"
dotnet build-server shutdown >/dev/null 2>&1
build "A0 cold"
for i in 1 2 3 4 5 6; do touch_accounts; build "A$i warm, accounts only"; done
echo "# B: warm server, Quarry + AxoSync built in between (4 rounds)"
for i in 1 2 3 4; do touch_others; touch_accounts; build "B$i after quarry+axosync builds"; done
echo "# C: content change then restore"
F=$R/accounts/Intelimensa.Accounts/Security/RegistrationCode.cs; cp $F $S/rc.bak
echo "// x" >> $F; build "C1 content changed"; cp $S/rc.bak $F; build "C2 content restored"
echo "# D: a razor file touched (no content change)"
touch $R/accounts/Intelimensa.Accounts/Pages/Account/Login.cshtml; build "D1 Login.cshtml mtime"
echo "# E: if anything failed above: does it persist, and what clears it? (rebuild untouched; then shutdown; then rebuild)"
build "E1 rebuild, nothing changed"
dotnet build-server shutdown >/dev/null 2>&1; build "E2 after build-server shutdown"
echo "# F: control: the same interleaved sequence without the shared compiler server"
dotnet build-server shutdown >/dev/null 2>&1
for i in 1 2 3 4; do touch_others; touch_accounts; build "F$i interleaved, UseSharedCompilation=false" -p:UseSharedCompilation=false; done
echo "# G: control: interleaved with the shared server again, to see it come back"
for i in 1 2 3 4; do touch_others; touch_accounts; build "G$i interleaved, shared server" ; done
cd $R && git -C accounts status --short | head -3; echo "# done"
