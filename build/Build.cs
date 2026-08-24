using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fallout.Common;
using Fallout.Common.IO;
using Fallout.Common.Tooling;
using Serilog;

// Media stack pipeline — Fallout build.
//
// This stack lives in its own repo (ADR-0008 meta-repo model), so it owns its own
// pipeline rather than borrowing the superproject's. Two things it does today:
//
//   ValidateShapes → the SAME engine `validate` the superproject runs, via the portable
//                    validator published to the Homelab `schema-v1` release. No .NET
//                    engine source, no private feeds, no self-hosted runner.
//   Release        → bundle the stack at a commit into an immutable artifact and cut the
//                    GitHub Release a deploy consumes by tag.
//
// Preview/Deploy are deliberately NOT here yet. Converge needs the engine plus cluster
// reach (Proxmox API + SSH to nodes), which stays in the superproject on the self-hosted
// runner. The seam for adding them later is `Engine(...)` below — it already runs the
// portable binary, so a Deploy target is a new target, not a restructure.
//
//   ./build.sh                      # default target: ValidateShapes
//   ./build.sh Bundle               # validate + produce dist/ artifact
//   ./build.sh Release              # bundle + cut the GitHub Release
//   ./build.sh Release --dry-run    # everything except creating the release
class Build : FalloutBuild
{
    public static int Main() => Execute<Build>(x => x.ValidateShapes);

    [Parameter("Homelab release tag to pin the portable validator to (default: the moving schema-v1 channel).")]
    readonly string SchemaRef = "schema-v1";

    [Parameter("Explicit release version, without the leading 'v' (e.g. 1.4.0). Default: computed from the labels on PRs merged since the last tag.")]
    readonly string ReleaseVersion;

    [Parameter("Compute, validate and bundle, but do not create the GitHub Release.")]
    readonly bool DryRun;

    const string SuperprojectRepo = "Chrison-Homelab/Homelab";
    const string StackRepo = "Chrison-Homelab/Homelab.Stacks.Media";

    AbsolutePath DistDirectory => RootDirectory / "dist";
    AbsolutePath ValidatorDirectory => RootDirectory / ".validator";
    AbsolutePath ValidatorBinary => ValidatorDirectory / "homelab-infra";

    // ---------------------------------------------------------------- validate

    Target RestoreValidator => _ => _
        .Description("Download the pinned portable validator from the Homelab schema-v1 release.")
        .OnlyWhenDynamic(() => !ValidatorBinary.FileExists())
        .Executes(() =>
        {
            // The published validator is linux-x64 only, so on a dev Mac/Windows box this
            // would otherwise fail with a bare "exec format error". Say what's wrong and how
            // to get past it — the superproject publishes no other RID today.
            if (!EnvironmentInfo.IsLinux)
                throw new Exception(
                    $"The portable validator is linux-x64 only and this is {EnvironmentInfo.Platform}. " +
                    "Run `./build.sh <Target> --skip ValidateShapes` locally, or let CI validate — " +
                    $"the full-fidelity gate is the superproject's `./build.sh ValidateShapes`.");

            ValidatorDirectory.CreateDirectory();
            // Two `gh` calls in this build need DIFFERENT tokens: this one reads a release
            // asset from the private SUPERPROJECT (SCHEMA_RO_PAT), while the version
            // computation reads PR labels from THIS repo (the ambient Actions token). `gh`
            // takes its token from the ambient GH_TOKEN, so the schema token is scoped to
            // this one process rather than exported over the whole run.
            Gh($"release download {SchemaRef} --repo {SuperprojectRepo} " +
               $"--pattern homelab-validate-linux-x64.tar.gz --dir {ValidatorDirectory} --clobber",
               token: SchemaToken);
            Run("tar", $"-C {ValidatorDirectory} -xzf {ValidatorDirectory / "homelab-validate-linux-x64.tar.gz"}");
            Run("chmod", $"+x {ValidatorBinary}");

            // The engine resolves the schema from AppContext.BaseDirectory, so it must sit
            // beside the binary. A silently-missing schema would fail every shape instead.
            var schema = ValidatorDirectory / "schema" / "shape.schema.json";
            if (!schema.FileExists())
                throw new Exception($"validator unpacked without its schema at {schema}");
        });

    Target ValidateShapes => _ => _
        .Description("Validate every shape in this stack against shape.schema.json.")
        .DependsOn(RestoreValidator)
        .Executes(() =>
        {
            // The validator skips any YAML that isn't an `apiVersion: homelab/v1` shape, so
            // pointing it at the repo root is safe — compose files and CI aren't shapes.
            Engine($"validate {RootDirectory}");
        });

    // ---------------------------------------------------------------- bundle

    Target Bundle => _ => _
        .Description("Bundle the stack's shapes and assets into dist/ with a manifest.")
        .DependsOn(ValidateShapes)
        .Executes(() =>
        {
            var version = ResolveVersion();
            DistDirectory.CreateOrCleanDirectory();

            var manifest = DistDirectory / "MANIFEST.md";
            manifest.WriteAllText(BuildManifest(version));

            // Ship the shapes and the assets they reference, and nothing else. Explicitly not
            // a `git archive` of everything: the docs/ tree carries ~10 MB of firmware images
            // and PDFs that a deploy has no use for.
            var archive = DistDirectory / $"media-{version}.tar.gz";
            var payload = string.Join(" ", BundlePaths().Select(p => p.ToString()));
            Run("tar", $"-czf {archive} -C {RootDirectory} {payload} -C {DistDirectory} MANIFEST.md");

            Log.Information("bundled {Archive} ({Size} bytes)", archive, archive.ToFileInfo().Length);
        });

    // Paths (relative to the repo root) that make up a deployable stack.
    IEnumerable<string> BundlePaths()
    {
        // ⚠ HAND-MAINTAINED. A directory not listed here is silently left out of the bundle
        // rather than failing the build — cross-check when adding a member. `snippets` and
        // `volume4-nfs.spec.json` are included because converge reads them, not just humans.
        var candidates = new[]
        {
            "stack.yaml", "podman-host", "youtarr", "snippets", "volume4-nfs.spec.json",
        };
        foreach (var yaml in RootDirectory.GlobFiles("*.lxc.yaml", "*.vm.yaml").OrderBy(p => p.Name))
            yield return yaml.Name;
        foreach (var c in candidates)
        {
            var path = RootDirectory / c;
            if (path.DirectoryExists() || path.FileExists())
                yield return c;
        }
    }

    string BuildManifest(string version)
    {
        var sha = Git("rev-parse HEAD").Trim();
        var members = RootDirectory
            .GlobFiles("*.lxc.yaml", "*.vm.yaml")
            .OrderBy(p => p.Name)
            .Select(p =>
            {
                var text = p.ReadAllText();
                var ctid = Regex.Match(text, @"^\s*(?:ctid|vmid):\s*(\d+)", RegexOptions.Multiline);
                var manage = Regex.Match(text, @"^\s*manage:\s*([a-z-]+)", RegexOptions.Multiline);
                return $"| {p.Name} | {(ctid.Success ? ctid.Groups[1].Value : "—")} " +
                       $"| {(manage.Success ? manage.Groups[1].Value : "managed")} |";
            });

        return $"""
            # Media stack — {version}

            - Commit: `{sha}`
            - Repo:   `{StackRepo}`
            - Built:  {DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}

            This artifact is the deployable stack at that commit. A deploy resolves the tag to
            this bundle, so what was validated is what ships.

            ## Members

            | Shape | ID | Lifecycle |
            |---|---|---|
            {string.Join("\n", members)}
            """;
    }

    // ---------------------------------------------------------------- release

    Target Release => _ => _
        .Description("Cut the GitHub Release for this stack, with notes generated from PR labels.")
        .DependsOn(Bundle)
        .Executes(() =>
        {
            var version = ResolveVersion();
            var tag = $"v{version}";

            if (DryRun)
            {
                Log.Information("dry run — would create release {Tag} from {Sha}", tag, Git("rev-parse HEAD").Trim());
                return;
            }

            if (!string.IsNullOrWhiteSpace(GhOrEmpty($"release view {tag} --repo {StackRepo} --json tagName")))
            {
                Log.Warning("release {Tag} already exists — nothing to do", tag);
                return;
            }

            // --generate-notes groups merged PRs by label using .github/release.yml, which is
            // why every PR must carry exactly one category label at creation time.
            Gh($"release create {tag} {DistDirectory / $"media-{version}.tar.gz"} {DistDirectory / "MANIFEST.md"} " +
               $"--repo {StackRepo} --title \"Media {tag}\" --generate-notes --target {Git("rev-parse HEAD").Trim()}");

            Log.Information("released {Tag}", tag);
        });

    // ---------------------------------------------------------------- versioning

    string _resolvedVersion;

    // SemVer, derived from the labels on PRs merged since the last tag — the same taxonomy
    // the repo already enforces at PR-creation time, so the version says something true
    // about whether an upgrade is safe:
    //   breaking-change → major   (a ctid or contract change: recreating a guest)
    //   enhancement     → minor
    //   anything else   → patch
    string ResolveVersion()
    {
        if (_resolvedVersion is not null) return _resolvedVersion;
        if (!string.IsNullOrWhiteSpace(ReleaseVersion)) return _resolvedVersion = ReleaseVersion.TrimStart('v');

        var last = GitOrEmpty("describe --tags --abbrev=0 --match v*").Trim();
        if (string.IsNullOrWhiteSpace(last))
        {
            Log.Information("no previous tag — starting at 0.1.0");
            return _resolvedVersion = "0.1.0";
        }

        var parsed = Regex.Match(last, @"^v(\d+)\.(\d+)\.(\d+)$");
        if (!parsed.Success)
            throw new Exception($"last tag '{last}' is not vMAJOR.MINOR.PATCH — pass --release-version to override");

        var (major, minor, patch) = (int.Parse(parsed.Groups[1].Value),
                                     int.Parse(parsed.Groups[2].Value),
                                     int.Parse(parsed.Groups[3].Value));

        var labels = MergedPrLabelsSince(last);
        if (labels.Contains("breaking-change")) { major++; minor = 0; patch = 0; }
        else if (labels.Contains("enhancement")) { minor++; patch = 0; }
        else patch++;

        _resolvedVersion = $"{major}.{minor}.{patch}";
        Log.Information("{Last} + [{Labels}] → v{Next}", last, string.Join(", ", labels.OrderBy(x => x)), _resolvedVersion);
        return _resolvedVersion;
    }

    // Labels across every PR merged since `sinceTag`. Uses the commit list rather than a date
    // window: merged-at timestamps overlap when two PRs land close together, which would
    // silently fold one PR's labels into the previous release.
    HashSet<string> MergedPrLabelsSince(string sinceTag)
    {
        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var shas = GitOrEmpty($"rev-list {sinceTag}..HEAD")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .ToList();

        foreach (var sha in shas)
        {
            var json = GhOrEmpty($"api repos/{StackRepo}/commits/{sha}/pulls --jq [.[].labels[].name]");
            if (string.IsNullOrWhiteSpace(json)) continue;
            try
            {
                foreach (var name in JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>())
                    labels.Add(name);
            }
            catch (JsonException) { /* no associated PR — direct commit */ }
        }

        if (labels.Count == 0)
            Log.Warning("no PR labels found since {Tag} — defaulting to a patch bump", sinceTag);
        return labels;
    }

    // ---------------------------------------------------------------- process helpers

    // Pre-built strings take StartProcess's plain overload. Passing an interpolated string
    // DIRECTLY binds Fallout's ArgumentStringHandler, which quotes each interpolation hole —
    // collapsing a multi-token argument list into one quoted argument.
    void Engine(string arguments)
    {
        string command = arguments;
        ProcessTasks.StartProcess(ValidatorBinary, command, workingDirectory: RootDirectory).AssertZeroExitCode();
    }

    void Run(string tool, string arguments)
    {
        string command = arguments;
        ProcessTasks.StartProcess(tool, command, workingDirectory: RootDirectory).AssertZeroExitCode();
    }

    // Token with contents:read on the private superproject, needed only to download the
    // validator. Falls back to the ambient gh auth locally, where a dev already has access.
    string SchemaToken => Environment.GetEnvironmentVariable("SCHEMA_RO_PAT");

    void Gh(string arguments, string token = null)
    {
        if (string.IsNullOrWhiteSpace(token)) { Run("gh", arguments); return; }

        string command = arguments;
        var env = Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string)e.Value, StringComparer.OrdinalIgnoreCase);
        env["GH_TOKEN"] = token;

        ProcessTasks.StartProcess("gh", command, workingDirectory: RootDirectory, environmentVariables: env)
            .AssertZeroExitCode();
    }

    string Git(string arguments)
    {
        string command = arguments;
        var proc = ProcessTasks.StartProcess("git", command, workingDirectory: RootDirectory, logOutput: false).AssertZeroExitCode();
        return string.Join("\n", proc.Output.Select(o => o.Text));
    }

    string GitOrEmpty(string arguments) => TryCapture("git", arguments);
    string GhOrEmpty(string arguments) => TryCapture("gh", arguments);

    string TryCapture(string tool, string arguments)
    {
        string command = arguments;
        try
        {
            var proc = ProcessTasks.StartProcess(tool, command, workingDirectory: RootDirectory, logOutput: false);
            proc.WaitForExit();
            return proc.ExitCode == 0 ? string.Join("\n", proc.Output.Select(o => o.Text)) : "";
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "{Tool} {Args} failed", tool, arguments);
            return "";
        }
    }
}
