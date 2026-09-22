#!/usr/bin/env dotnet
#:package Spectre.Console@0.49.1

// seedonly — classify every live qBittorrent torrent as HOT, COLD or DELETABLE.
//
// WHY THIS EXISTS: the rules are subtle enough that doing it by hand gets it wrong.
// It already did — 12 torrents holding content Plex serves were moved to cold storage
// because the first attempt tested "is it hardlinked?" instead of "is it in Plex?".
// Those are different questions whenever an import crossed a filesystem boundary.
// docs/seedonly-rules.md is the prose; this file is the authority.
//
// READ-ONLY. It changes nothing: no category is set, no file is deleted. It prints what
// the rules say and stops. Acting on the output is a separate, deliberate step.
//
// WHERE IT RUNS: anywhere with dotnet. The NFS exports are mounted on the Proxmox host
// only (never inside a guest — ADR/BL-016), and the hypervisor has no dotnet, so the
// filesystem facts come over SSH in two `find` passes. Same shape as the converge
// engine's NodeExec: logic local, facts remote.

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using Spectre.Console;

// ── configuration ────────────────────────────────────────────────────────────────────
// Everything is overridable by environment so this works against a rebuilt fleet without
// an edit. Defaults match the live stack.
var node      = Env("SEEDONLY_NODE",  "root@hpe-01.homelab.chrison.internal");
var qbitUrl   = Env("SEEDONLY_QBIT",  "http://10.10.110.115:8090");
var plexUrl   = Env("SEEDONLY_PLEX",  "http://10.10.200.98:32400");
var qbitUser  = Env("QBIT_USER",      "");
var qbitPass  = Env("QBIT_PASSWORD",  "");
var plexToken = Env("PLEX_TOKEN",     "");
var sonarrUrl = Env("SEEDONLY_SONARR", "http://10.10.250.29:8989");
var sonarrKey = Env("SONARR_API_KEY",  "");
// Watched is not enough on its own: an episode watched last night may be mid-rewatch.
// Only content untouched for this long is considered finished with.
var staleDays = int.Parse(Env("SEEDONLY_STALE_DAYS", "30"), CultureInfo.InvariantCulture);
// Batches are capped by SIZE, not count. qBittorrent marks every torrent in a batch
// `moving` at once and then copies them one at a time, so the cap is really a cap on
// how long the last torrent in the batch sits unseeded.
var batchGb   = double.Parse(Env("SEEDONLY_BATCH_GB", "75"), CultureInfo.InvariantCulture);
var apply     = Environment.GetCommandLineArgs().Concat(args).Any(a => a == "--apply");
var dryRun    = Environment.GetCommandLineArgs().Concat(args).Any(a => a == "--dry-run");
var maxBatches= int.Parse(Env("SEEDONLY_MAX_BATCHES", "0"), CultureInfo.InvariantCulture);

const string V3 = "/mnt/pve/ds1813-nfs-volume-3";
const string V4 = "/mnt/pve/ds1813-nfs-volume-4";

// Every path an app actually SERVES from, on BOTH volumes. Plex's TV and Movie libraries
// each carry two paths, one per volume, which is the whole reason a volume4 torrent can
// have its library counterpart on volume3 as an unlinked copy.
string[] libraryRoots =
[
    $"{V4}/data/media", $"{V3}/data/media", $"{V4}/audiobooks", $"{V4}/ebooks",
    $"{V4}/books", $"{V4}/roms", $"{V4}/youtube", $"{V4}/library",
];
string[] torrentRoots = [$"{V4}/data/torrents", $"{V4}/data/usenet", $"{V3}/seedonly-torrents"];

// Scope, per the operator's rules. Movies are deliberately excluded: a film is watched
// once and kept, so "watched" does not imply "done with it" the way it does for an episode.
var inScope = new HashSet<string>(StringComparer.Ordinal) { "tv-sonarr" };

// Trackers to leave alone entirely, matched by HOST.
//
// It must be the host, not the category. Sonarr sets a category per APPLICATION, so
// anything it grabs from the SoulVoice indexer arrives tagged `tv-sonarr`, not
// `soulvoice` — six torrents are in exactly that state today, excluded only by the
// 30-day rule and due to slip through once they age.
//
// Only the host is ever compared or printed: a private tracker's announce URL carries
// the account PASSKEY as a query parameter, and it must not reach a log or a table.
var excludedTrackerHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "pt.soulvoice.club" };
var minSeedDays   = int.Parse(Env("SEEDONLY_MIN_SEED_DAYS", "30"), CultureInfo.InvariantCulture);

if (qbitUser.Length == 0 || qbitPass.Length == 0)
{
    AnsiConsole.MarkupLine("[red]QBIT_USER / QBIT_PASSWORD not set — source secrets.env first.[/]");
    return 2;
}

// `--list=VERDICT` switches to TSV output for batching. Detected up front so the
// progress chatter is suppressed — it goes to stdout and would otherwise corrupt the list.
var listWanted = Environment.GetCommandLineArgs().Concat(args)
    .FirstOrDefault(a => a.StartsWith("--list=", StringComparison.Ordinal))?["--list=".Length..];
void Say(string m) { if (listWanted is null) AnsiConsole.MarkupLine(m); }

// ── 1. filesystem facts, over SSH ────────────────────────────────────────────────────
// One find per root set. `%i %n %s %p` = inode, link count, size, path. Parsing is
// positional on the first three fields so a path containing spaces survives intact.
Say($"[grey]scanning library roots on[/] {node}…");
var libFiles = await FindAsync(node, libraryRoots);
Say("[grey]scanning torrent roots…[/]");
var torFiles = await FindAsync(node, torrentRoots);

var libInodes = libFiles.Select(f => f.Inode).ToHashSet();
var libByInode = libFiles.GroupBy(f => f.Inode).ToDictionary(g => g.Key, g => g.First().Path);
var libSizes  = libFiles.Select(f => f.Size).ToHashSet();
var byPath    = torFiles.ToLookup(f => f.Path);

// ── 2. qBittorrent ───────────────────────────────────────────────────────────────────
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
var login = await http.PostAsync($"{qbitUrl}/api/v2/auth/login",
    new FormUrlEncodedContent([new("username", qbitUser), new("password", qbitPass)]));
if (!login.IsSuccessStatusCode) { AnsiConsole.MarkupLine("[red]qBittorrent login failed[/]"); return 2; }
if (login.Headers.TryGetValues("Set-Cookie", out var ck))
    http.DefaultRequestHeaders.Add("Cookie", string.Join("; ", ck.Select(c => c.Split(';')[0])));

using var torJson = JsonDocument.Parse(await http.GetStringAsync($"{qbitUrl}/api/v2/torrents/info"));

// hash -> tracker hosts, from ONE maindata call rather than a per-torrent round trip.
// `trackers` is {announce url -> [hashes]}; only the host is kept, deliberately.
var trackerHosts = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
using (var main = JsonDocument.Parse(await http.GetStringAsync($"{qbitUrl}/api/v2/sync/maindata?rid=0")))
{
    if (main.RootElement.TryGetProperty("trackers", out var tks))
        foreach (var tk in tks.EnumerateObject())
        {
            var host = Uri.TryCreate(tk.Name, UriKind.Absolute, out var u) ? u.Host : "";
            if (host.Length == 0) continue;
            foreach (var hv in tk.Value.EnumerateArray())
                if (hv.GetString() is { Length: > 0 } hh)
                    (trackerHosts.TryGetValue(hh, out var set) ? set : trackerHosts[hh] = new(StringComparer.OrdinalIgnoreCase)).Add(host);
        }
}

// ── 3. Plex watched state — the ADMIN account only ───────────────────────────────────
// viewCount is per Plex account. This token is the admin's, so "watched" means the
// operator watched it; a managed user's state is invisible here and is NOT considered.
var watchedSizes = new HashSet<long>();
var watchedPaths = new HashSet<string>(StringComparer.Ordinal);
// path -> how many days since it was last played. Absent = never watched.
var lastViewed = new Dictionary<string, int>(StringComparer.Ordinal);
if (plexToken.Length > 0)
{
    var sections = XDocument.Parse(await http.GetStringAsync($"{plexUrl}/library/sections?X-Plex-Token={plexToken}"));
    foreach (var dir in sections.Root!.Elements("Directory").Where(d => (string?)d.Attribute("type") == "show"))
    {
        var key = (string?)dir.Attribute("key");
        var eps = XDocument.Parse(await http.GetStringAsync(
            $"{plexUrl}/library/sections/{key}/all?type=4&X-Plex-Token={plexToken}"));
        foreach (var v in eps.Root!.Elements("Video"))
        {
            if (int.Parse((string?)v.Attribute("viewCount") ?? "0", CultureInfo.InvariantCulture) == 0) continue;
            var lv = long.TryParse((string?)v.Attribute("lastViewedAt"), out var lvv) ? lvv : 0;
            var age = lv > 0 ? (int)((DateTimeOffset.UtcNow.ToUnixTimeSeconds() - lv) / 86400) : -1;
            foreach (var part in v.Descendants("Part"))
            {
                if (long.TryParse((string?)part.Attribute("size"), out var s)) watchedSizes.Add(s);
                if ((string?)part.Attribute("file") is { Length: > 0 } f)
                {
                    watchedPaths.Add(f);
                    if (age >= 0) lastViewed[f] = age;
                }
            }
        }
    }
}
else Say("[yellow]PLEX_TOKEN unset — watched-state rules will not fire.[/]");

// ── 4. classify ──────────────────────────────────────────────────────────────────────
var results = new List<Row>();
foreach (var t in torJson.RootElement.EnumerateArray())
{
    var hash  = t.GetProperty("hash").GetString()!;
    var cat   = t.GetProperty("category").GetString() ?? "";
    var size  = t.GetProperty("size").GetInt64();
    var priv  = t.TryGetProperty("private", out var pv) && pv.ValueKind == JsonValueKind.True;
    var days  = (int)(t.GetProperty("seeding_time").GetInt64() / 86400);
    var up    = t.GetProperty("upspeed").GetInt64();
    var leech = t.GetProperty("num_leechs").GetInt32();
    var cpath = t.GetProperty("content_path").GetString() ?? "";
    var name  = t.GetProperty("name").GetString() ?? "";

    // Guest path -> host path. /data is volume4's export; /seedonly-torrents is volume3's.
    var host = cpath.StartsWith("/data", StringComparison.Ordinal) ? $"{V4}{cpath}"
             : cpath.StartsWith("/seedonly-torrents", StringComparison.Ordinal) ? $"{V3}{cpath}"
             : cpath;

    var files = torFiles.Where(f => f.Path == host || f.Path.StartsWith(host + "/", StringComparison.Ordinal)).ToList();
    if (files.Count == 0) { results.Add(new(hash, name, cat, size, priv, days, "UNRESOLVED", "content path not found on disk", [], host)); continue; }

    // IN PLEX, tested two ways. A cross-filesystem import COPIES rather than hardlinks,
    // and a copy is every bit as much "in Plex" as a link — missing that is exactly how
    // 12 torrents were wrongly cold-stored.
    var linked = files.Any(f => libInodes.Contains(f.Inode));
    var copied = files.Any(f => libSizes.Contains(f.Size));
    var inPlex = linked || copied;

    // WATCHED AND STALE, judged on the library files this torrent actually backs.
    // Size-matching alone would call a copy-import watched without knowing which copy
    // Plex played, so prefer the hardlinked path and fall back to size only when there
    // is no link to follow.
    var backing = files.Where(f => libByInode.ContainsKey(f.Inode))
                       .Select(f => ToGuestPath(libByInode[f.Inode])).Distinct().ToList();
    bool StaleEnough(string guestPath) =>
        lastViewed.TryGetValue(guestPath, out var d) && d >= staleDays;
    var watched = backing.Count > 0
        ? backing.All(StaleEnough)                       // every backed episode must be done with
        : files.Any(f => watchedSizes.Contains(f.Size)); // copy-import: no link to follow

    var hosts = trackerHosts.TryGetValue(hash, out var hs) ? hs : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var excludedTracker = hosts.FirstOrDefault(excludedTrackerHosts.Contains);

    string verdict, why;
    if (excludedTracker is not null)           { verdict = "EXCLUDED"; why = $"excluded tracker ({excludedTracker})"; }
    else if (!inScope.Contains(cat))           { verdict = "EXCLUDED"; why = $"out of scope: {cat}"; }
    else if (days < minSeedDays)               { verdict = "EXCLUDED"; why = $"seeded {days}d (< {minSeedDays})"; }
    else if (up > 0 || leech > 0)              { verdict = "EXCLUDED"; why = "currently uploading"; }
    else if (inPlex && !watched)               { verdict = "HOT";      why = linked ? "in Plex (hardlinked), unwatched" : "in Plex (copy import), unwatched"; }
    else if (inPlex && watched && !priv)       { verdict = "DELETABLE"; why = "watched, public tracker — re-downloadable"; }
    else if (inPlex && watched)                { verdict = "COLD";     why = "watched — unmonitor, drop from Plex, seed from volume3"; }
    else if (!priv)                            { verdict = "DELETABLE"; why = "not in Plex, public tracker"; }
    else                                       { verdict = "COLD";     why = "not in Plex, private tracker"; }

    results.Add(new(hash, name, cat, size, priv, days, verdict, why, backing, host));
}

// ── 5. --apply: Plex → Sonarr → move, in size-capped batches ─────────────────────────
// The order is the whole point and it is easy to get wrong. Moving a torrent that is still
// hardlinked into the library BREAKS the link, leaving two full copies instead of one — it
// cost ~70 GB to learn. So per batch: delete the library file through Sonarr FIRST, confirm
// the torrent is no longer linked, and only then hand it to qBittorrent.
//
// Re-runnable by design: each run re-derives everything from live state, so it can be run
// again and again until the COLD list is empty.
if (apply)
{
    if (sonarrKey.Length == 0) { AnsiConsole.MarkupLine("[red]SONARR_API_KEY not set[/]"); return 2; }

    // Sonarr's episode files, keyed by the absolute path Sonarr uses.
    var fileIdByPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    async Task<string> Son(string path, HttpMethod? m = null)
    {
        var rq = new HttpRequestMessage(m ?? HttpMethod.Get, $"{sonarrUrl}/api/v3/{path}");
        rq.Headers.Add("X-Api-Key", sonarrKey);
        var rs = await http.SendAsync(rq);
        return rs.IsSuccessStatusCode ? await rs.Content.ReadAsStringAsync() : $"!{(int)rs.StatusCode}";
    }
    using (var series = JsonDocument.Parse(await Son("series")))
        foreach (var sv in series.RootElement.EnumerateArray())
        {
            var sid = sv.GetProperty("id").GetInt32();
            var spath = sv.GetProperty("path").GetString()!;
            var body = await Son($"episodefile?seriesId={sid}");
            if (body.StartsWith('!')) continue;
            using var efs = JsonDocument.Parse(body);
            foreach (var ef in efs.RootElement.EnumerateArray())
                fileIdByPath[$"{spath}/{ef.GetProperty("relativePath").GetString()}"] = ef.GetProperty("id").GetInt32();
        }
    AnsiConsole.MarkupLine($"[grey]Sonarr episode files indexed: {fileIdByPath.Count}[/]");

    var queue = results.Where(r => r.Verdict == "COLD").OrderByDescending(r => r.Size).ToList();
    AnsiConsole.MarkupLine($"[grey]COLD queue: {queue.Count} torrents, {queue.Sum(r => r.Size) / 1e9:F1} GB[/]");

    var cap = (long)(batchGb * 1e9);
    var batches = new List<List<Row>>(); var cur = new List<Row>(); long run = 0;
    foreach (var r in queue)
    {
        if (run + r.Size > cap && cur.Count > 0) { batches.Add(cur); cur = []; run = 0; }
        cur.Add(r); run += r.Size;
    }
    if (cur.Count > 0) batches.Add(cur);
    if (maxBatches > 0) batches = batches.Take(maxBatches).ToList();

    var bn = 0;
    foreach (var b in batches)
    {
        bn++;
        AnsiConsole.MarkupLine($"\n[bold]batch {bn}/{batches.Count}[/] — {b.Count} torrent(s), {b.Sum(r => r.Size) / 1e9:F1} GB");
        var ready = new List<Row>();
        foreach (var r in b)
        {
            // Step 2: drop every library file this torrent backs, through Sonarr so its
            // database stays consistent. A file Sonarr does not know about is left alone
            // and the torrent is skipped rather than moved while Plex still serves it.
            var unknown = r.Backing.Where(p => !fileIdByPath.ContainsKey(p)).ToList();
            if (r.Backing.Count > 0 && unknown.Count > 0)
            {
                AnsiConsole.MarkupLine($"  [yellow]skip[/] {Markup.Escape(Trim(r.Name, 46))} — Sonarr does not manage {unknown.Count} backing file(s)");
                continue;
            }
            var okAll = true;
            foreach (var gp in r.Backing)
            {
                if (dryRun) { AnsiConsole.MarkupLine($"  [grey]would delete[/] {Markup.Escape(Trim(gp, 62))}"); continue; }
                var res = await Son($"episodefile/{fileIdByPath[gp]}", HttpMethod.Delete);
                if (res.StartsWith('!')) { AnsiConsole.MarkupLine($"  [red]delete failed[/] {Markup.Escape(Trim(gp, 52))} {res}"); okAll = false; }
            }
            if (okAll) ready.Add(r);
        }
        if (ready.Count == 0) { AnsiConsole.MarkupLine("  nothing ready in this batch"); continue; }
        if (dryRun) { AnsiConsole.MarkupLine($"  [grey]would move {ready.Count} torrent(s)[/]"); continue; }

        // Step 3: re-read the filesystem and refuse to move anything still linked.
        var fresh = (await FindAsync(node, libraryRoots)).Select(f => f.Inode).ToHashSet();
        var moving = new List<Row>();
        foreach (var r in ready)
        {
            var tf = (await FindAsync(node, [r.ContentPath])).ToList();
            if (tf.Any(f => fresh.Contains(f.Inode)))
                AnsiConsole.MarkupLine($"  [red]REFUSE[/] {Markup.Escape(Trim(r.Name, 46))} — still hardlinked into a library");
            else moving.Add(r);
        }
        if (moving.Count == 0) { AnsiConsole.MarkupLine("  nothing safe to move"); continue; }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await http.PostAsync($"{qbitUrl}/api/v2/torrents/setCategory",
            new FormUrlEncodedContent([new("hashes", string.Join("|", moving.Select(m => m.Hash))), new("category", "seed-only")]));
        while (true)
        {
            await Task.Delay(20000);
            using var st = JsonDocument.Parse(await http.GetStringAsync(
                $"{qbitUrl}/api/v2/torrents/info?hashes={string.Join("|", moving.Select(m => m.Hash))}"));
            if (!st.RootElement.EnumerateArray().Any(t => t.GetProperty("state").GetString() == "moving")) break;
        }
        using var final = JsonDocument.Parse(await http.GetStringAsync(
            $"{qbitUrl}/api/v2/torrents/info?hashes={string.Join("|", moving.Select(m => m.Hash))}"));
        var arr = final.RootElement.EnumerateArray().ToList();
        var errs = arr.Count(t => t.GetProperty("state").GetString() is "error" or "missingFiles");
        var landed = arr.Count(t => t.GetProperty("save_path").GetString()!.StartsWith("/seedonly-torrents", StringComparison.Ordinal));
        AnsiConsole.MarkupLine($"  moved {landed}/{moving.Count} in {sw.Elapsed.TotalSeconds:F0}s, errors {errs}");
        if (errs > 0) { AnsiConsole.MarkupLine("[red]aborting — a torrent is in an error state[/]"); return 1; }
    }
    AnsiConsole.MarkupLine("\n[green]apply complete[/] — re-run to continue down the list.");
    return 0;
}

// ── 6. machine-readable list, for batching ───────────────────────────────────────────
// `--list COLD` prints hash/size/days/name as TSV, largest first, and nothing else.
// It exists so a batch is selected BY THE RULES rather than by someone pattern-matching
// torrent names: a hand-written filter for "Bridgerton" also catches Queen Charlotte and
// three S04 episodes that are HOT, which is precisely the mistake this tool is for.
if (listWanted is { } want)
{
    foreach (var r in results.Where(r => string.Equals(r.Verdict, want, StringComparison.OrdinalIgnoreCase))
                             .OrderByDescending(r => r.Size))
        Console.WriteLine($"{r.Hash}\t{r.Size}\t{r.Days}\t{r.Name}");
    return 0;
}

// ── 7. report ────────────────────────────────────────────────────────────────────────
var summary = new Table().Border(TableBorder.Rounded).Title("[bold]seed-only classification[/]");
summary.AddColumn("verdict"); summary.AddColumn(new TableColumn("torrents").RightAligned());
summary.AddColumn(new TableColumn("GB").RightAligned()); summary.AddColumn("meaning");
foreach (var v in new[] { "HOT", "COLD", "DELETABLE", "EXCLUDED", "UNRESOLVED" })
{
    var g = results.Where(r => r.Verdict == v).ToList();
    if (g.Count == 0) continue;
    summary.AddRow(Colour(v), g.Count.ToString(), $"{g.Sum(r => r.Size) / 1e9:F1}",
        Markup.Escape(string.Join("; ", g.GroupBy(r => r.Why).OrderByDescending(x => x.Count()).Select(x => $"{x.Key} ({x.Count()})"))));
}
AnsiConsole.Write(summary);

foreach (var v in new[] { "COLD", "DELETABLE" })
{
    var g = results.Where(r => r.Verdict == v).OrderByDescending(r => r.Size).ToList();
    if (g.Count == 0) continue;
    var t = new Table().Border(TableBorder.Minimal).Title($"[bold]{Colour(v)}[/] — {g.Count} torrent(s), {g.Sum(r => r.Size) / 1e9:F1} GB");
    t.AddColumn(new TableColumn("GB").RightAligned()); t.AddColumn(new TableColumn("days").RightAligned());
    t.AddColumn("tracker"); t.AddColumn("name");
    foreach (var r in g)
        t.AddRow($"{r.Size / 1e9:F2}", r.Days.ToString(), r.Private ? "private" : "public", Markup.Escape(Trim(r.Name, 58)));
    AnsiConsole.Write(t);
}

AnsiConsole.MarkupLine("\n[grey]read-only — nothing was changed. See docs/seedonly-rules.md for the decision table.[/]");
return 0;

// ── helpers ──────────────────────────────────────────────────────────────────────────
// Host path -> the path Plex and Sonarr see inside their containers. volume4's export is
// mounted at /data in every arr and in Plex; volume3's library is Plex-only at /mnt/media.
static string ToGuestPath(string host) =>
    host.StartsWith("/mnt/pve/ds1813-nfs-volume-4", StringComparison.Ordinal)
        ? host["/mnt/pve/ds1813-nfs-volume-4".Length..]
    : host.StartsWith("/mnt/pve/ds1813-nfs-volume-3/data/media", StringComparison.Ordinal)
        ? "/mnt/media" + host["/mnt/pve/ds1813-nfs-volume-3/data/media".Length..]
    : host;

static string Env(string k, string d) => Environment.GetEnvironmentVariable(k) is { Length: > 0 } v ? v : d;
static string Trim(string s, int n) => s.Length <= n ? s : s[..n];
static string Colour(string v) => v switch
{
    "HOT" => "[red]HOT[/]", "COLD" => "[blue]COLD[/]", "DELETABLE" => "[yellow]DELETABLE[/]",
    "EXCLUDED" => "[grey]EXCLUDED[/]", _ => $"[magenta]{v}[/]",
};

static async Task<List<FsFile>> FindAsync(string node, string[] roots)
{
    // One SSH round trip for the whole root set. Missing roots are tolerated (2>/dev/null)
    // so a not-yet-created path is not a hard failure.
    var cmd = $"find {string.Join(" ", roots.Select(r => $"'{r}'"))} -type f -printf '%i %n %s %p\\n' 2>/dev/null";
    var psi = new ProcessStartInfo("ssh") { RedirectStandardOutput = true, RedirectStandardError = true };
    psi.ArgumentList.Add("-o"); psi.ArgumentList.Add("BatchMode=yes");
    psi.ArgumentList.Add(node); psi.ArgumentList.Add(cmd);
    using var p = Process.Start(psi)!;
    var outp = await p.StandardOutput.ReadToEndAsync();
    await p.WaitForExitAsync();

    var list = new List<FsFile>();
    foreach (var line in outp.Split('\n', StringSplitOptions.RemoveEmptyEntries))
    {
        // inode, nlink, size, then the path — which may itself contain spaces.
        var a = line.Split(' ', 4);
        if (a.Length < 4) continue;
        if (!long.TryParse(a[0], out var ino) || !int.TryParse(a[1], out var nl) || !long.TryParse(a[2], out var sz)) continue;
        list.Add(new FsFile(ino, nl, sz, a[3]));
    }
    return list;
}

record FsFile(long Inode, int Links, long Size, string Path);
record Row(string Hash, string Name, string Category, long Size, bool Private, int Days, string Verdict, string Why, IReadOnlyList<string> Backing, string ContentPath);
