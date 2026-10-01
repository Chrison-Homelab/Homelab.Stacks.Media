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
// Trackers that are freeleech ALL the time (LST, Milkie) are deliberately absent from the
// config: nothing about them is ever news.
//
// Env:  PROWLARR_API_KEY, NTFY_TOKEN (required)
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

var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
var state = File.Exists(statePath) ? JsonNode.Parse(File.ReadAllText(statePath))!.AsObject() : new JsonObject();
var ts = state["trackers"] as JsonObject ?? new JsonObject(); state["trackers"] = ts;

Log($"trackerwatch up: {trackers.Count} tracker(s), events every {eventEvery.TotalHours}h, digest at "
    + string.Join(",", digestAt.Select(t => t.ToString("HH:mm"))) + $" {tz.Id}{(dryRun ? ", DRY RUN" : "")}");

while (true)
{
    var now = DateTimeOffset.UtcNow;
    var nextEvent = state["nextEventCheck"] is JsonNode ne ? DateTimeOffset.Parse(ne.GetValue<string>()) : now;
    var lastDigest = state["lastDigest"] is JsonNode ld ? DateTimeOffset.Parse(ld.GetValue<string>()) : now;
    if (state["lastDigest"] is null) state["lastDigest"] = now.ToString("o");   // never digest retroactively on first start
    var digestDue = runOnce || DigestSlotPassedSince(lastDigest, now);

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
async Task<Observation> Observe(JsonObject t)
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

async Task AssertUnreachable(Observation o, DateTimeOffset since)
{
    // Re-asserted on every run while it holds, with endsAt just past the next run: Alertmanager
    // keeps alerts in memory only (#602), so a one-shot alert would not survive a restart.
    var alert = new JsonArray(new JsonObject
    {
        ["labels"] = new JsonObject { ["alertname"] = "TrackerUnreachable", ["severity"] = "warning",
            ["stack"] = "media", ["service"] = o.Name.ToLowerInvariant(), ["instance"] = o.Name },
        ["annotations"] = new JsonObject {
            ["summary"] = $"{o.Name} has not answered through Prowlarr since {Local(since):ddd HH:mm}",
            ["description"] = "Every freeleech search for this tracker failed. Check the indexer in Prowlarr " +
                              "(credentials, cookie, or the site itself)." },
        ["startsAt"] = since.ToString("o"),
        ["endsAt"] = (DateTimeOffset.UtcNow + eventEvery + TimeSpan.FromHours(1)).ToString("o"),
    });
    if (dryRun) { Log($"  DRY RUN, would assert on the bus: {alert.ToJsonString()}"); return; }
    try
    {
        using var resp = await http.PostAsync($"{amUrl}/api/v2/alerts",
            new StringContent(alert.ToJsonString(), Encoding.UTF8, "application/json"));
        Log($"  {o.Name}: TrackerUnreachable asserted (HTTP {(int)resp.StatusCode})");
    }
    catch (Exception e) { Log($"  {o.Name}: could not reach Alertmanager: {e.GetType().Name}"); }
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
bool DigestSlotPassedSince(DateTimeOffset last, DateTimeOffset now)
{
    // Due if any configured local time-of-day fell in (last, now]. Robust to the container
    // being down over a slot: it sends once on the next tick rather than catching up repeatedly.
    var day = DateOnly.FromDateTime(Local(last).Date);
    for (var d = day; d <= DateOnly.FromDateTime(Local(now).Date); d = d.AddDays(1))
        foreach (var t in digestAt)
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
