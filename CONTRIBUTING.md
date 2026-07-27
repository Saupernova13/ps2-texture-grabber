# Contributing to ps2-texture-grabber

Thanks for taking an interest. This is a small, single-purpose Windows tool, so
contributions are kept lightweight.

## Building

```powershell
git clone https://github.com/Saupernova13/ps2-texture-grabber
cd ps2-texture-grabber
dotnet build Ps2TextureGrabber.csproj -c Release
```

To produce the same artifact a release ships:

```powershell
dotnet publish Ps2TextureGrabber.csproj -c Release -r win-x64 --self-contained -o bin\publish
```

`shim/dlps2tex.cmd` finds `bin\publish\ps2tex.exe` on its own, so after a publish
you can run the launcher straight out of the checkout:

```powershell
.\shim\dlps2tex.cmd --version
```

**Requires .NET 10 SDK.** The tool is Windows-only by design — it writes into
PCSX2's `%APPDATA%` layout, shells out to `7z.exe`, and publishes `win-x64`.

## Repo shape

```
Program.cs             top-level argument dispatch; every subcommand starts here
AppPaths.cs            every path the app uses, computed from the exe location
Models/                plain records passed between services
Services/              one class per external system or concern
Downloaders/           one per host (MEGA, GDrive, MediaFire, Yandex, direct)
Worker/                the detached background worker: download, extract, install
shim/dlps2tex.cmd      the launcher shipped in releases
.github/workflows/     CI (build + smoke test) and Release (publish + zip)
```

`AppPaths` is the only place that knows where anything lives. If you need a new
file or directory, add it there rather than composing a path inline — `--clean`
and `EnsureAll` both read from it, and a path invented somewhere else is a path
neither of them will manage.

## Conventions

- **No hardcoded personal paths.** Anything machine-specific is either a
  `.settings` key, a command-line argument, or derived at runtime from
  `%APPDATA%`. `.settings.example` must stay free of real usernames.
- **Nothing may block on a download.** A query resolves links, spawns a detached
  worker and returns a job id. There is deliberately no `--wait`: the tool is
  driven by scripts and agents that must not be held for a multi-GB transfer.
- **Fail soft on optional dependencies.** A missing 7-Zip, an unreachable host or
  a dead cache should degrade with a clear message, never crash.
- **Batch files stay CRLF.** `cmd.exe` resolves `goto` by byte offset; LF endings
  make labels vanish once a file grows. `.gitattributes` pins this and CI checks
  it.
- **Never commit build output.** `bin/` and `obj/` are ignored. Releases carry
  the binary.
- **Commits:** conventional style — `feat:`, `fix:`, `docs:`, `refactor:`,
  `chore:`, optionally scoped (`fix(worker): ...`).

## Testing changes

There is no automated test suite yet. Before opening a PR:

1. `dotnet build Ps2TextureGrabber.csproj -c Release` — must be warning-clean
   for your own code.
2. Exercise the paths you touched:
   ```powershell
   .\shim\dlps2tex.cmd --version
   .\shim\dlps2tex.cmd --list
   .\shim\dlps2tex.cmd --clean --dry-run
   .\shim\dlps2tex.cmd "God of War"        # end to end; returns a job id
   .\shim\dlps2tex.cmd --status <jobId>
   ```
3. If you changed scraping (`GbatempService`, `ArchiveOrgIndexService`,
   `WikiService`) run a real query — those depend on live HTML that no unit test
   would notice changing.

CI runs the build, publishes a self-contained binary, smoke-tests `--version`
and `--clean --dry-run`, and verifies the shim's line endings.

## A note on the scrapers

The GBAtemp path is the fragile part. It reads a live forum behind Cloudflare, so
it will break when the site changes — that is expected, not a defect in your PR.

When a thread yields no links, the tool saves the full HTML to
`data/cache/missing-links/` and tells you to add a matching pattern to
`GbatempService.HostPatterns`. That dump is the intended starting point for a
fix; please include the host name and an example URL in the PR.

## Releasing (maintainers)

The tag is the version. Everything else is automatic:

```powershell
git tag v1.2.3
git push origin v1.2.3
```

The Release workflow stamps `1.2.3` into the assembly, verifies
`ps2tex --version` reports exactly that, packages the exe + launcher + docs,
unpacks the zip somewhere clean and drives it through the shim, then publishes
the GitHub Release. A version that does not match `v<major>.<minor>.<patch>`
fails the run rather than shipping something mislabelled. A tag containing `-`
(e.g. `v1.3.0-rc1`) is marked as a pre-release.

## Scope and legality

This tool automates downloads from third-party community sites. Use it only for
content you are entitled to. Please don't add features whose purpose is to
circumvent paywalls or DRM beyond what the target sites already serve publicly.
