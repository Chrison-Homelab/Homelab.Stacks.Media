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

// ── 1. filesystem facts, over SSH ────────────────────────────────────────────────────
// One find per root set. `%i %n %s %p` = inode, link count, size, path. Parsing is
// positional on the first three fields so a path containing spaces survives intact.
AnsiConsole.MarkupLine("[grey]scanning library roots on[/] {0}…", node);
var libFiles = await FindAsync(node, libraryRoots);
AnsiConsole.MarkupLine("[grey]scanning torrent roots…[/]");
var torFiles = await FindAsync(node, torrentRoots);

var libInodes = libFiles.Select(f => f.Inode).ToHashSet();
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
            foreach (var part in v.Descendants("Part"))
            {
                if (long.TryParse((string?)part.Attribute("size"), out var s)) watchedSizes.Add(s);
                if ((string?)part.Attribute("file") is { Length: > 0 } f) watchedPaths.Add(f);
            }
        }
    }
}
else AnsiConsole.MarkupLine("[yellow]PLEX_TOKEN unset — watched-state rules will not fire.[/]");

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
    if (files.Count == 0) { results.Add(new(hash, name, cat, size, priv, days, "UNRESOLVED", "content path not found on disk")); continue; }

    // IN PLEX, tested two ways. A cross-filesystem import COPIES rather than hardlinks,
    // and a copy is every bit as much "in Plex" as a link — missing that is exactly how
    // 12 torrents were wrongly cold-stored.
    var linked = files.Any(f => libInodes.Contains(f.Inode));
    var copied = files.Any(f => libSizes.Contains(f.Size));
    var inPlex = linked || copied;

    // Watched applies only to what Plex still holds; an absent file was never watchable.
    var watched = files.Any(f => watchedSizes.Contains(f.Size));

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

    results.Add(new(hash, name, cat, size, priv, days, verdict, why));
}

// ── 5. report ────────────────────────────────────────────────────────────────────────
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
record Row(string Hash, string Name, string Category, long Size, bool Private, int Days, string Verdict, string Why);
