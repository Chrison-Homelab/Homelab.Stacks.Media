# CLAUDE.md — Homelab.Stacks.Media

## What this is

The **Media stack**: the arr fleet, Plex, the request/discovery surface, and the podman media
host. Extracted from the superproject on 2026-08-24 with its history intact, and composed back
as a submodule at `stacks/Media`.

> **Read the superproject's [`CLAUDE.md`](https://github.com/Chrison-Homelab/Homelab/blob/main/CLAUDE.md) first.**
> It carries the rules that apply here and are *not* repeated in this file: the PR-only git
> workflow + merge strategy (ADR-0010), the worktree rule for parallel sessions, the shared
> external-account guardrails (**add-only** — never touch Cloudflare/GitHub resources we didn't
> create), and `secrets.env` / Bitwarden Secrets Manager handling.

## This stack

Seventeen members in the `51xx` block, plus two adopted `50xx` survivors of the old fleet
(`plex` 5008, `seerr` 5105's predecessor lineage). The members split three ways:

- **Acquisition** — prowlarr, sonarr, radarr, bazarr, qbittorrent, cross-seed, flaresolverr
- **Presentation** — plex, seerr, audiobookshelf, romm, shelfmark, tracearr, youtarr
- **Platform** — `podman-host` (CT 5114, rootless podman + quadlets), `cloudflared` (the stack's
  own tunnel)

## ⚠ Why extraction was a reversal, and what it means for you

ADR-0008 originally named this stack as the example of what **not** to extract — *"Media in
particular is the heart of the lab and heavily coupled to monitoring/cloudflared"*. That was
amended on 2026-08-24, because two of the three costs it cited had gone away: stack repos can
now validate themselves, and the submodule bump bot self-merges hourly.

**The coupling it worried about is real and did not go away.** It just stopped being a reason to
keep the directory in-tree:

- **monitoring** scrapes this stack. `exportarr-{sonarr,radarr,prowlarr}` live in the *monitoring*
  stack and hold API keys for services declared *here*. Renaming or re-addressing a member here
  breaks a scrape target in another repo, and nothing links the two.
- **Core/Pangolin** fronts almost every member of this stack. The `resources:` list in
  `stacks/Core/pangolin.lxc.yaml` hard-codes each member's IP and port. A member that moves is a
  broken public route, declared one repo away.

Neither is enforced by anything. Both are two-repo changes.

## Working here

Converge runs **from the superproject**, pointed at this directory — never from inside this repo:

```bash
# in the superproject
./build.sh Preview --stack Media
./build.sh Deploy  --stack Media
```

This repo has its **own** build, so its PRs are checked here rather than only downstream:

```bash
./build.sh              # validate shapes against the pinned portable validator
./build.sh Bundle       # + produce dist/ (media-<version>.tar.gz + MANIFEST.md)
./build.sh Release      # + cut the GitHub Release a deploy consumes by tag
```

> The validator is **linux-x64 only**. On macOS `./build.sh` fails at `RestoreValidator` with a
> message saying so; use `--skip ValidateShapes` locally and let CI validate, or run the
> superproject's `./build.sh ValidateShapes`, which is the full-fidelity gate.

⚠ `BundlePaths()` in `build/Build.cs` is **hand-maintained**. A directory not listed there is
silently omitted from the bundle rather than failing the build. Today that list is `stack.yaml`,
`podman-host`, `youtarr`, `snippets`, `volume4-nfs.spec.json` — cross-check it when adding a
member with its own asset tree.

## Gotchas specific to this stack

- **The `50xx` members are adopted, not managed from scratch.** They predate the rebuild and
  carry state nobody wants to recreate. Check `manage:` before assuming a shape can be destroyed
  and re-converged.
- **NFS mounts are host-level, never inside the guest** (ADR/BL-016). `volume4-nfs.spec.json`
  describes the Synology side; the mount is declared on the shape and applied on the Proxmox
  host, so a container that cannot see its media is usually a host mount, not a container bug.
- **Two members deliberately bypass the SSO gate.** `seerr` and `audiobookshelf` are declared
  `sso: false` in Pangolin because their native mobile clients cannot complete the interstitial.
  Audiobookshelf has since gained real OIDC support (Homelab #485), so that exemption is
  reducible — Seerr's is not, its OIDC is still preview-only upstream.
