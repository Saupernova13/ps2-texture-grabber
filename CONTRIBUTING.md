# Contributing to ps2-texture-grabber

Thanks for taking an interest.

This is a small, single-purpose Windows tool. Contributions are kept lightweight.

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

`shim/dlps2tex.cmd` finds `bin\publish\ps2tex.exe` on its own. So after a publish
you can run the launcher straight from the checkout:

```powershell
.\shim\dlps2tex.cmd --version
```

**You need the .NET 10 SDK.**

The tool is Windows-only on purpose. It writes into PCSX2's `%APPDATA%` folders,
calls `7z.exe`, and publishes as `win-x64`.

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

`AppPaths` is the only place that knows where anything lives.

If you need a new file or folder, add it there. Do not build a path inline.
`--clean` and `EnsureAll` both read from `AppPaths`. A path invented anywhere else
is a path neither of them will manage.

## Conventions

- **No hardcoded personal paths.** Anything machine-specific is a `.settings`
  key, a command-line argument, or derived at runtime from `%APPDATA%`. Keep real
  usernames out of `.settings.example`.
- **Nothing may wait for a download.** A query finds links, starts a detached
  worker, and returns a job id. There is no `--wait`, on purpose. Scripts and
  agents drive this tool, and they must not be held up by a multi-GB transfer.
- **Fail soft on optional dependencies.** A missing 7-Zip, an unreachable host, or
  a dead cache should print a clear message and carry on. Never crash.
- **Batch files stay CRLF.** `cmd.exe` finds a `goto` label by byte offset. LF
  endings make labels vanish once a file grows. `.gitattributes` enforces this and
  CI checks it.
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
3. If you changed scraping, run a real query. That covers `GbatempService`,
   `ArchiveOrgIndexService` and `WikiService`. They depend on live HTML, and no
   unit test would notice that changing.

CI runs the build, publishes a self-contained binary, smoke-tests `--version`
and `--clean --dry-run`, and verifies the shim's line endings.

## A note on the scrapers

The GBAtemp path is the fragile part. It reads a live forum behind Cloudflare. So
it will break when the site changes. That is expected, not a fault in your PR.

When a thread yields no links, the tool saves the full HTML to
`data/cache/missing-links/`. It then tells you to add a matching pattern to
`GbatempService.HostPatterns`.

That saved HTML is the intended starting point for a fix. Please include the host
name and an example URL in your PR.

## Releasing (maintainers)

The tag is the version. Everything else is automatic:

```powershell
git tag v1.2.3
git push origin v1.2.3
```

The Release workflow then does all of this:

1. Stamps `1.2.3` into the assembly.
2. Checks that the built exe reports exactly that version.
3. Packages the exe, the launcher and the docs into a zip.
4. Unpacks that zip somewhere clean and runs it through the shim.
5. Publishes the GitHub Release.

A tag not shaped like `v<major>.<minor>.<patch>` fails the run. That is better
than shipping something mislabelled.

A tag containing `-`, such as `v1.3.0-rc1`, is marked as a pre-release.

## Scope and legality

This tool automates downloads from third-party community sites. Use it only for
content you are entitled to. Please don't add features whose purpose is to
circumvent paywalls or DRM beyond what the target sites already serve publicly.
