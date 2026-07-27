<!-- Keep this short. The diff says what changed; this says why. -->

## What and why

<!-- What problem does this solve? Link an issue if there is one. -->

## How it was tested

<!-- CI builds and smoke-tests, but it cannot exercise a live scrape or a real
     download. Say what you actually ran. -->

- [ ] `dotnet build Ps2TextureGrabber.csproj -c Release`
- [ ] `.\shim\dlps2tex.cmd --version`
- [ ] `.\shim\dlps2tex.cmd --clean --dry-run`
- [ ] End-to-end query (`.\shim\dlps2tex.cmd "Some Game"`) — required if you
      touched scraping, downloading, extraction or install

## Checklist

- [ ] No build output (`bin/`, `obj/`) committed
- [ ] No personal paths or usernames added to tracked files
- [ ] New paths go through `AppPaths`
- [ ] Batch files still CRLF
