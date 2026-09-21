#!/usr/bin/env bash
# Proxmox CT lifecycle hookscript — BL-016 shared-export guard (issue #105).
#
# A shared NFS export bound by *path* (not a storage-referenced volume) is NOT
# gated by Proxmox on storage health. If the NAS drops, the bind target becomes
# an empty dir and writes land on the CT rootfs — the documented CT 5007
# rootfs-fill failure mode. This pre-start hook refuses to start a member unless
# the exports it binds are genuinely mounted on the host, closing that race.
#
# Install on a snippets-enabled storage and reference from a shape as:
#   spec.hookscript: local:snippets/ensure-data-mount.sh
# (see docs/runbooks/volume4-data-export.md). Must be executable: chmod 755.
#
# Proxmox invokes: <script> <vmid> <phase>   phase ∈ pre-start|post-start|pre-stop|post-stop
#
# CHECKS EVERY BOUND EXPORT, not just volume4. It used to hard-code volume4,
# which silently left any second export unguarded — qBittorrent gained a
# volume3 bind for seed-only torrents and would have written them to the rootfs
# had volume3 dropped. Deriving the list from `pct config` means a mount added
# to a shape is guarded the moment it converges, with nothing to remember.
#
# Conservative by construction: if no `/mnt/pve/...` binds can be parsed, it
# falls back to the original volume4 check rather than passing. A guard that
# quietly stops guarding is worse than one that is too strict.
set -euo pipefail

vmid="${1:?vmid}"
phase="${2:?phase}"

LEGACY_MOUNT="/mnt/pve/ds1813-nfs-volume-4"
LEGACY_DIR="${LEGACY_MOUNT}/data"

refuse () {
  echo "[ensure-data-mount] $1 — refusing to start CT $vmid" >&2
  echo "[ensure-data-mount] (would write to the CT rootfs instead of the NAS)" >&2
  exit 1
}

case "$phase" in
  pre-start)
    # mpN: /mnt/pve/<storage>/<subpath>,mp=/...  → collect host export roots + source dirs
    mapfile -t sources < <(
      pct config "$vmid" 2>/dev/null \
        | sed -n 's#^mp[0-9]\+: \(/mnt/pve/[^,]*\).*#\1#p'
    )

    if [ "${#sources[@]}" -eq 0 ]; then
      # Nothing parsed (no binds, or pct unavailable) — keep the original behaviour.
      mountpoint -q "$LEGACY_MOUNT" || refuse "$LEGACY_MOUNT is not mounted"
      [ -d "$LEGACY_DIR" ] || refuse "$LEGACY_DIR missing on a mounted export"
      exit 0
    fi

    for src in "${sources[@]}"; do
      # /mnt/pve/<storage> is the export root; everything after it is the subpath.
      root="$(printf '%s' "$src" | cut -d/ -f1-4)"
      mountpoint -q "$root" || refuse "$root is not mounted (needed for $src)"
      [ -d "$src" ] || refuse "$src missing on a mounted export"
    done
    ;;
  *)
    : # nothing to do for post-start / pre-stop / post-stop
    ;;
esac

exit 0
