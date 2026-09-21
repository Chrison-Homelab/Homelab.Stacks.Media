# Hot / cold storage rules for torrents

volume4 is **hot** — what Plex serves. volume3 is **cold** — what we keep only to seed for
ratio. [`tools/seedonly.cs`](../tools/seedonly.cs) is the executable form of this document;
where they disagree, the script is what actually runs, so fix the script.

## The decision table

Applied in order. The first match wins.

| # | Condition | Verdict | Action |
|---|---|---|---|
| 1 | category is `soulvoice` | EXCLUDED | new tracker — never touched |
| 2 | category not `tv-sonarr` | EXCLUDED | movies and everything else are out of scope |
| 3 | seeded < 30 days | EXCLUDED | too new to judge; hit-and-run risk |
| 4 | currently uploading or has leechers | EXCLUDED | pausing it costs ratio right now |
| 5 | in Plex, **not** watched | **HOT** | stays on volume4 |
| 6 | in Plex, watched, public tracker | **DELETABLE** | re-downloadable, no ratio to protect |
| 7 | in Plex, watched, private tracker | **COLD** | unmonitor → drop from Plex → seed from volume3 |
| 8 | not in Plex, public tracker | **DELETABLE** | nothing to keep |
| 9 | not in Plex, private tracker | **COLD** | pure seed obligation |

## The two definitions that matter

**"In Plex" is not "hardlinked".** A torrent counts as in Plex if *either* of these holds:

- one of its files shares an **inode** with a file under a library root, or
- one of its files has a **byte size** matching a file under a library root.

The second test exists because Plex's TV and Movie libraries each carry **two paths, one per
volume**, so an import can land on the other volume from the torrent. Hardlinks cannot cross a
filesystem, so that import **copies**, and the torrent ends up linked to nothing while the
content is very much in Plex.

> This is not hypothetical. The first attempt at this tested only for hardlinks and moved 12
> torrents holding content Plex serves — Glass Onion, War Horse, House of the Dragon and others
> — into cold storage. Nothing broke, because the Plex copies were independent files, but the
> rule had found the opposite of what it was looking for.

**"Watched" means the operator watched it.** `viewCount > 0` read with the admin Plex token.
Plex tracks view state **per account**, so a managed user's history is invisible here and is
deliberately not considered. If the household grows into separate Plex users, this rule needs
revisiting before it is trusted again.

## Why movies are excluded

A film is watched once and kept; "watched" does not imply "finished with it" the way it does
for an episode. 79 radarr torrents (~2.1 TB) sit outside these rules on purpose.

## Acting on COLD

Order matters, because each step changes what the next one sees:

1. **Unmonitor** the episode in Sonarr, so it is not re-grabbed.
2. **Delete the episode file** through Sonarr (`DELETE /api/v3/episodefile/{id}`), not from the
   filesystem — deleting underneath Sonarr leaves phantom database entries.
3. **Re-run the classifier.** The torrent now reads "not in Plex" and stays COLD on its own merits.
4. **Set the torrent's category to `seed-only`.** qBittorrent relocates the files itself, because
   `auto_tmm` is on for every torrent and `category_changed_tmm_enabled` is set.

Step 2 frees nothing on its own when the file is hardlinked to the torrent — the bytes go when
the last link does, which is what step 4's move to volume3 accomplishes.

## Measured behaviour of the move

- **Every torrent in a batch goes offline immediately**, then they are copied **one at a time**.
  So the offline window for the Nth torrent is the sum of the copy times before it. Small batches
  are strictly better for seeding, not just safer.
- **~46.6 MB/s** uncontended, NAS→host→NAS (83 GB took 1784 s). An earlier 28.6 MB/s figure was
  measured while an unrelated rsync was running and should be ignored.
- Suggested batch size: **20–25 GB**, keeping the worst-case offline window under ~10 minutes.

## Running it

```bash
set -a && . ./secrets.env && set +a     # QBIT_USER / QBIT_PASSWORD
export PLEX_TOKEN=...                   # admin token, from Plex's Preferences.xml
dotnet run tools/seedonly.cs
```

Read-only. It sets no categories and deletes nothing.

The NFS exports are mounted on the Proxmox host only (ADR/BL-016 — never inside a guest) and the
hypervisor has no dotnet, so filesystem facts come over SSH in two `find` passes while the logic
runs locally. Same split as the converge engine's `NodeExec`. Override `SEEDONLY_NODE`,
`SEEDONLY_QBIT`, `SEEDONLY_PLEX` and `SEEDONLY_MIN_SEED_DAYS` if any of that moves.
