// trackerwatch: freeleech events and a freeleech digest for the private trackers worth watching.
//
// Runs as a long-lived container on the Media podman host (CT 5114) and schedules itself,
// the same way Recyclarr does: the podman provisioner renders only *.container/.volume/
// .network/.pod, so a systemd .timer dropped next to the quadlet would be silently ignored.
//
// WHAT IT ANSWERS, per tracker:
//   * EVENTS: has a site-wide freeleech started or ended? Announced the run it changes, at
//     priority 3, because they are rare and worth acting on.
//   * DIGEST: which individual torrents are freeleech right now? Sent at fixed times of day,
//     at priority 1 (silent), because Christian skims it when he feels like it.
//
// HOW AN EVENT IS DETECTED, and why it is not the obvious way:
//   Prowlarr's empty-query search returns only the ~100 NEWEST torrents, and paging past them
//   returns nothing. On a tracker with a new-upload promo window every one of those is free by
//   construction. SoulVoice read 100/100 nine days after its event ended. "All the newest are
//   free" is therefore not a signal; it is the absence of one. The signal is freeleech on OLD
//   torrents: one past every per-torrent promo window can only be free because the site made it
//   free. Older torrents are reached with many varied search terms, not with paging. With only
//   four generic terms the sample came back all fresh and the test could never fire (@Torrenting's
//   finding, soulvoice-freeleech-watch.sh).
//
// "COULDN'T TELL" IS NOT "NO". A sample with too few old torrents to judge is INDETERMINATE: it
// leaves the stored state alone and stays quiet. Treating it as "inactive" would announce an
// event over while it was still running.
//
// WHY IT PUBLISHES STRAIGHT TO NTFY, past Alertmanager (an exception recorded in ADR-0011):
//   Alertmanager models CONDITIONS that fire and resolve. A digest is a MESSAGE. Through the bus
//   it would repeat on its interval and send meaningless RESOLVED notices. Genuine conditions,
//   such as a tracker unreachable for a day, DO go through the bus, and are re-asserted every run
//   with a short endsAt, because Alertmanager keeps alerts in memory only (#602).
//
// Trackers that are freeleech ALL the time (Milkie) are deliberately absent from the config:
// nothing about them is ever news. LST was once left out on the same belief, and its global
// freeleech of 2026-10-03 went unannounced. Check a tracker's old-torrent free share before
// assuming it.
//
// KEEPALIVE (#611), the second job: some trackers disable an account that hasn't been ACTIVE on
// the website for a while, however busy it is seeding (AvistaZ: 60 days). Logging in can't be
// scripted there, because the login page is behind bot protection, and defeating that is
// what gets accounts banned. What CAN be done is what soulvoice-attend does: reuse Christian's
// browser session cookie to view a logged-in page, daily, inside the session's 60h idle expiry. AvistaZ's profile "Last Access" moves on any
// page view, so that page is also the PROOF: the visit only counts as confirmed when the page
// it returns is the logged-in one. Outcomes are kept apart (#601):
//   * alive        the logged-in page came back: activity confirmed
//   * expired      a login page came back: the cookie is dead, a human must log in and refresh it
//   * unreachable  no answer, a block, or a 5xx: the site or the network, NOT the cookie
// Alerts go through Alertmanager (they are conditions, with a meaningful RESOLVED):
//   TrackerSessionExpired as soon as the cookie dies, and TrackerLoginDue once the last
//   confirmed activity is `warnDays` old, which is the reminder of last resort, whatever the cause.
//
// BONUS (#612), the third job: read each tracker's bonus balance once a day, send a separate
// "Tracker digest" message (same ntfy topic, different kind of notification), and optionally
// SPEND points by fixed rules. Only MyAnonamouse today, and only through endpoints on MAM's
// /api/list.php (rule 1.7: anything else automated can cost the account): jsonLoad.php to read,
// json/bonusBuy.php to buy. Gifting (gift, sendWedge) may NOT be automated and is never called.
// Buying is a write against someone else's site, so: off unless bonus.autoBuy is true (otherwise
// the digest says what it WOULD buy), at most maxBuysPerDay, every purchase announced, and any
// response it doesn't understand HALTS buying until a human clears state.bonus.<name>.halted.
//
// STATS, the fourth job: every stats.everyHours, read account stats (ratio, up/down, buffer, bonus,
// hit-and-runs, unsatisfied) through each tracker's OWN API, and serve them as Prometheus metrics on
// :stats.metricsPort/metrics for the monitoring stack to scrape and alert on. Only trackers with an
// API are here: LST (UNIT3D /api/user, its key read from Prowlarr at runtime, so there is one copy)
// and MyAnonamouse (jsonLoad.php?snatch_summary, on MAM's permitted list). AvistaZ has no stats API.
//
// PLACEHOLDER WATCH, the fifth job (2026-10-08): some Chinese-tracker uploads (UBWEB on SoulVoice)
// are listed as one episode, e.g. "…S01E39…", but the torrent itself is named "…S01.Complete…" and
// holds that one file. Sonarr's grab history records the right episode, but a tracked download
// prefers the CLIENT's title whenever it parses, so the queue maps the download to every episode
// of the season (Against the Current: one E39 file → 47 queue rows). That jams imports ("Episode
// file already imported") and blocks searches ("Release in queue already meets cutoff").
// The fix is upstream of all of that: rename the torrent in qBittorrent to the title Sonarr
// grabbed. That is qBittorrent's display name only; files, hash and seeding are untouched. Sonarr
// re-maps it on its next refresh and imports normally. It never removes a torrent, never deletes
// a queue entry and never searches. A rename that doesn't fix the mapping is logged and not retried.
//
// Env:  PROWLARR_API_KEY, NTFY_TOKEN (required)
//       SONARR_API_KEY, QBIT_PASSWORD: the placeholder watch; unset means it is skipped.
//       <bonus cookieEnv>, e.g. MAM_ID: the bare mam_id value. Followed through rotation like the
//       keepalive cookie.
//       <keepalive cookieEnv>, e.g. AVISTAZ_COOKIE: a browser Cookie header, the SEED. Password-
//       equivalent: never logged and never in the state file. The site's rotated value is kept in
//       <state dir>/cookies/<name>.cookie (0600). Unset means that keepalive is skipped.
//       TRACKERWATCH_CONFIG (default /app/trackerwatch.json), TRACKERWATCH_STATE (/data/state.json)
//       DRY_RUN=1   print what would be published instead of publishing
//       RUN_ONCE=1  do one observation, and include the digest, then exit (for testing)
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

var cfgPath   = Environment.GetEnvironmentVariable("TRACKERWATCH_CONFIG") ?? "/app/trackerwatch.json";
var statePath = Environment.GetEnvironmentVariable("TRACKERWATCH_STATE")  ?? "/data/state.json";
var dryRun    = Environment.GetEnvironmentVariable("DRY_RUN") == "1";
var runOnce   = Environment.GetEnvironmentVariable("RUN_ONCE") == "1";
var apiKey    = Need("PROWLARR_API_KEY");
var ntfyToken = dryRun ? (Environment.GetEnvironmentVariable("NTFY_TOKEN") ?? "") : Need("NTFY_TOKEN");

var cfg       = JsonNode.Parse(File.ReadAllText(cfgPath))!.AsObject();
var tz        = TimeZoneInfo.FindSystemTimeZoneById(Str(cfg, "timezone", "Pacific/Auckland"));
var prowlarr  = Str(cfg, "prowlarrUrl").TrimEnd('/');
var ntfyUrl   = Str(cfg, "ntfyUrl").TrimEnd('/');
var ntfyTopic = Str(cfg, "ntfyTopic");
var amUrl     = Str(cfg, "alertmanagerUrl").TrimEnd('/');
var eventEvery = TimeSpan.FromHours(Num(cfg, "eventIntervalHours", 6));
var digestAt  = cfg["digestTimes"]!.AsArray().Select(n => TimeOnly.Parse(n!.GetValue<string>())).OrderBy(t => t).ToList();
var sendEmpty = cfg["sendEmptyDigest"]?.GetValue<bool>() ?? false;
var terms     = cfg["searchTerms"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
var delay     = TimeSpan.FromSeconds(Num(cfg, "requestDelaySeconds", 3));
var perTracker = (int)Num(cfg, "digestMaxPerTracker", 10);
var alertAfter = TimeSpan.FromHours(Num(cfg, "unreachableAlertAfterHours", 24));
var trackers  = cfg["trackers"]!.AsArray().Select(n => n!.AsObject()).ToList();
var keepalives = cfg["keepalive"]?.AsArray().Select(n => n!.AsObject()).ToList() ?? [];
var bonusCfg  = cfg["bonus"] as JsonObject;
var bonusTrackers = bonusCfg?["trackers"]?.AsArray().Select(n => n!.AsObject()).ToList() ?? [];
var bonusAt   = bonusCfg?["digestTimes"]?.AsArray().Select(n => TimeOnly.Parse(n!.GetValue<string>())).OrderBy(t => t).ToList() ?? [];
var autoBuy   = bonusCfg?["autoBuy"]?.GetValue<bool>() ?? false;
var statsCfg  = cfg["stats"] as JsonObject;
var statsTrackers = statsCfg?["trackers"]?.AsArray().Select(n => n!.AsObject()).ToList() ?? [];
var statsEvery = TimeSpan.FromHours(statsCfg is null ? 6 : Num(statsCfg, "everyHours", 6));
var metrics   = new System.Collections.Concurrent.ConcurrentDictionary<string, Dictionary<string, double>>();
var watchCfg  = cfg["placeholderWatch"] as JsonObject;
var watchOn   = watchCfg?["enabled"]?.GetValue<bool>() ?? false;
HttpClient? qbit = null;                                                   // placeholder watch: qBittorrent session
var counters  = new System.Collections.Concurrent.ConcurrentDictionary<string, double>();   // trackerwatch_* series

var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
// The keepalive client must SEE redirects (a redirect to the login page is the "expired" signal)
// and must send only the cookie it is given, so no auto-redirect and no cookie container.
var web  = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
           { Timeout = TimeSpan.FromSeconds(60) };
var state = File.Exists(statePath) ? JsonNode.Parse(File.ReadAllText(statePath))!.AsObject() : new JsonObject();
var ts = state["trackers"] as JsonObject ?? new JsonObject(); state["trackers"] = ts;
var ks = state["keepalive"] as JsonObject ?? new JsonObject(); state["keepalive"] = ks;
var bs = state["bonus"] as JsonObject ?? new JsonObject(); state["bonus"] = bs;

Log($"trackerwatch up: {trackers.Count} tracker(s), events every {eventEvery.TotalHours}h, digest at "
    + string.Join(",", digestAt.Select(t => t.ToString("HH:mm"))) + $" {tz.Id}, keepalive: "
    + (keepalives.Count == 0 ? "none" : string.Join(",", keepalives.Select(k => Str(k, "name"))))
    + ", stats: " + (statsTrackers.Count == 0 ? "none" : string.Join(",", statsTrackers.Select(t => Str(t, "name"))))
    + ", placeholder watch: " + (watchOn ? "on" : "off")
    + ", bonus: " + (bonusTrackers.Count == 0 ? "none" : string.Join(",", bonusTrackers.Select(b => Str(b, "name")))
        + " at " + string.Join(",", bonusAt.Select(t => t.ToString("HH:mm"))) + (autoBuy ? " (AUTO-BUY ON)" : " (dry run)"))
    + (dryRun ? ", DRY RUN" : ""));

if (statsCfg?["metricsPort"] is JsonNode mport && !runOnce) _ = ServeMetrics((int)mport.GetValue<double>());

while (true)
{
    var now = DateTimeOffset.UtcNow;
    var nextEvent = state["nextEventCheck"] is JsonNode ne ? DateTimeOffset.Parse(ne.GetValue<string>()) : now;
    var lastDigest = state["lastDigest"] is JsonNode ld ? DateTimeOffset.Parse(ld.GetValue<string>()) : now;
    if (state["lastDigest"] is null) state["lastDigest"] = now.ToString("o");   // never digest retroactively on first start
    var digestDue = runOnce || DigestSlotPassedSince(lastDigest, now);

    foreach (var k in keepalives) await KeepAlive(k, now);

    if (watchOn) await PlaceholderWatch(now);

    var nextStats = state["nextStats"] is JsonNode ns ? DateTimeOffset.Parse(ns.GetValue<string>()) : now;
    if (statsTrackers.Count > 0 && (runOnce || now >= nextStats || metrics.IsEmpty))
    {
        foreach (var t in statsTrackers) await ReadStats(t, now);
        state["nextStats"] = (now + statsEvery).ToString("o"); Save();
    }

    var lastBonus = state["lastBonus"] is JsonNode lb ? DateTimeOffset.Parse(lb.GetValue<string>()) : now;
    if (state["lastBonus"] is null) state["lastBonus"] = now.ToString("o");      // no retroactive digest on first start
    if (bonusTrackers.Count > 0 && (runOnce || SlotPassedSince(bonusAt, lastBonus, now)))
    {
        await BonusDigest(now);
        state["lastBonus"] = now.ToString("o"); Save();
    }

    if (runOnce || digestDue || now >= nextEvent)
    {
        var obs = new List<Observation>();
        foreach (var t in trackers) obs.Add(await Observe(t));
        foreach (var o in obs) await EvaluateEvent(o, now);
        if (digestDue) { await SendDigest(obs, now); state["lastDigest"] = now.ToString("o"); }
        state["nextEventCheck"] = (now + eventEvery).ToString("o");
        Save();
        if (runOnce) break;
    }
    await Task.Delay(TimeSpan.FromSeconds(60));
}
return 0;

// ── observation ───────────────────────────────────────────────────────────────────────────
async Task<Observation> Observe(JsonObject t) =>
    Str(t, "source", "prowlarr") == "unit3d" ? await ObserveUnit3d(t) : await ObserveProwlarr(t);

// A UNIT3D tracker read through its OWN API, not Prowlarr's search. Needed for LST: its Prowlarr
// indexer has "Search freeleech only" on (deliberately, so Sonarr/Radarr only ever grab free
// releases), which makes every result free and an event's end invisible. LST's global freeleech of
// 2026-10-03 stayed "in progress" for six days because of it. The oldest torrents are paged in
// directly; each becomes a row of the same shape the Prowlarr path produces, so the event and digest
// logic is shared. The Bearer key is the one Prowlarr already holds for the indexer.
async Task<Observation> ObserveUnit3d(JsonObject t)
{
    var name = Str(t, "name");
    var oldHours = Num(t, "oldHours", 336); var minOld = (int)Num(t, "minOld", 5);
    var pages = (int)Num(t, "pages", 3);
    var rows = new List<JsonObject>(); int failed = 0;
    string key;
    try { key = await ProwlarrField((int)Num(t, "prowlarrIndexerId"), "apikey"); }
    catch (Exception e) { Log($"  {name}: no API key from Prowlarr: {e.GetType().Name}"); key = ""; failed = pages; }
    for (var page = 1; page <= pages && key != ""; page++)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, Str(t, "baseUrl").TrimEnd('/')
                + $"/api/torrents/filter?perPage=100&page={page}&sortField=created_at&sortDirection=asc");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            req.Headers.TryAddWithoutValidation("User-Agent", "trackerwatch/1.0 (self-hosted)");
            using var resp = await web.SendAsync(req);
            if (!resp.IsSuccessStatusCode) { failed++; Log($"  {name}: page {page} HTTP {(int)resp.StatusCode}"); continue; }
            foreach (var d in JsonNode.Parse(await resp.Content.ReadAsStringAsync())!["data"]!.AsArray())
            {
                var a = d!["attributes"]!.AsObject();
                var flags = new JsonArray();
                if (a["freeleech"]?.ToString() == "100%") flags.Add(JsonValue.Create("freeleech"));
                if (a["double_upload"]?.ToString() is "true" or "True" or "1") flags.Add(JsonValue.Create("doubleupload"));
                var created = DateTimeOffset.Parse(a["created_at"]!.ToString());
                rows.Add(new JsonObject
                {
                    ["guid"] = d["id"]?.ToString(), ["title"] = a["name"]?.ToString(),
                    ["size"] = (long)(ParseNum(a["size"]) ?? 0),
                    ["ageHours"] = (DateTimeOffset.UtcNow - created).TotalHours, ["indexerFlags"] = flags,
                });
            }
        }
        catch (Exception e) { failed++; Log($"  {name}: page {page} failed: {e.GetType().Name}: {Trim(e.Message, 120)}"); }
        await Task.Delay(delay);
    }
    bool Free(JsonObject r) => Flags(r).Contains("freeleech");
    var old = rows.Where(r => Age(r) >= oldHours).ToList();
    var o2 = new Observation(t, name, Reachable: failed < pages, Unique: rows.Count,
        Old: old.Count, OldFree: old.Count(Free), Determinate: old.Count >= minOld, Rows: rows);
    Log($"  {name}: unique={o2.Unique} old={o2.Old} oldFree={o2.OldFree} determinate={o2.Determinate} "
        + $"reachable={o2.Reachable} failedPages={failed}/{pages} (own API)");
    return o2;
}

async Task<Observation> ObserveProwlarr(JsonObject t)
{
    var name = Str(t, "name"); var id = (int)Num(t, "indexerId");
    var oldHours = Num(t, "oldHours", 336); var minOld = (int)Num(t, "minOld", 5);
    var rows = new Dictionary<string, JsonObject>(); int failed = 0;
    foreach (var term in terms)
    {
        try
        {
            var url = $"{prowlarr}/api/v1/search?query={Uri.EscapeDataString(term)}&indexerIds={id}&type=search&limit=100";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("X-Api-Key", apiKey);
            using var resp = await http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) { failed++; continue; }
            foreach (var r in JsonNode.Parse(await resp.Content.ReadAsStringAsync())!.AsArray())
            {
                var o = r!.AsObject();
                // Prowlarr fans a search out to the indexer; a result from another indexer means the
                // id now points somewhere else. Never count it.
                if ((int)(o["indexerId"]?.GetValue<int>() ?? -1) != id) continue;
                rows[(o["guid"] ?? o["title"])!.ToString()] = o;
            }
        }
        catch (Exception e) { failed++; Log($"  {name}: '{term}' failed: {e.GetType().Name}"); }
        await Task.Delay(delay);
    }
    var all = rows.Values.ToList();
    bool Free(JsonObject r) => Flags(r).Contains("freeleech");
    var old = all.Where(r => Age(r) >= oldHours).ToList();
    var o2 = new Observation(t, name, Reachable: failed < terms.Count, Unique: all.Count,
        Old: old.Count, OldFree: old.Count(Free), Determinate: old.Count >= minOld, Rows: all);
    Log($"  {name}: unique={o2.Unique} old={o2.Old} oldFree={o2.OldFree} determinate={o2.Determinate} "
        + $"reachable={o2.Reachable} failedQueries={failed}/{terms.Count}");
    return o2;
}

// ── events ────────────────────────────────────────────────────────────────────────────────
async Task EvaluateEvent(Observation o, DateTimeOffset now)
{
    var s = ts[o.Name] as JsonObject ?? new JsonObject { ["state"] = "unknown" }; ts[o.Name] = s;
    if (!o.Reachable)
    {
        var first = s["unreachableSince"] is JsonNode f ? DateTimeOffset.Parse(f.GetValue<string>()) : now;
        s["unreachableSince"] = first.ToString("o");
        if (now - first >= alertAfter) await AssertUnreachable(o, first);
        return;
    }
    s.Remove("unreachableSince");
    if (!o.Determinate) { Log($"  {o.Name}: indeterminate ({o.Old} old < minOld); state held at {s["state"]}"); return; }

    var ratio = (double)o.OldFree / o.Old;
    var prev = s["state"]!.GetValue<string>();
    var next = ratio >= Num(o.Cfg, "activeAbove", 0.8) ? "active"
             : ratio <= Num(o.Cfg, "inactiveBelow", 0.2) ? "inactive"
             : prev;                                                  // in the band: hold, don't flap
    s["lastRatio"] = Math.Round(ratio, 3);
    if (next == prev) return;
    s["state"] = next; s["since"] = now.ToString("o");
    var share = $"{o.OldFree}/{o.Old} torrents older than {Num(o.Cfg, "oldHours", 336) / 24:0} days are free";
    if (prev == "unknown" && next == "inactive") { Log($"  {o.Name}: first observation, no event ({share})"); return; }
    if (next == "active")
        await Publish($"{o.Name}: site-wide freeleech {(prev == "unknown" ? "in progress" : "started")}",
            $"{share}.\nDownloads on {o.Name} don't count against your ratio while this lasts.", 3, "tada");
    else
        await Publish($"{o.Name}: site-wide freeleech ended",
            $"{share}.\nDownloads on {o.Name} count against your ratio again.", 3, "hourglass");
}

Task AssertUnreachable(Observation o, DateTimeOffset since) =>
    AssertAlert("TrackerUnreachable", o.Name, since, eventEvery + TimeSpan.FromHours(1),
        $"{o.Name} has not answered through Prowlarr since {Local(since):ddd HH:mm}",
        "Every freeleech search for this tracker failed. Check the indexer in Prowlarr " +
        "(credentials, cookie, or the site itself).");

async Task AssertAlert(string alertname, string tracker, DateTimeOffset since, TimeSpan holdFor,
                       string summary, string description)
{
    // Re-asserted on every run while it holds, with endsAt just past the next run: Alertmanager
    // keeps alerts in memory only (#602), so a one-shot alert would not survive a restart.
    var alert = new JsonArray(new JsonObject
    {
        // category=tracker routes every tracker alert to the `trackers` topic with one matcher, so a
        // new tracker needs no Alertmanager edit (Monitoring, 2026-10-04). service stays for detail.
        ["labels"] = new JsonObject { ["alertname"] = alertname, ["severity"] = "warning", ["category"] = "tracker",
            ["stack"] = "media", ["service"] = tracker.ToLowerInvariant(), ["instance"] = tracker },
        ["annotations"] = new JsonObject { ["summary"] = summary, ["description"] = description },
        ["startsAt"] = since.ToString("o"),
        ["endsAt"] = (DateTimeOffset.UtcNow + holdFor).ToString("o"),
    });
    if (dryRun) { Log($"  DRY RUN, would assert on the bus: {alert.ToJsonString()}"); return; }
    try
    {
        using var resp = await http.PostAsync($"{amUrl}/api/v2/alerts",
            new StringContent(alert.ToJsonString(), Encoding.UTF8, "application/json"));
        Log($"  {tracker}: {alertname} asserted (HTTP {(int)resp.StatusCode})");
    }
    catch (Exception e) { Log($"  {tracker}: could not reach Alertmanager: {e.GetType().Name}"); }
}

// ── keepalive (#611) ──────────────────────────────────────────────────────────────────────
async Task KeepAlive(JsonObject k, DateTimeOffset now)
{
    var name = Str(k, "name");
    var s = ks[name] as JsonObject ?? new JsonObject(); ks[name] = s;
    if (!runOnce && s["next"] is JsonNode nx && now < DateTimeOffset.Parse(nx.GetValue<string>())) return;

    // REMINDER mode (AvistaZ, 2026-10-06): the cookie keepalive lost its session within a day, twice,
    // apparently because the site keeps one session per account and Christian's own browser wins.
    // So no visits at all: a plain nudge every remindEveryDays, counted from the last nudge.
    if (Str(k, "mode", "visit") == "reminder")
    {
        s["lastReminder"] ??= now.ToString("o");                 // the clock starts at the switch-over
        var last = DateTimeOffset.Parse(s["lastReminder"]!.GetValue<string>());
        var remindEvery = Num(k, "remindEveryDays", 45);
        if (now - last >= TimeSpan.FromDays(remindEvery))
        {
            await Publish($"Log in to {name}", $"{name} deletes accounts with no website login for {Num(k, "windowDays"):0} days, " +
                $"and seeding doesn't count. Open the site once in your browser; the next reminder is in {remindEvery:0} days.", 3, "key");
            s["lastReminder"] = now.ToString("o");
        }
        s["next"] = (now + TimeSpan.FromHours(12)).ToString("o"); Save();
        return;
    }

    var seeded = Environment.GetEnvironmentVariable(Str(k, "cookieEnv"));
    if (string.IsNullOrEmpty(seeded))
    {
        Log($"  {name}: keepalive skipped, {Str(k, "cookieEnv")} is not set");
        s["next"] = (now + TimeSpan.FromHours(Num(k, "everyHours", 72))).ToString("o"); Save(); return;
    }

    // The site ROTATES its session cookie (Set-Cookie on every response), and a session that keeps
    // being sent the original value can be dropped while the browser's copy lives on; the first
    // AvistaZ session died ~1.5 days in this way. So the latest value the site handed back is kept
    // in a 0600 file beside the state, and used instead of the seed. A NEW seed (a human refreshed
    // the secret) is recognised by its hash and wins over the kept value.
    var (jar, cookie) = SessionCookie(name, seeded, s);

    var (verdict, rotated) = await Visit(k, cookie);
    if (rotated is not null && rotated != cookie && verdict == "alive") KeepCookie(jar, rotated);
    s["lastVerdict"] = verdict; s["lastRun"] = now.ToString("o");
    s["since"] ??= now.ToString("o");                       // baseline when nothing is confirmed yet
    if (verdict == "alive") { s["lastConfirmed"] = now.ToString("o"); s.Remove("expiredSince"); }
    if (verdict == "expired") s["expiredSince"] ??= now.ToString("o");
    if (verdict != "expired") s.Remove("expiredSince");

    // A failed visit is retried sooner than a successful one is repeated.
    var every = TimeSpan.FromHours(Num(k, "everyHours", 72));
    var retry = TimeSpan.FromHours(Num(k, "retryHours", 6));
    s["next"] = (now + (verdict == "alive" ? every : retry)).ToString("o");
    var hold  = (verdict == "alive" ? every : retry) + TimeSpan.FromHours(1);

    var confirmed = DateTimeOffset.Parse((s["lastConfirmed"] ?? s["since"])!.GetValue<string>());
    var days = (now - confirmed).TotalDays;
    var window = Num(k, "windowDays"); var warn = Num(k, "warnDays");
    Log($"  {name}: keepalive {verdict}, last confirmed activity {days:0.0} days ago (window {window:0}d)");

    if (verdict == "expired")
        await AssertAlert("TrackerSessionExpired", name, DateTimeOffset.Parse(s["expiredSince"]!.GetValue<string>()), hold,
            $"{name}: the keepalive session has expired",
            $"Log in to {name} in the browser, then refresh {Str(k, "cookieEnv")} (trackerwatch README, " +
            $"\"Refreshing a keepalive cookie\"). Last confirmed activity {days:0} days ago; " +
            $"{name} disables accounts after {window:0} days.");
    if (days >= warn)
        await AssertAlert("TrackerLoginDue", name, confirmed + TimeSpan.FromDays(warn), hold,
            $"{name}: log in by hand, {Math.Max(0, window - days):0} days left",
            $"No confirmed activity on {name} for {days:0} days (keepalive: {verdict}). " +
            $"{name} disables accounts after {window:0} days without it.");
    Save();
}

(string Jar, string Cookie) SessionCookie(string name, string seeded, JsonObject s)
{
    var jar = Path.Combine(Path.GetDirectoryName(statePath)!, "cookies", name + ".cookie");
    var seedHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(seeded)))[..16];
    var cookie = s["seedHash"]?.GetValue<string>() == seedHash && File.Exists(jar) ? File.ReadAllText(jar) : seeded;
    s["seedHash"] = seedHash;
    return (jar, cookie);
}

void KeepCookie(string path, string value)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    if (!OperatingSystem.IsWindows())
        File.SetUnixFileMode(Path.GetDirectoryName(path)!, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    var tmp = path + ".tmp";
    File.WriteAllText(tmp, "");
    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    File.WriteAllText(tmp, value);
    File.Move(tmp, path, overwrite: true);
}

// Apply the response's Set-Cookie values to the cookies we sent, by name. Only names we already
// send are updated, so the jar never grows tracking cookies.
static string? Rotate(string sent, HttpResponseMessage resp)
{
    if (!resp.Headers.TryGetValues("Set-Cookie", out var sets)) return null;
    var jar = sent.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                  .Select(p => p.Split('=', 2)).Where(p => p.Length == 2)
                  .Select(p => (Name: p[0], Value: p[1])).ToList();
    var changed = false;
    foreach (var sc in sets)
    {
        var kv = sc.Split(';', 2)[0].Split('=', 2);
        if (kv.Length != 2) continue;
        var i = jar.FindIndex(c => c.Name == kv[0].Trim());
        if (i >= 0 && jar[i].Value != kv[1]) { jar[i] = (jar[i].Name, kv[1]); changed = true; }
    }
    return changed ? string.Join("; ", jar.Select(c => $"{c.Name}={c.Value}")) : null;
}

async Task<(string Verdict, string? Rotated)> Visit(JsonObject k, string cookie)
{
    using var req = new HttpRequestMessage(HttpMethod.Get, Str(k, "url"));
    req.Headers.TryAddWithoutValidation("Cookie", cookie);
    req.Headers.TryAddWithoutValidation("User-Agent", Str(k, "userAgent"));
    req.Headers.TryAddWithoutValidation("Accept", "text/html");
    try
    {
        using var resp = await web.SendAsync(req);
        var code = (int)resp.StatusCode;
        if (code is >= 300 and < 400)
            return ((resp.Headers.Location?.ToString() ?? "").Contains("login", StringComparison.OrdinalIgnoreCase)
                ? "expired" : "unreachable", null);
        if (code == 401) return ("expired", null);
        if (code != 200) { Log($"  {Str(k, "name")}: keepalive got HTTP {code}"); return ("unreachable", null); }
        var body = await resp.Content.ReadAsStringAsync();
        // A 200 login page is still a login page.
        return (body.Contains(Str(k, "aliveMarker")) ? "alive" : "expired", Rotate(cookie, resp));
    }
    catch (Exception e) { Log($"  {Str(k, "name")}: keepalive failed: {e.GetType().Name}"); return ("unreachable", null); }
}

// ── bonus points (#612) ───────────────────────────────────────────────────────────────────
async Task BonusDigest(DateTimeOffset now)
{
    var sb = new StringBuilder();
    foreach (var b in bonusTrackers)
    {
        var name = Str(b, "name");
        var s = bs[name] as JsonObject ?? new JsonObject(); bs[name] = s;
        var seeded = Environment.GetEnvironmentVariable(Str(b, "cookieEnv"));
        if (string.IsNullOrEmpty(seeded)) { Log($"  {name}: bonus skipped, {Str(b, "cookieEnv")} is not set"); continue; }
        var (jar, cookie) = SessionCookie(name, seeded, s);

        var r = await MamLoad(b, cookie);
        if (r.Rotated is not null && r.Verdict == "ok") KeepCookie(jar, r.Rotated);
        s["lastVerdict"] = r.Verdict; s["lastRun"] = now.ToString("o");
        if (r.Verdict != "ok")
        {
            s["unreadableSince"] ??= now.ToString("o");
            var since = DateTimeOffset.Parse(s["unreadableSince"]!.GetValue<string>());
            sb.AppendLine($"⚠️ {name}: balance not readable ({r.Verdict})");
            if (now - since >= TimeSpan.FromHours(Num(b, "unreadableAlertAfterHours", 24)))
                await AssertAlert("TrackerBonusUnreadable", name, since, TimeSpan.FromHours(26),
                    $"{name}: bonus balance unreadable since {Local(since):ddd HH:mm} ({r.Verdict})",
                    r.Verdict == "expired"
                        ? $"The {Str(b, "cookieEnv")} session is no longer accepted. Create a new one on the site (Preferences → Security) and update it in OpenBao."
                        : $"{name} did not answer. The site or the network, not the session.");
            continue;
        }
        s.Remove("unreadableSince");
        var d = r.Data!;
        var points = d["seedbonus"]!.GetValue<long>();
        var ratio = ParseNum(d["ratio"]);
        var vipUntil = DateTime.TryParse(d["vip_until"]?.ToString(), System.Globalization.CultureInfo.InvariantCulture,
                                         System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var vu)
                       ? new DateTimeOffset(vu, TimeSpan.Zero) : (DateTimeOffset?)null;
        var perDay = s["lastPoints"] is JsonNode lp && s["lastPointsAt"] is JsonNode la
            ? (points - lp.GetValue<long>()) / Math.Max(0.5, (now - DateTimeOffset.Parse(la.GetValue<string>())).TotalDays) : (double?)null;
        s["lastPoints"] = points; s["lastPointsAt"] = now.ToString("o");

        var line = $"{name}: {points:N0} points" + (perDay is double pd ? $" ({pd:+#,0;-#,0;0}/day)" : "")
                 + (vipUntil is DateTimeOffset v ? $" · VIP until {Local(v):d MMM} ({(v - now).TotalDays:0} d)" : "")
                 + (ratio is double rt ? $" · ratio {rt:0.##}" : "")
                 + (d["wedges"] is JsonNode w ? $" · {w} wedges" : "");
        sb.AppendLine(line);

        var plan = PlanBuy(b, points, ratio, vipUntil, now);
        if (plan is null) continue;
        var (spend, args, why) = plan.Value;
        var today = Local(now).ToString("yyyy-MM-dd");
        var buysToday = s["buyDay"]?.GetValue<string>() == today ? (int)(s["buysToday"]?.GetValue<double>() ?? 0) : 0;
        if (s["halted"] is not null)
            sb.AppendLine($"  ⛔ would buy {spend} ({why}), but auto-buy is HALTED: {s["halted"]}");
        else if (!autoBuy || dryRun)
            sb.AppendLine($"  💡 would buy {spend} ({why}). Auto-buy is off.");
        else if (buysToday >= (int)Num(b, "maxBuysPerDay", 1))
            sb.AppendLine($"  ⏸ would buy {spend} ({why}). Today's purchase limit has been reached.");
        else
        {
            var outcome = await MamBuy(b, cookie, args);
            s["buyDay"] = today; s["buysToday"] = buysToday + 1;
            if (outcome.Ok)
            {
                sb.AppendLine($"  ✅ bought {spend} ({why})");
                await Publish($"{name}: bought {spend}", $"{why}.\n{outcome.Detail}", 3, "shopping_cart");
            }
            else if (outcome.Known)
                sb.AppendLine($"  ✖ {spend} refused by {name}: {outcome.Detail}");
            else
            {
                s["halted"] = $"{Local(now):yyyy-MM-dd HH:mm} {spend}: {outcome.Detail}";
                sb.AppendLine($"  ⛔ unexpected answer buying {spend}. Auto-buy is HALTED.");
                await Publish($"{name}: auto-buy halted", $"Unexpected answer while buying {spend}: {outcome.Detail}\n" +
                    $"Nothing more will be bought until state.bonus.{name}.halted is removed.", 4, "warning");
            }
        }
    }
    if (sb.Length > 0)
        await Publish($"Tracker digest · {Local(now):ddd HH:mm}", sb.ToString().TrimEnd(), 1, "moneybag");
}

// The rules Christian agreed (2026-10-04), first match wins, one purchase a day:
//   1. VIP ends within vipRenewWithinDays            → VIP "max" (the API's only duration; fills to 90 d)
//   2. ratio below ratioFloor (MAM requires 1.0)      → ratioTopUpGiB of upload
//   3. points above surplusAbove                      → upload with everything over keepReserve
// Freeleech wedges are never bought: they are worth more spent by hand on a chosen torrent.
(string Spend, string Args, string Why)? PlanBuy(JsonObject b, long points, double? ratio, DateTimeOffset? vipUntil, DateTimeOffset now)
{
    var r = b["rules"]!.AsObject();
    if (vipUntil is DateTimeOffset v && (v - now).TotalDays < Num(r, "vipRenewWithinDays", 14))
        return ("VIP (max)", "spendtype=VIP&duration=max", $"VIP ends in {(v - now).TotalDays:0} days");
    if (ratio is double rt && rt < Num(r, "ratioFloor", 1.1))
    {
        var gib = (int)Num(r, "ratioTopUpGiB", 50);
        return ($"{gib} GiB upload", $"spendtype=upload&amount={gib}", $"ratio {rt:0.00} is below {Num(r, "ratioFloor", 1.1)}");
    }
    var above = Num(r, "surplusAbove", 80000);
    if (points > above)
    {
        var gib = (int)((points - Num(r, "keepReserve", 20000)) / Num(r, "pointsPerGiB", 500));
        if (gib >= 50)
            return ($"{gib} GiB upload", $"spendtype=upload&amount={gib}", $"{points:N0} points is over {above:N0}; keeping {Num(r, "keepReserve", 20000):N0}");
    }
    return null;
}

async Task<BonusRead> MamLoad(JsonObject b, string cookie)
{
    var (code, body, rotated) = await MamGet(b, cookie, "/jsonLoad.php");
    if (code is null) return new("unreachable", null, null);
    if (code is 401 or 403 || code is >= 300 and < 400) return new("expired", null, null);
    if (code != 200) { Log($"  {Str(b, "name")}: jsonLoad.php answered HTTP {code}"); return new("unreachable", null, null); }
    try
    {
        // MAM's tell for a dead session is HTML instead of JSON, with a 200.
        if (JsonNode.Parse(body!) is JsonObject o && o["seedbonus"] is not null) return new("ok", o, rotated);
    }
    catch (JsonException) { }
    return new("expired", null, null);
}

async Task<(bool Ok, bool Known, string Detail)> MamBuy(JsonObject b, string cookie, string args)
{
    var (code, body, _) = await MamGet(b, cookie, $"/json/bonusBuy.php/?{args}&_={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}");
    if (code != 200 || body is null) return (false, false, $"HTTP {code?.ToString() ?? "none"}");
    try
    {
        if (JsonNode.Parse(body) is JsonObject o && o["success"] is JsonNode ok)
        {
            var detail = string.Join(", ", o.Where(kv => kv.Key != "success").Select(kv => $"{kv.Key}={kv.Value}"));
            return ok.GetValue<bool>() ? (true, true, detail) : (false, o["error"] is not null, detail);
        }
    }
    catch (Exception) { }
    return (false, false, "not the JSON the store documents: " + Trim(body.Replace('\n', ' '), 120));
}

async Task<(int? Code, string? Body, string? Rotated)> MamGet(JsonObject b, string cookie, string path)
{
    var name = Str(b, "cookieName", "mam_id");
    using var req = new HttpRequestMessage(HttpMethod.Get, Str(b, "baseUrl").TrimEnd('/') + path);
    req.Headers.TryAddWithoutValidation("Cookie", $"{name}={cookie}");
    req.Headers.TryAddWithoutValidation("Accept", "application/json");
    // MAM answers 400 to a request with no User-Agent. Say honestly what this is.
    req.Headers.TryAddWithoutValidation("User-Agent", Str(b, "userAgent", "trackerwatch/1.0 (self-hosted)"));
    try
    {
        using var resp = await web.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        var rotated = Rotate($"{name}={cookie}", resp);
        return ((int)resp.StatusCode, body, rotated?[(name.Length + 1)..]);
    }
    catch (Exception e) { Log($"  {Str(b, "name")}: {path.Split('?')[0]} failed: {e.GetType().Name}"); return (null, null, null); }
}

static double? ParseNum(JsonNode? n) =>
    n is null ? null : double.TryParse(n.ToString(), System.Globalization.NumberStyles.Float,
                                        System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;

// ── account stats → Prometheus ─────────────────────────────────────────────────────────────
async Task ReadStats(JsonObject t, DateTimeOffset now)
{
    var name = Str(t, "name");
    Dictionary<string, double>? m = null;
    try
    {
        m = Str(t, "kind") switch
        {
            "unit3d" => await Unit3dStats(t),
            "mam"    => await MamStats(t),
            var k    => throw new Exception($"unknown stats kind '{k}'"),
        };
    }
    catch (Exception e) { Log($"  {name}: stats failed: {e.GetType().Name}: {Trim(e.Message, 120)}"); }

    var prev = metrics.TryGetValue(name, out var p) ? p : new Dictionary<string, double>();
    if (m is null)
    {
        // Keep the last good numbers but say they are stale; the alert is on staleness, not on a gap.
        prev["stats_up"] = 0; metrics[name] = prev; return;
    }
    m["ratio_minimum"] = Num(t, "ratioMinimum");
    if (t["unsatisfiedLimit"] is JsonNode ul) m["unsatisfied_limit"] = ul.GetValue<double>();
    m["stats_up"] = 1;
    m["stats_last_success_timestamp_seconds"] = now.ToUnixTimeSeconds();
    metrics[name] = m;
    Log($"  {name}: stats ratio {m.GetValueOrDefault("ratio"):0.##} (min {m["ratio_minimum"]}), " +
        $"H&R {m.GetValueOrDefault("hit_and_runs")}, bonus {m.GetValueOrDefault("bonus_points"):N0}");
}

async Task<Dictionary<string, double>> Unit3dStats(JsonObject t)
{
    // UNIT3D GET /api/user with the same Bearer key Prowlarr uses for search, read from Prowlarr so
    // a rotated key needs changing in one place only.
    var key = await ProwlarrField((int)Num(t, "prowlarrIndexerId"), "apikey");
    using var req = new HttpRequestMessage(HttpMethod.Get, Str(t, "baseUrl").TrimEnd('/') + "/api/user");
    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
    req.Headers.TryAddWithoutValidation("Accept", "application/json");
    req.Headers.TryAddWithoutValidation("User-Agent", "trackerwatch/1.0 (self-hosted)");
    using var resp = await web.SendAsync(req);
    if (!resp.IsSuccessStatusCode) throw new Exception($"HTTP {(int)resp.StatusCode}");
    var o = JsonNode.Parse(await resp.Content.ReadAsStringAsync())!.AsObject();
    var d = o["data"] as JsonObject ?? o;
    return new()
    {
        ["ratio"] = ParseNum(d["ratio"]) ?? throw new Exception("no ratio"),
        ["uploaded_bytes"] = Bytes(d["uploaded"]),
        ["downloaded_bytes"] = Bytes(d["downloaded"]),
        ["buffer_bytes"] = Bytes(d["buffer"]),
        ["bonus_points"] = ParseNum(d["seedbonus"]) ?? 0,
        ["hit_and_runs"] = ParseNum(d["hit_and_runs"]) ?? 0,
        ["seeding"] = ParseNum(d["seeding"]) ?? 0,
        ["leeching"] = ParseNum(d["leeching"]) ?? 0,
    };
}

async Task<Dictionary<string, double>> MamStats(JsonObject t)
{
    // Same session as the bonus job (its config entry carries the URL and the cookie), same endpoint,
    // plus snatch_summary: the seeding-requirement buckets that MAM's H&R rule is judged on.
    var name = Str(t, "name");
    var b = bonusTrackers.FirstOrDefault(x => Str(x, "name") == name) ?? throw new Exception("no bonus entry with the MAM session");
    var seeded = Environment.GetEnvironmentVariable(Str(b, "cookieEnv")) ?? throw new Exception($"{Str(b, "cookieEnv")} not set");
    var s = bs[name] as JsonObject ?? new JsonObject(); bs[name] = s;
    var (jar, cookie) = SessionCookie(name, seeded, s);
    var (code, body, rotated) = await MamGet(b, cookie, "/jsonLoad.php?snatch_summary");
    if (code != 200 || body is null) throw new Exception($"HTTP {code?.ToString() ?? "none"}");
    if (JsonNode.Parse(body) is not JsonObject d || d["seedbonus"] is null) throw new Exception("not JSON: session expired?");
    if (rotated is not null) KeepCookie(jar, rotated);
    var ss = d["snatch_summary"] as JsonObject ?? new JsonObject();
    double Count(string k) => ParseNum((ss[k] as JsonObject)?["count"]) ?? 0;
    var m = new Dictionary<string, double>
    {
        ["ratio"] = ParseNum(d["ratio"]) ?? throw new Exception("no ratio"),
        ["uploaded_bytes"] = ParseNum(d["uploaded_bytes"]) ?? 0,
        ["downloaded_bytes"] = ParseNum(d["downloaded_bytes"]) ?? 0,
        ["bonus_points"] = ParseNum(d["seedbonus"]) ?? 0,
        ["wedges"] = ParseNum(d["wedges"]) ?? 0,
        ["hit_and_runs"] = Count("inactHnr") + Count("seedHnr"),
        ["unsatisfied"] = Count("unsat"),
        ["seeding"] = Count("sSat") + Count("seedUnsat") + Count("seedHnr"),
        ["leeching"] = Count("leeching"),
        ["connectable"] = ss["connectable"]?.ToString() == "yes" ? 1 : 0,
    };
    if (DateTime.TryParse(d["vip_until"]?.ToString(), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var vu))
        m["vip_expiry_timestamp_seconds"] = new DateTimeOffset(vu, TimeSpan.Zero).ToUnixTimeSeconds();
    return m;
}

async Task<string> ProwlarrField(int indexerId, string field)
{
    using var req = new HttpRequestMessage(HttpMethod.Get, $"{prowlarr}/api/v1/indexer/{indexerId}");
    req.Headers.Add("X-Api-Key", apiKey);
    using var resp = await http.SendAsync(req);
    resp.EnsureSuccessStatusCode();
    var o = JsonNode.Parse(await resp.Content.ReadAsStringAsync())!.AsObject();
    return o["fields"]!.AsArray().Select(f => f!.AsObject()).FirstOrDefault(f => f["name"]?.ToString() == field)?["value"]?.ToString()
           ?? throw new Exception($"Prowlarr indexer {indexerId} has no '{field}'");
}

// "3.29 TiB" → bytes. UNIT3D formats sizes for humans; binary units, as it prints them.
static double Bytes(JsonNode? n)
{
    var m = System.Text.RegularExpressions.Regex.Match(n?.ToString() ?? "", @"^\s*(-?[\d.,]+)\s*([KMGTPE]?i?B)\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    if (!m.Success) return ParseNum(n) ?? 0;
    var v = double.Parse(m.Groups[1].Value.Replace(",", ""), System.Globalization.CultureInfo.InvariantCulture);
    var u = m.Groups[2].Value.ToUpperInvariant();
    var pow = "BKMGTPE".IndexOf(u[0]);
    return v * Math.Pow(u.Contains('I') || u.Length == 1 ? 1024 : 1000, Math.Max(0, pow));
}

async Task ServeMetrics(int port)
{
    var l = new System.Net.HttpListener();
    l.Prefixes.Add($"http://*:{port}/");
    l.Start();
    Log($"metrics on :{port}/metrics");
    while (true)
    {
        var ctx = await l.GetContextAsync();
        try
        {
            var sb = new StringBuilder();
            var names = metrics.Values.SelectMany(m => m.Keys).Distinct().OrderBy(x => x);
            foreach (var mname in names)
            {
                sb.AppendLine($"# TYPE tracker_{mname} gauge");
                foreach (var (tracker, m) in metrics.OrderBy(kv => kv.Key))
                    if (m.TryGetValue(mname, out var v))
                        sb.AppendLine($"tracker_{mname}{{tracker=\"{tracker}\"}} {v.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            }
            foreach (var (cname, v) in counters.OrderBy(kv => kv.Key))
            {
                sb.AppendLine($"# TYPE trackerwatch_{cname} {(cname.EndsWith("_total") ? "counter" : "gauge")}");
                sb.AppendLine($"trackerwatch_{cname} {v.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            }
            var bytes = Encoding.UTF8.GetBytes(ctx.Request.Url?.AbsolutePath == "/metrics" ? sb.ToString() : "see /metrics\n");
            ctx.Response.ContentType = "text/plain; version=0.0.4";
            await ctx.Response.OutputStream.WriteAsync(bytes);
        }
        catch (Exception e) { Log($"metrics: {e.GetType().Name}"); }
        finally { ctx.Response.Close(); }
    }
}

// ── placeholder season packs (see the header) ─────────────────────────────────────────────
async Task PlaceholderWatch(DateTimeOffset now)
{
    var sonarrKey = Environment.GetEnvironmentVariable("SONARR_API_KEY");
    var qbitPass = Environment.GetEnvironmentVariable("QBIT_PASSWORD");
    if (string.IsNullOrEmpty(sonarrKey) || string.IsNullOrEmpty(qbitPass)) return;
    var sonarr = Str(watchCfg!, "sonarrUrl").TrimEnd('/');
    var ws = state["placeholderWatch"] as JsonObject ?? new JsonObject(); state["placeholderWatch"] = ws;
    var done = ws["handled"] as JsonObject ?? new JsonObject(); ws["handled"] = done;

    JsonArray queue;
    try { queue = (await SonarrGet(sonarr, sonarrKey, "/api/v3/queue?pageSize=1000&includeUnknownSeriesItems=false"))!["records"]!.AsArray(); }
    catch (Exception e) { Log($"placeholder watch: Sonarr queue unreadable: {e.GetType().Name}"); return; }

    // One queue row per mapped episode; a download mapped to several is a candidate.
    var groups = queue.Select(r => r!.AsObject())
        .Where(r => r["downloadId"] is not null && r["episodeId"] is not null)
        .GroupBy(r => r["downloadId"]!.ToString())
        .Where(g => g.Select(r => r["episodeId"]!.ToString()).Distinct().Count() > 1)
        .ToList();
    var mismatched = 0; var renamed = 0;
    foreach (var g in groups)
    {
        var id = g.Key;
        var mapped = g.Select(r => r["episodeId"]!.ToString()).Distinct().Count();
        JsonArray grabs;
        try { grabs = (await SonarrGet(sonarr, sonarrKey, $"/api/v3/history?downloadId={id}&eventType=1&pageSize=200"))!["records"]!.AsArray(); }
        catch (Exception e) { Log($"placeholder watch: history for {id[..8]} unreadable: {e.GetType().Name}"); continue; }
        var grabbedEps = grabs.Select(h => h!["episodeId"]?.ToString()).Where(x => x is not null).Distinct().Count();
        var title = grabs.Select(h => h!["sourceTitle"]?.ToString()).FirstOrDefault(t => !string.IsNullOrEmpty(t));
        // A genuine season pack was GRABBED as a pack, so history covers every mapped episode.
        if (grabbedEps == 0 || title is null || mapped <= grabbedEps) continue;
        mismatched++;

        var queueTitle = g.First()["title"]?.ToString() ?? "";
        if (done[id] is JsonNode prior)
        {
            if (prior.ToString() == "renamed" && queueTitle == title)
            {
                // Renamed already, and Sonarr still maps it wide: the grabbed title itself must parse
                // as a pack. Renaming again would change nothing, so say so once.
                Log($"placeholder watch: {id[..8]} still maps {mapped} episodes after the rename to its grabbed title; leaving it");
                done[id] = "unfixable";
            }
            continue;
        }
        // Only when the torrent really holds no more videos than were grabbed. A genuine season
        // pack that an indexer listed as one episode would lose its other episodes to a rename.
        var videos = await QbitVideoCount(id.ToLowerInvariant(), qbitPass);
        if (videos is null) continue;
        if (videos > grabbedEps)
        {
            Log($"placeholder watch: {id[..8]} holds {videos} videos but was grabbed as {grabbedEps} episode(s); a real pack, leaving it");
            done[id] = "pack"; continue;
        }
        var line = $"{id[..8]}: \"{Trim(queueTitle, 60)}\" maps {mapped} episodes, grabbed as {grabbedEps}: \"{Trim(title, 70)}\"";
        if (dryRun || (watchCfg!["dryRun"]?.GetValue<bool>() ?? false)) { Log($"placeholder watch: DRY RUN, would rename {line}"); continue; }
        if (await QbitRename(id.ToLowerInvariant(), title, qbitPass))
        {
            done[id] = "renamed"; renamed++;
            counters.AddOrUpdate("placeholder_renames_total", 1, (_, v) => v + 1);
            Log($"placeholder watch: renamed {line}");
        }
    }
    counters["placeholder_mismatched_downloads"] = mismatched;
    counters["placeholder_last_run_timestamp_seconds"] = now.ToUnixTimeSeconds();

    // Forget downloads that have left the queue, so the state file doesn't grow forever.
    var live = queue.Select(r => r!["downloadId"]?.ToString()).Where(x => x is not null).ToHashSet();
    foreach (var k in done.Select(kv => kv.Key).Where(k => !live.Contains(k)).ToList()) done.Remove(k);

    if (renamed > 0)
    {
        try { await SonarrPost(sonarr, sonarrKey, "/api/v3/command", new JsonObject { ["name"] = "RefreshMonitoredDownloads" }); }
        catch (Exception e) { Log($"placeholder watch: refresh failed: {e.GetType().Name}"); }
    }
    Save();
}

async Task<JsonNode?> SonarrGet(string baseUrl, string key, string path)
{
    using var req = new HttpRequestMessage(HttpMethod.Get, baseUrl + path);
    req.Headers.Add("X-Api-Key", key);
    using var resp = await http.SendAsync(req);
    resp.EnsureSuccessStatusCode();
    return JsonNode.Parse(await resp.Content.ReadAsStringAsync());
}

async Task SonarrPost(string baseUrl, string key, string path, JsonObject body)
{
    using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + path)
        { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
    req.Headers.Add("X-Api-Key", key);
    using var resp = await http.SendAsync(req);
    resp.EnsureSuccessStatusCode();
}

async Task<int?> QbitVideoCount(string hash, string password)
{
    var resp = await QbitCall(HttpMethod.Get, $"/api/v2/torrents/files?hash={hash}", null, password);
    if (resp is null || !resp.IsSuccessStatusCode) { Log($"placeholder watch: files of {hash[..8]} unreadable"); return null; }
    var ext = new[] { ".mkv", ".mp4", ".avi", ".ts", ".m2ts", ".mov", ".wmv" };
    return JsonNode.Parse(await resp.Content.ReadAsStringAsync())!.AsArray()
        .Count(f => ext.Any(e => f!["name"]!.ToString().EndsWith(e, StringComparison.OrdinalIgnoreCase)));
}

async Task<bool> QbitRename(string hash, string name, string password)
{
    var resp = await QbitCall(HttpMethod.Post, "/api/v2/torrents/rename",
        new Dictionary<string, string> { ["hash"] = hash, ["name"] = name }, password);
    if (resp is not null && resp.IsSuccessStatusCode) return true;
    Log($"placeholder watch: rename {hash[..8]} → HTTP {(resp is null ? "none" : (int)resp.StatusCode)}");
    return false;
}

// qBittorrent WebUI API with its own cookie session. Logs in lazily and again on a 403.
async Task<HttpResponseMessage?> QbitCall(HttpMethod method, string path, Dictionary<string, string>? form, string password)
{
    var baseUrl = Str(watchCfg!, "qbitUrl").TrimEnd('/');
    for (var attempt = 0; attempt < 2; attempt++)
    {
        if (qbit is null)
        {
            qbit = new HttpClient(new HttpClientHandler { CookieContainer = new System.Net.CookieContainer() }) { Timeout = TimeSpan.FromSeconds(30) };
            var login = await qbit.PostAsync(baseUrl + "/api/v2/auth/login", new FormUrlEncodedContent(new Dictionary<string, string>
                { ["username"] = Str(watchCfg!, "qbitUser", "admin"), ["password"] = password }));
            // qBittorrent 5.2 answers a good login with 204 and no body; older ones with 200 "Ok.".
            // A bad one is 200 "Fails." (or 403 once the IP is banned), so test for the failure.
            var body = await login.Content.ReadAsStringAsync();
            if (!login.IsSuccessStatusCode || body.StartsWith("Fails")) { Log($"placeholder watch: qBittorrent login refused (HTTP {(int)login.StatusCode})"); qbit = null; return null; }
        }
        using var req = new HttpRequestMessage(method, baseUrl + path) { Content = form is null ? null : new FormUrlEncodedContent(form) };
        var resp = await qbit.SendAsync(req);
        if ((int)resp.StatusCode == 403) { qbit = null; continue; }        // session expired: log in again
        return resp;
    }
    return null;
}

// ── digest ────────────────────────────────────────────────────────────────────────────────
async Task SendDigest(List<Observation> obs, DateTimeOffset now)
{
    var sb = new StringBuilder(); int listed = 0;
    foreach (var o in obs.Where(o => o.Cfg["digest"]?.GetValue<bool>() ?? true))
    {
        var st = (ts[o.Name] as JsonObject)?["state"]?.GetValue<string>();
        if (!o.Reachable) { sb.AppendLine($"⚠️ {o.Name}: not reachable this run"); continue; }
        if (st == "active")
        {
            // Everything is free during an event, so a list would just be "everything". One line.
            sb.AppendLine($"🟢 {o.Name}: site-wide freeleech is on; everything is free");
            continue;
        }
        // A torrent still inside the tracker's standing new-upload promo is free by policy, not
        // news (SoulVoice frees every upload for its first 7 days). Double upload always counts.
        var promo = Num(o.Cfg, "newUploadPromoHours", 0);
        var picks = o.Rows.Where(r => Flags(r).Contains("doubleupload")
                                   || (Flags(r).Contains("freeleech") && Age(r) >= promo))
                          .OrderByDescending(r => Flags(r).Contains("doubleupload"))
                          .ThenBy(Age).Take(perTracker).ToList();
        if (picks.Count == 0) continue;
        sb.AppendLine($"{o.Name}:");
        foreach (var r in picks)
        {
            var f = Flags(r);
            var tag = f.Contains("doubleupload") ? (f.Contains("freeleech") ? "FL ×2 " : "×2 ") : "FL ";
            sb.AppendLine($"  {tag}{Trim(r["title"]!.ToString(), 64)}  ·  {Size(r)}  ·  {AgeText(r)}");
            listed++;
        }
    }
    if (sb.Length == 0 && !sendEmpty) { Log("digest: nothing to report, not sending"); return; }
    await Publish($"Freeleech digest · {Local(now):ddd HH:mm}",
        sb.Length == 0 ? "Nothing freeleech on the watched trackers." : sb.ToString().TrimEnd(), 1, "package");
    Log($"digest: sent, {listed} torrent(s) listed");
}

// ── publishing ────────────────────────────────────────────────────────────────────────────
async Task Publish(string title, string body, int priority, string tag)
{
    if (dryRun) { Log($"DRY RUN, would publish p{priority} [{title}]\n{body}"); return; }
    // JSON publish to ntfy's root URL, not headers on /<topic>: titles here carry non-ASCII
    // (·, CJK release names) and HttpClient refuses non-ASCII header values outright.
    var msg = new JsonObject { ["topic"] = ntfyTopic, ["title"] = title, ["message"] = body,
                               ["priority"] = priority, ["tags"] = new JsonArray(tag) };
    using var req = new HttpRequestMessage(HttpMethod.Post, ntfyUrl + "/")
        { Content = new StringContent(msg.ToJsonString(), Encoding.UTF8, "application/json") };
    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ntfyToken);
    try
    {
        using var resp = await http.SendAsync(req);
        Log($"published p{priority} [{title}] → HTTP {(int)resp.StatusCode}");
        if (!resp.IsSuccessStatusCode) Log("  " + Trim(await resp.Content.ReadAsStringAsync(), 200));
    }
    catch (Exception e) { Log($"publish failed [{title}]: {e.GetType().Name}: {e.Message}"); }
}

// ── helpers ───────────────────────────────────────────────────────────────────────────────
bool DigestSlotPassedSince(DateTimeOffset last, DateTimeOffset now) => SlotPassedSince(digestAt, last, now);
bool SlotPassedSince(List<TimeOnly> times, DateTimeOffset last, DateTimeOffset now)
{
    // Due if any configured local time-of-day fell in (last, now]. Robust to the container
    // being down over a slot: it sends once on the next tick rather than catching up repeatedly.
    var day = DateOnly.FromDateTime(Local(last).Date);
    for (var d = day; d <= DateOnly.FromDateTime(Local(now).Date); d = d.AddDays(1))
        foreach (var t in times)
        {
            var slot = new DateTimeOffset(d.ToDateTime(t), tz.GetUtcOffset(d.ToDateTime(t)));
            if (slot > last && slot <= now) return true;
        }
    return false;
}
DateTime Local(DateTimeOffset t) => TimeZoneInfo.ConvertTime(t, tz).DateTime;
void Save()
{
    Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
    var tmp = statePath + ".tmp";
    File.WriteAllText(tmp, state.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    File.Move(tmp, statePath, overwrite: true);                    // atomic: a crash never leaves half a file
}
static HashSet<string> Flags(JsonObject r) =>
    (r["indexerFlags"] as JsonArray)?.Select(f => f!.ToString()).ToHashSet() ?? new();
static double Age(JsonObject r) => r["ageHours"]?.GetValue<double>() ?? 0;
static string AgeText(JsonObject r) { var h = Age(r); return h < 48 ? $"{h:0}h" : $"{h / 24:0}d"; }
static string Size(JsonObject r) { var b = r["size"]?.GetValue<long>() ?? 0; return b >= 1e9 ? $"{b / 1e9:0.0} GB" : $"{b / 1e6:0} MB"; }
static string Trim(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";
static string Str(JsonObject o, string k, string? def = null) => o[k]?.GetValue<string>() ?? def ?? throw new Exception($"config: missing '{k}'");
static double Num(JsonObject o, string k, double? def = null) => o[k] is JsonNode n ? n.GetValue<double>() : def ?? throw new Exception($"config: missing '{k}'");
static string Need(string k) => Environment.GetEnvironmentVariable(k) is { Length: > 0 } v ? v : throw new Exception($"{k} is not set");
static void Log(string m) => Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {m}");

record Observation(JsonObject Cfg, string Name, bool Reachable, int Unique, int Old, int OldFree, bool Determinate, List<JsonObject> Rows);
record BonusRead(string Verdict, JsonObject? Data, string? Rotated);
