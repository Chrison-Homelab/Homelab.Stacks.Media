#!/usr/bin/env bash
#
# maintainerr-wire.sh — point Maintainerr (CT 5114 :6246) at Plex, Sonarr, Radarr and Seerr,
# then refuse to exit 0 unless every collection is report-only.
#
# Also applies the rule groups in maintainerr-rules.json and connects Tracearr (CT 5109), which is
# the watch-history source here since Tautulli was retired.
#
# Maintainerr keeps its settings in its own sqlite database and takes them only through its
# HTTP API, so the quadlet cannot carry them the way recyclarr's asset does. This script is
# that missing half. Re-runnable: settings are PATCHed to the desired values, *arr servers are
# matched by name and updated in place rather than added twice.
#
# REPORT-ONLY GUARD (Homelab #565). A collection is skipped by Maintainerr's collection handler
# if its action is DO_NOTHING *or* it has no "take action after days". This script requires
# BOTH on every collection and exits 1 naming any that fail — it does not rewrite them, because
# a collection that fails is either a mistake or a decision, and a script cannot tell which.
# Enabling a delete action is meant to be a change to this file, reviewed, not a UI click.
#
# Usage (from the superproject root, so secrets.env is the synced one):
#   stacks/Media/tools/maintainerr-wire.sh              # wire + check
#   stacks/Media/tools/maintainerr-wire.sh --check      # check the posture only, change nothing
#
# Needs: curl, jq, ssh to root@hpe-01 (to read Seerr's own API key off CT 5105), and
# secrets.env with PLEX_TOKEN, PLEX_SERVER_CLIENT_IDENTIFIER, SONARR_API_KEY, RADARR_API_KEY and
# TRACEARR_API_KEY (made in Tracearr's UI; no API can mint one).
# No secret value is printed.
set -euo pipefail

MAINTAINERR="${MAINTAINERR_URL:-http://media.homelab.chrison.internal:6246}"
PLEX_HOST="plex.homelab.chrison.internal"
SONARR_URL="http://sonarr.homelab.chrison.internal:8989"
RADARR_URL="http://radarr.homelab.chrison.internal:7878"
SEERR_URL="http://seerr.homelab.chrison.internal:5055"
PVE_NODE="root@hpe-01.homelab.chrison.internal"
SEERR_CTID=5105
TRACEARR_URL="http://tracearr.homelab.chrison.internal:3000"
RULES_FILE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/maintainerr-rules.json"

# ServarrAction.DO_NOTHING in @maintainerr/contracts (servarr-action.ts, v3.29.0).
DO_NOTHING=4

CHECK_ONLY=false
[ "${1:-}" = "--check" ] && CHECK_ONLY=true

api() {  # api METHOD PATH [JSON-BODY]
  local method=$1 path=$2 body=${3:-}
  if [ -n "$body" ]; then
    curl -fsS -X "$method" -H 'Content-Type: application/json' --data "$body" "$MAINTAINERR/api$path"
  else
    curl -fsS -X "$method" "$MAINTAINERR/api$path"
  fi
}

# Maintainerr answers a refused write with HTTP 200 and {"status":"NOK"} in the body, so curl -f
# alone passes it. Verified live: the first version of this script PATCHed Plex settings before
# the token existed, got "Authenticate with Plex before saving Plex server settings." back, and
# reported success while every field stayed null. Every write goes through this.
write() {  # write METHOD PATH JSON-BODY
  local out
  out="$(api "$@")"
  if printf '%s' "$out" | jq -e 'type == "object" and has("status") and .status != "OK"' >/dev/null 2>&1; then
    echo "ERROR: $1 $2 refused: $(printf '%s' "$out" | jq -r '.message // .')" >&2
    exit 1
  fi
}

curl -fsS -o /dev/null "$MAINTAINERR/api/health/ready" \
  || { echo "ERROR: Maintainerr is not ready at $MAINTAINERR" >&2; exit 1; }

if ! $CHECK_ONLY; then
  for v in PLEX_TOKEN PLEX_SERVER_CLIENT_IDENTIFIER SONARR_API_KEY RADARR_API_KEY; do
    [ -n "${!v:-}" ] || { echo "ERROR: $v is not set — source a synced secrets.env" >&2; exit 1; }
  done

  SEERR_API_KEY="$(ssh -o BatchMode=yes "$PVE_NODE" \
    "pct exec $SEERR_CTID -- cat /opt/seerr/config/settings.json" 2>/dev/null | jq -r '.main.apiKey // empty')"
  [ -n "$SEERR_API_KEY" ] || { echo "ERROR: could not read Seerr's API key from CT $SEERR_CTID" >&2; exit 1; }

  PLEX_NAME="$(curl -fsS -H 'Accept: application/json' -H "X-Plex-Token: $PLEX_TOKEN" \
    "http://$PLEX_HOST:32400/" | jq -r '.MediaContainer.friendlyName')"

  # ── Plex: the token has its OWN endpoint, and must land before any Plex server field ──
  # updateSettings refuses Plex host/port while no token is stored (see write() above).
  write POST /settings/plex/token "$(jq -n --arg t "$PLEX_TOKEN" '{plex_auth_token: $t}')"

  # ── Plex server + Seerr: plain settings fields ──
  write PATCH /settings "$(jq -n \
    --arg host "$PLEX_HOST" --arg name "$PLEX_NAME" --arg machine "$PLEX_SERVER_CLIENT_IDENTIFIER" \
    --arg seerr "$SEERR_URL" --arg seerrKey "$SEERR_API_KEY" '{
      media_server_type: "plex",
      plex_hostname: $host, plex_port: 32400, plex_ssl: 0, plex_name: $name,
      plex_machine_id: $machine, plex_manual_mode: 1,
      seerr_url: $seerr, seerr_api_key: $seerrKey
    }')"

  # Read back rather than trust the write: a status of OK has already been wrong once.
  api GET /settings | jq -e --arg host "$PLEX_HOST" --arg seerr "$SEERR_URL" \
    '.media_server_type == "plex" and .plex_hostname == $host and .seerr_url == $seerr' >/dev/null \
    || { echo "ERROR: settings did not persist — read-back shows: $(api GET /settings | jq -c '{media_server_type, plex_hostname, seerr_url}')" >&2; exit 1; }
  echo "settings: Plex ($PLEX_NAME) and Seerr set"

  # ── Sonarr / Radarr: a list of servers, matched by name ──
  upsert_arr() {  # upsert_arr sonarr|radarr NAME URL KEY
    local kind=$1 name=$2 url=$3 key=$4 body id
    body="$(jq -n --arg n "$name" --arg u "$url" --arg k "$key" '{serverName:$n, url:$u, apiKey:$k}')"
    api POST "/settings/test/$kind" "$body" | jq -e '.status == "OK"' >/dev/null \
      || { echo "ERROR: Maintainerr cannot reach $kind at $url" >&2; exit 1; }
    id="$(api GET "/settings/$kind" | jq -r --arg n "$name" '.[] | select(.serverName == $n) | .id' | head -1)"
    if [ -n "$id" ]; then
      write PUT "/settings/$kind/$id" "$body"; echo "$kind: '$name' updated (id $id)"
    else
      write POST "/settings/$kind" "$body";    echo "$kind: '$name' added"
    fi
  }
  upsert_arr sonarr Sonarr "$SONARR_URL" "$SONARR_API_KEY"
  upsert_arr radarr Radarr "$RADARR_URL" "$RADARR_API_KEY"

  # ── Tracearr: watch history (the Tautulli replacement) ──
  # Unset is loud but not fatal, so the rules below still apply on a machine without the key.
  if [ -n "${TRACEARR_API_KEY:-}" ]; then
    conn="$(jq -n --arg u "$TRACEARR_URL" --arg k "$TRACEARR_API_KEY" '{url:$u, api_key:$k}')"
    # One Plex server in Tracearr; pick it by the name Plex reports rather than hard-coding its id.
    server_id="$(api POST /settings/tracearr/servers "$conn" \
      | jq -r --arg n "$PLEX_NAME" '[.[] | select(.name == $n)][0].id // empty')"
    [ -n "$server_id" ] || { echo "ERROR: Tracearr lists no server named '$PLEX_NAME'" >&2; exit 1; }
    body="$(printf '%s' "$conn" | jq --arg s "$server_id" '. + {server_id:$s}')"
    write POST /settings/test/tracearr "$body"
    write POST /settings/tracearr "$body"
    api GET /settings/tracearr | jq -e --arg s "$server_id" '.server_id == $s' >/dev/null \
      || { echo "ERROR: Tracearr settings did not persist" >&2; exit 1; }
    echo "tracearr: connected (server '$PLEX_NAME')"
  else
    echo "WARNING: TRACEARR_API_KEY unset — Tracearr NOT connected. Create a key in Tracearr, store it" >&2
    echo "         in Bitwarden SM as TRACEARR_API_KEY, re-sync secrets.env, re-run." >&2
  fi

  # ── Rule groups: matched by name, created or replaced ──
  # arrAction and deleteAfterDays are set HERE, not read from the file, so no edit to the rules
  # file can turn a group destructive. That takes a change to this script.
  sonarr_id="$(api GET /settings/sonarr | jq -r '.[] | select(.serverName == "Sonarr") | .id')"
  existing="$(api GET /rules)"
  n="$(jq '.groups | length' "$RULES_FILE")"
  for i in $(seq 0 $((n - 1))); do
    g="$(jq -c ".groups[$i]" "$RULES_FILE")"
    name="$(printf '%s' "$g" | jq -r .name)"
    id="$(printf '%s' "$existing" | jq -r --arg n "$name" '[.[] | select(.name == $n)][0].id // empty')"
    body="$(printf '%s' "$g" | jq --argjson dn "$DO_NOTHING" --arg sid "$sonarr_id" --arg id "$id" '
      {
        name, description, libraryId, dataType, rules,
        arrAction: $dn, isActive: true, useRules: true,
        listExclusions: false, cleanupLeftoverFolders: false, forceSeerr: false, tagInArr: false,
        keepInMaintainerrOnly: false,
        collection: {
          visibleOnRecommended: false, visibleOnHome: false,
          overlayEnabled: false, overlayTemplateId: null,
          deleteAfterDays: null, manualCollection: false, keepLogsForMonths: 6
        }
      }
      + (if .sonarr then {sonarrSettingsId: ($sid | tonumber)} else {} end)
      + (if $id != "" then {id: ($id | tonumber)} else {} end)')"
    if [ -n "$id" ]; then
      write PUT /rules "$body";  echo "rules: '$name' replaced (id $id)"
    else
      write POST /rules "$body"; echo "rules: '$name' created"
    fi
  done
fi

# ── The report-only guard ──
offenders="$(api GET /collections | jq -r --argjson dn "$DO_NOTHING" '
  .[] | select(.arrAction != $dn or .deleteAfterDays != null)
      | "  - \(.title): arrAction=\(.arrAction) deleteAfterDays=\(.deleteAfterDays)"')"
count="$(api GET /collections | jq 'length')"
if [ -n "$offenders" ]; then
  echo "REPORT-ONLY VIOLATED — these collections could act on media:" >&2
  echo "$offenders" >&2
  exit 1
fi
echo "posture: $count collection(s), all report-only (Do nothing, no action window)"
