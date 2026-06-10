#!/usr/bin/env bash
# Remotely build-install-launch-screenshot LichessXbox on the dev Xbox via Windows
# Device Portal. No Windows machine and no manual Safari sideload needed.
#
# Usage:
#   deploy.sh install   # download latest green CI artifact, (re)install, launch
#   deploy.sh launch     # just launch the installed app
#   deploy.sh shot [out] # screenshot to /tmp/xbox.png (or given path)
#   deploy.sh stop       # force-stop the running app
#   deploy.sh uninstall  # remove the package
#   deploy.sh state      # last package-manager operation result
#
# Env overrides: XBOX (portal host:port), default below.
set -uo pipefail

XBOX="${XBOX:-192.168.1.64:11443}"
BASE="https://$XBOX"
JAR=/tmp/xbox-cookies.txt
PFN="Lichess.Xbox.Chess_1.0.0.0_x64__7mhbhha5y68ey"   # PackageFullName
AID="Lichess.Xbox.Chess_7mhbhha5y68ey!App"            # AppUserModelId (PRAID)
ARTDIR=/tmp/alx-sideload
PKGDIR="$ARTDIR/LichessXbox/AppPackages/LichessXbox_1.0.0.0_Test"

# Device Portal blocks non-GET without a CSRF token: do one GET to seed the cookie
# jar, then send the cookie's token back as X-CSRF-Token on every POST/DELETE.
auth() {
  curl -sk -c "$JAR" -o /dev/null "$BASE/api/os/info"
  TOKEN=$(grep -i csrf "$JAR" | awk '{print $NF}')
}
post()   { curl -sk -b "$JAR" -H "X-CSRF-Token: $TOKEN" -X POST   "$@"; }
delete() { curl -sk -b "$JAR" -H "X-CSRF-Token: $TOKEN" -X DELETE "$@"; }
b64()    { printf '%s' "$1" | base64; }

wait_state() {  # poll until the async deploy/uninstall reports a terminal result
  for _ in $(seq 1 45); do
    R=$(curl -sk -b "$JAR" -w "|%{http_code}" "$BASE/api/app/packagemanager/state")
    [ "${R##*|}" = "200" ] && { echo "${R%|*}"; return 0; }
    sleep 4
  done
  echo "TIMED OUT waiting for package-manager state"; return 1
}

fetch_artifact() {
  local RUN
  RUN=$(gh run list -R olievans123/lichessXbox --limit 10 \
        --json databaseId,conclusion,status \
        -q '[.[]|select(.status=="completed" and .conclusion=="success")][0].databaseId')
  [ -z "$RUN" ] && { echo "No green CI run found"; exit 1; }
  echo "Downloading artifact from run $RUN..."
  rm -rf "$ARTDIR"
  gh run download "$RUN" -R olievans123/lichessXbox -n LichessXbox-sideload -D "$ARTDIR"
}

stop_app() {
  auth
  delete "$BASE/api/taskmanager/app?package=$(b64 "$PFN")&forcestop=yes" -o /dev/null -w "stop: HTTP %{http_code}\n"
}

do_install() {
  fetch_artifact
  auth
  # CRITICAL: stop the app first. Deploying over the RUNNING app fails with
  # 0x80070005 "Access is denied".
  stop_app
  sleep 3
  echo "Deploying (bundle + x64 dependencies)..."
  post "$BASE/api/app/packagemanager/package?package=LichessXbox_1.0.0.0_x64.msixbundle" \
    -F "LichessXbox_1.0.0.0_x64.msixbundle=@$PKGDIR/LichessXbox_1.0.0.0_x64.msixbundle" \
    -F "Microsoft.VCLibs.x64.14.00.appx=@$PKGDIR/Dependencies/x64/Microsoft.VCLibs.x64.14.00.appx" \
    -F "Microsoft.NET.Native.Framework.2.2.appx=@$PKGDIR/Dependencies/x64/Microsoft.NET.Native.Framework.2.2.appx" \
    -F "Microsoft.NET.Native.Runtime.2.2.appx=@$PKGDIR/Dependencies/x64/Microsoft.NET.Native.Runtime.2.2.appx" \
    -w "\ndeploy: HTTP %{http_code}\n"
  R=$(wait_state); echo "RESULT: $R"
  case "$R" in
    *'"Success" : true'*) do_launch ;;
    *0x80070005*|*'Not ready'*|*0x80270300*)
      echo
      echo "!! HALF-REGISTERED LIMBO. Do NOT remote-uninstall — on this console the WDP"
      echo "   uninstall leaves a per-user registration (two users: DefaultAccount +"
      echo "   UserMgr1) that breaks the next install. The ONLY reliable clear is from"
      echo "   the console: My games & apps > Apps > Online Chess > Manage > Uninstall."
      echo "   Then run: deploy.sh install"
      exit 1 ;;
    *) echo "Deploy failed; see RESULT above"; exit 1 ;;
  esac
}

# NOTE: remote uninstall is UNRELIABLE on this Xbox — it half-deregisters and the
# next install lands in 'Not ready yet' limbo. Uninstall from the console UI instead.
# Kept only for completeness / diagnostics.
do_uninstall() { auth; delete "$BASE/api/app/packagemanager/package?package=$PFN" -w "\nuninstall: HTTP %{http_code}\n"; wait_state >/dev/null 2>&1; }

do_launch() {  # launch needs an empty body (-d '') or WDP returns HTTP 411
  auth
  for i in $(seq 1 10); do
    C=$(post -d '' "$BASE/api/taskmanager/app?appid=$(b64 "$AID")&package=$(b64 "$PFN")" -o /dev/null -w "%{http_code}")
    echo "[$i] launch HTTP $C"
    [ "$C" = "200" ] && { echo LAUNCHED; return 0; }
    sleep 8   # a clean install finalizes in <15s; persistent 400s = the limbo below
  done
  echo
  echo "!! Launch keeps 400ing = 'Not ready yet' LIMBO (blank green tile on the dashboard)."
  echo "   On THIS console ANY remote re-deploy lands here — over an existing install OR"
  echo "   after a remote uninstall. The ONLY reliable fix is a CONSOLE-side uninstall:"
  echo "   My games & apps > Apps > Online Chess > Manage > Uninstall, THEN deploy.sh install"
  echo "   (a single clean install over a truly-absent app launches first try)."
  return 1
}

do_shot() { local O="${1:-/tmp/xbox.png}"; curl -sk -m 15 -o "$O" "$BASE/ext/screenshot?download=true" && echo "saved $O"; }

case "${1:-}" in
  install)   do_install ;;
  launch)    do_launch ;;
  shot)      do_shot "${2:-}" ;;
  stop)      stop_app ;;
  uninstall) do_uninstall; echo done ;;
  state)     auth; wait_state ;;
  *) echo "usage: deploy.sh {install|launch|shot [path]|stop|uninstall|state}"; exit 1 ;;
esac
