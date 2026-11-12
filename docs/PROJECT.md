# SteamReady development checkpoint

## Goal
Bulk update preference management for locally installed Steam apps on Windows, including secondary drives.

## Checkpoint 0.1 — compiled, fixture-tested, owner-confirmed core functionality
- Registry discovery; manual folder selection.
- Parse modern and legacy libraryfolders.vdf and AppState manifests.
- Display manifest-backed apps (may include tools or other non-game apps).
- Preserve UTF-8 BOM, whitespace, unrelated keys by replacing just the value token.
- Require Steam closed for mutations, byte-for-byte backups, journal before writes, stale-data checks, atomic replacement and verification.
- Undo only the preference, not the whole manifest.
- Backups under %LOCALAPPDATA%/SteamReady/backups. Last-batch undo only; older raw backups remain.

## Verified evidence
- GitHub Actions [run #1](https://github.com/daemien6667/SteamReady/actions/runs/43321088154) completed successfully: Windows compile, isolated parser/discovery fixture checks, self-contained x64 publish, checksum, and artifact upload.
- Build source commit: cef9df3f4cb354168f19ac6eca481bf7dc55c200.
- Owner test, 2026-11-12: Derek reported the application worked perfectly, changed all games to immediate updates, then changed all back to Steam decides, and confirmed the settings persisted.
- This confirms owner-reported bulk changes for modes 2 and 0 on a real installation. It does not establish mode 1, undo, or failure recovery, nor a clean-machine run. No independent screenshots or logs supplied.

## Owner direction
**Do not begin UI design or visual redesign yet.** Preserve the existing working interface. Discuss further functionality and priorities with the owner before starting new implementation. Do not treat core success as authorization for a public release.

## Remaining validation before release
1. Verify mode 1 (only when launched) and the dedicated undo function.
2. Add isolated file-write, BOM preservation, journal and failure-recovery tests; cover stale data, permissions, Steam running, and partial failures.
3. Confirm multiple drives and unavailable-library behavior on real systems.
4. Verify clean Windows run without .NET installed; document unsigned executable warnings and compatibility.
5. Owner acceptance of release scope and final build before publishing a public release.
6. UI polish is deferred by explicit owner request.

## Build (Windows, .NET 10 SDK)
```powershell
dotnet build src/SteamReady/SteamReady.csproj -c Release
dotnet publish src/SteamReady/SteamReady.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

Known limitations: manifest format unofficial; no guaranteed instant download, no forced shutdown, no background monitoring. Steam can be restarted externally during a write; process checks minimize but cannot eliminate this race. Do not start Steam until the utility finishes. Undo only tracks most recent batch. Journal and recovery behavior require failure-injection testing. Development preview artifact exists; no public v1.0 release yet.
