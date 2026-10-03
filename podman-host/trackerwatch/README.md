# trackerwatch

Freeleech notifications for the private trackers worth watching, published to the ntfy
**`freeleech`** topic. Christian can unsubscribe from that topic without losing anything else.

| message | when | ntfy priority |
|---|---|---|
| **Site-wide event**: a tracker-wide freeleech started or ended | the run it changes (every `eventIntervalHours`) | **3**: normal notification |
| **Digest**: individual freeleech torrents | at each of `digestTimes` | **1**: silent, sits in the list |
| **Tracker unreachable** for a day | each run while it holds | via **Alertmanager**, `stack: media` |
| **Keepalive session expired** (`TrackerSessionExpired`) | as soon as the cookie stops working | via **Alertmanager** |
| **Login due** (`TrackerLoginDue`): no confirmed activity for `warnDays` | each keepalive run while it holds | via **Alertmanager** |

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
| LST | ✅ | **not** always free, despite what we first thought: it runs **global freeleech events** (one on 2026-10-03 went unannounced because LST wasn't watched). Outside events only some torrents are free (e.g. files over 75 GiB). The normal free share wasn't measurable while that event ran, so `inactiveBelow: 0.5` is a cautious first guess: re-check it once the event has ended |
| Milkie | ❌ | freeleech **all the time** (81/81 old torrents free on 2026-10-03), so nothing about it is ever news. Prowlarr priority 19, the same as LST, so Sonarr and Radarr prefer both |
| MyAnonamouse | not yet | not in Prowlarr: MAM only allows VIP accounts to query it (Homelab #612) |
| ULCX | ❌ | account lost |

## Keepalive (#611): keeping accounts active

Some trackers disable an account that hasn't been active **on the website** for a while, however
much it seeds. trackerwatch reuses Christian's browser session to view a logged-in page every
`everyHours`, and counts the visit only when the logged-in page comes back (`aliveMarker`).

| tracker | rule | how it's covered |
|---|---|---|
| AvistaZ | log in at least once every **60 days**, and download a torrent every 90 (seeding alone doesn't count) | keepalive **daily**. The 90-day download happens anyway via Sonarr |
| LST | 90 days without activity → disabled; **seeding at least one torrent counts as activity** (LST FAQ) | nothing needed: we always seed LST torrents |
| Milkie | never disables for inactivity | nothing needed |
| SoulVoice | daily check-in | `soulvoice-attend` on hpe-01 |
| MyAnonamouse | its ToS bans AI and automation | **never automated**: only through its official API (#612) |

**Why a cookie and not a scripted login:** AvistaZ's login page sits behind bot protection (a
scripted request gets 403), and LST's login has a captcha. Getting past either is exactly what
gets accounts banned. Viewing an ordinary page with an existing session is what a browser does,
and AvistaZ's profile "Last Access" moves on any page view.

Outcomes are kept apart, as in soulvoice-attend (#601):

- **alive:** the logged-in page came back. Repeat in `everyHours`.
- **expired:** a login page came back, so the cookie is dead → `TrackerSessionExpired`.
- **unreachable:** no answer, a block or a 5xx. That's the site or the network, *not* the
  cookie, so no "refresh your cookie" alert. Retried in `retryHours`.

Whatever the cause, `TrackerLoginDue` fires once the last *confirmed* activity is `warnDays`
old. That's the reminder of last resort: log in by hand before `windowDays`.

### Refreshing a keepalive cookie

The cookie is password-equivalent: keep it out of issues and the repo.

It is AvistaZ's **session** cookie, `avistazx_session`, and the server forgets a session after
**60 hours without use** (`Max-Age=216000`). Each visit restarts that clock, which is why the
keepalive runs daily: if trackerwatch is down for more than ~2.5 days, the session dies and
`TrackerSessionExpired` fires.

**Give the keepalive its OWN session**, separate from the one in your everyday browser. The first
one shared Chrome's session and died about 1.5 days in. AvistaZ rotates the session cookie on every
response, and when the browser's activity re-issues the session, the copy held by the keepalive
is dropped. trackerwatch now keeps the rotated value it is handed (in
`/home/podman/trackerwatch-data/cookies/`, mode 0600), so a session it owns stays alive.

1. Open a **private/incognito window** and log in to AvistaZ there.
2. DevTools → Application → Cookies → `https://avistaz.to` → copy the value of
   **`avistazx_session`**.
3. **Close the private window. Don't log out**, because logging out kills the session.
4. In the superproject: `scripts/openbao-set.sh AVISTAZ_COOKIE`, value `avistazx_session=<value>`
   (it's sent as the Cookie header). OpenBao is the only secrets store.
5. Podman secrets are seeded **add-only**, so the old value stays on CT 5114 until it is removed:
   `podman secret rm trackerwatch_avistaz_cookie` as `podman` on CT 5114, then converge
   podman-host (restarts every unit on the host), or recreate the one secret by hand and
   `systemctl --user restart trackerwatch`.


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
