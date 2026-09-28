#!/usr/bin/env bash
# =============================================================================
#  deploy-auto.sh — unattended MangosSuperUI deploy with health check + rollback.
# =============================================================================
#
#  Usage (on the server, as the service user):
#    deploy-auto.sh /tmp/mangossuperui-deploy.tgz
#
#  Needs ONE sudoers grant, installed once by the owner (validated with visudo):
#    <user> ALL=(root) NOPASSWD: /usr/bin/systemctl stop mangossuperui, \
#        /usr/bin/systemctl start mangossuperui, /usr/bin/systemctl restart mangossuperui, \
#        /usr/bin/systemctl is-active mangossuperui
#  (see tools/deploy-webapp.ps1 for the exact one-time command).
#
#  What it does, in order — any failure stops before the live install is touched,
#  or restores the previous install if the new one does not come up healthy:
#    1. refuses while a World Pack build is running (a restart would kill it);
#    2. unpacks the tarball into a fresh staging dir and sanity-checks it;
#    3. backs up /opt/mangossuperui (keeps the newest 5 auto backups);
#    4. stop → copy (appsettings*.json and server-config.json are never overwritten) → start;
#    5. health check: HTTP 200 from /WorldPacks/Status within 90 s;
#    6. on failure: restore the backup, start again, exit non-zero.
# =============================================================================
set -euo pipefail

TARBALL="${1:?usage: deploy-auto.sh <tarball>}"
APP_DIR="${APP_DIR:-/opt/mangossuperui}"
SERVICE="${SERVICE:-mangossuperui}"
BACKUPS="${BACKUPS:-$HOME/deploy/backups}"
HEALTH_URL="${HEALTH_URL:-http://127.0.0.1:5000/WorldPacks/Status}"
STAMP="$(date +%Y%m%dT%H%M%S)"
STAGE="$(mktemp -d /tmp/msui-deploy-XXXXXX)"
trap 'rm -rf "$STAGE"' EXIT

log() { echo "[deploy $(date +%H:%M:%S)] $*"; }
fail() { log "FAILED: $*"; exit 1; }

# 1. Never restart the web app under a running World Pack publish.
if curl -s -m 5 "$HEALTH_URL" | grep -q '"running":true'; then
    fail "a World Pack build is running — try again when it finishes"
fi
sudo -n /usr/bin/systemctl is-active "$SERVICE" >/dev/null 2>&1 || true   # proves the grant exists
sudo -n -l 2>/dev/null | grep -q "systemctl stop $SERVICE" || fail "missing the NOPASSWD systemctl grant for $SERVICE"

# 2. Unpack and sanity-check.
tar xzf "$TARBALL" -C "$STAGE"
[ -f "$STAGE/MangosSuperUI.dll" ] || fail "tarball has no MangosSuperUI.dll"
rm -f "$STAGE"/appsettings.json "$STAGE"/appsettings.Development.json "$STAGE"/server-config.json
log "staged $(find "$STAGE" -type f | wc -l) file(s)"

# 3. Backup.
mkdir -p "$BACKUPS"
BACKUP="$BACKUPS/auto-$STAMP"
cp -a "$APP_DIR" "$BACKUP"
ls -1dt "$BACKUPS"/auto-* 2>/dev/null | tail -n +6 | xargs -r rm -rf
log "backup: $BACKUP"

healthy() {
    for _ in $(seq 1 45); do
        code="$(curl -s -o /dev/null -w '%{http_code}' -m 5 "$HEALTH_URL" || true)"
        [ "$code" = "200" ] && return 0
        sleep 2
    done
    return 1
}

# 4. Swap.
sudo -n /usr/bin/systemctl stop "$SERVICE"
cp -a "$STAGE"/. "$APP_DIR"/
sudo -n /usr/bin/systemctl start "$SERVICE"

# 5/6. Health check, rollback on failure.
if healthy; then
    log "OK: $SERVICE is up on the new build ($(stat -c %y "$APP_DIR/MangosSuperUI.dll" | cut -d. -f1))"
    exit 0
fi
log "new build did not answer $HEALTH_URL — rolling back to $BACKUP"
sudo -n /usr/bin/systemctl stop "$SERVICE" || true
rm -rf "$APP_DIR".rollback-tmp && cp -a "$BACKUP" "$APP_DIR".rollback-tmp
cp -a "$APP_DIR".rollback-tmp/. "$APP_DIR"/ && rm -rf "$APP_DIR".rollback-tmp
sudo -n /usr/bin/systemctl start "$SERVICE"
healthy && fail "rolled back to the previous build (it is serving again)" || fail "rollback started but the service is still not healthy — check journalctl -u $SERVICE"
