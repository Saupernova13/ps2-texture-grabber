# Changelog

All notable changes to this project are documented here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and
this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- `--search "<query>"` shows every matching GBAtemp thread — score, URL and the
  download links it contains — without downloading anything. `--browse` lists
  every thread on the texture pack subforum, scraping all pages rather than
  just the RSS window. Both support `--json`; `--browse --refresh` forces a
  live re-scrape instead of reusing a listing under 12 hours old.
- `--clean` reclaims abandoned job working directories (the staged archive plus
  its extracted copy, left behind when a worker is killed) and the rebuildable
  caches. `--all` also clears finished job records; `--dry-run` previews.
  Installed texture packs are never touched, and live jobs are protected.
- `--version`, so a bug report can say which build it came from.
- `shim/dlps2tex.cmd` — the launcher now lives in the repo and is shipped in
  releases. It finds `ps2tex.exe` beside itself, then in a source checkout, then
  on `PATH`, with no absolute path baked in.
- GitHub Actions: **CI** builds, publishes and smoke-tests every push; **Release**
  turns a `v*` tag into a verified, self-contained zip.
- MIT licence, contributing guide, issue and PR templates, `.editorconfig`.

### Changed
- GBAtemp page fetches are much faster: a small pool of browser contexts is
  reused (Cloudflare clearance only happens once per context) and the
  10-second NetworkIdle wait is gone. Fetches are paced at ~0.25 requests/s,
  the rate the Cloudflare edge demonstrably tolerates - faster bursts land in
  unsolvable challenge loops. Searches refresh page 1 via the browser when
  the RSS feed 403s, so new threads are never missed.
- `.settings.example` no longer contains a real Windows username, and documents
  that every key is optional on a standard EmuDeck install.
- `.gitignore` now excludes all build output.

### Fixed
- Job working directories are deleted after a successful install, not just on
  failure, so extracted texture copies stop accumulating.

### Removed
- `bin/publish/ps2tex.exe`, its `.pdb` and `playwright.ps1` are no longer tracked
  in git. Releases carry the binary; CI rebuilds it from source.

[Unreleased]: https://github.com/Saupernova13/ps2-texture-grabber/commits/main
