# Changelog

All notable changes to this project are documented here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and
this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
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
