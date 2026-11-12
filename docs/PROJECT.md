# SteamReady development checkpoint

## Goal
Bulk update preference management for locally installed Steam apps on Windows, including secondary drives.

## Implementation checkpoint 0.1 — source only, not tested
- Registry discovery; manual folder selection.
- Parse modern and legacy libraryfolders.vdf and AppState manifests.
- Display manifest-backed apps (may include tools or other non-game apps).
- Preserve UTF-8 BOM, whitespace, unrelated keys by replacing just the value token.
- Require Steam closed for mutations, byte-for-byte backups, journal before writes, stale-data checks, atomic replacement and verification.
- Undo only the preference, not the whole manifest.
- Backups under %LOCALAPPDATA%/SteamReady/backups. Last-batch undo only; older raw backups remain.

## Required before release
1. Compile and run fixture tests: modern/legacy paths, malformed/duplicate keys, BOM/CRLF, missing mode, stale data, undo, failure handling.
2. Verify actual Steam client preference mapping for 0/1/2 using one disposable game, checking the UI after restarting Steam.
3. Test multiple drives, unavailable libraries, permissions, Steam running and partial failures.
4. Improve UI responsiveness and recovery controls.
5. Publish a self-contained win-x64 executable; generate SHA-256, verify clean Windows run without .NET installed.
6. Owner acceptance before public release.

## Build (Windows, .NET 10 SDK)
```powershell
dotnet build src/SteamReady/SteamReady.csproj -c Release
dotnet publish src/SteamReady/SteamReady.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

Known limitations: manifest format unofficial; no guaranteed instant download, no forced shutdown, no background monitoring. Initial source is untested. Steam can be restarted externally during a write; process checks minimize but cannot eliminate this race. Do not start Steam until the utility finishes. Undo only tracks most recent batch. Journal and recovery behavior require failure-injection testing. No public executable yet.
