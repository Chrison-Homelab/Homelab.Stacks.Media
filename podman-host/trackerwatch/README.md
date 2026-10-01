# trackerwatch

Freeleech notifications for the private trackers worth watching, published to the ntfy
**`freeleech`** topic. Christian can unsubscribe from that topic without losing anything else.

| message | when | ntfy priority |
|---|---|---|
| **Site-wide event**: a tracker-wide freeleech started or ended | the run it changes (every `eventIntervalHours`) | **3**: normal notification |
| **Digest**: individual freeleech torrents | at each of `digestTimes` | **1**: silent, sits in the list |
| **Tracker unreachable** for a day | each run while it holds | via **Alertmanager**, `stack: media` |

## Tuning: everything is in `assets/trackerwatch.json`

- `eventIntervalHours`, `digestTimes`: the schedule (local time, `timezone`)
- `sendEmptyDigest`: send "nothing to report" digests too (default off)
- `digestMaxPerTracker`: list length per tracker
- per tracker:
  - `oldHours`: what counts as "old" when detecting an event; must exceed the tracker's
    longest per-torrent promo
  - `minOld`: below this many old torrents the sample is **indeterminate**, and state is left alone
  - `activeAbove` / `inactiveBelow`: the old-torrent free share that means an event is on or
    off. In between is a dead band, so a borderline sample cannot flap the state.
  - `newUploadPromoHours`: freeleech younger than this is the tracker's standing new-upload
    promo, so it's left out of the digest (double upload is always listed)

## Which trackers, and why

| tracker | watched | why |
|---|---|---|
| SoulVoice | ✅ | events are real and rare (two in two weeks); every upload is free for 7 days, so `newUploadPromoHours: 168` |
| AvistaZ | ✅ | per-torrent freeleech is meaningful. About 57% of old torrents are free at baseline, hence `inactiveBelow: 0.75` |
| LST, Milkie | ❌ | freeleech **all the time**, so nothing about them is ever news. Prowlarr priority 19 instead, so Sonarr and Radarr prefer them |
| MyAnonamouse | not yet | not in Prowlarr: MAM only allows VIP accounts to query it (Homelab #612) |
| ULCX | ❌ | account lost |

## Things that look like bugs and aren't

- **"All the newest torrents are free" is not an event.** Prowlarr only returns the ~100 newest
  per query and won't page past them, and on a tracker with a new-upload promo they're free by
  construction. Events are read from freeleech on **old** torrents, reached with many varied
  `searchTerms`. Don't trim that list: with four generic terms the sample came back all fresh
  and an event could never fire.
- **The digest bypasses Alertmanager on purpose.** A digest is a message, not a condition; on
  the bus it would repeat and send meaningless RESOLVED notices. See ADR-0011's exceptions.
- **No notifications at all** usually means nobody is subscribed to `freeleech` in the app. A
  publish to an unsubscribed topic succeeds and goes nowhere.
- **The first minute after a restart is the compile.** It runs from source with the .NET SDK
  image (`dotnet run`).

## Testing without publishing

```bash
DRY_RUN=1 RUN_ONCE=1 TRACKERWATCH_CONFIG=assets/trackerwatch.json TRACKERWATCH_STATE=/tmp/tw.json \
  PROWLARR_API_KEY=… dotnet run assets/trackerwatch.cs
```

It does one observation and a digest, prints what it would publish or assert, and exits. Run it
twice against the same state file to check that an event isn't announced again.
