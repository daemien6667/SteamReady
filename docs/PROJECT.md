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

## Approved functionality decisions — 2026-11-12

Status: APPROVED DESIGN only. These decisions are not implementation or public-release authorization. Preserve existing working UI; no UI design work until explicitly authorized.

| # | Decision | Approved direction |
|---|---|---|
| 1 | Automatic management scope | User-selectable new games only (default when automatic management is enabled) or continuous enforcement across installed games, subject to individual exceptions. Background operation remains optional. |
| 2 | Individual exceptions | User-selectable: (A, default) respect deliberate changes made within Steam and automatically remember them as exceptions; (B) notify the user and ask whether to keep the new preference or restore the SteamReady preference. Applies independently of automatic management scope. |
| 3 | Uninstall/reinstall | Remember preferences by Steam app ID across uninstall and reinstall. User-selectable: (A, default) restore saved preferences automatically on reinstall, subject to the chosen Apply Changes Behaviour; (B) ask whether to restore saved preferences on reinstall. Offer an option to forget individual uninstalled games entirely. |
| 4 | Backup retention | Keep the last 50 change batches by default, with an option to retain more. Approved option C: restore an entire historical batch or selected individual games from any retained batch. Display date, affected game count and identities, and previous/new preferences. Record each restoration as a new history entry so restorations can themselves be undone. Restore preferences only, not entire historical manifest files; respect Apply Changes Behaviour and guard against stale data. Multi-batch recovery is planned; current build supports only last-batch undo. |
| 5 | Game profiles | Approved option C: both user-defined game groups and switchable library-wide profiles that assign update preferences to those groups. Custom group and profile names; no artificial profile-count limit; duplicate existing profiles; preview changes before applying. Switching profiles restores their saved preferences, subject to explicit individual exceptions and chosen Apply Changes Behaviour. Owner-approved: a game may belong to multiple groups. Resolve overlapping group preferences deterministically using an explicit group priority; individual exceptions take precedence. Exact priority handling is a proposed implementation detail to verify before implementation, not a new owner-approved decision. No additional design audit required. |
| 6 | Portable preferences | Approved option C: selective export and import of game preferences (including uninstalled apps), groups, profiles, individual exceptions, automatic management settings and application preferences, with a one-click Export Everything option. Use readable, versioned JSON and match games by Steam app ID, independent of installation location. Preview changes before applying; choose merge or replace. Exclude historical manifest backups from standard export. Validate imported settings and respect Apply Changes Behaviour; never silently enable automatic writes or startup from imported settings. No Steam credentials required. |
| 7 | SteamReady updates | Approved option B: automatic version checks and notification, enabled by default but optional; check no more than once per day automatically, and provide a manual Check for Updates action. Display release notes, link directly to official GitHub Releases downloads, and allow skipping a particular version. Users choose when to download and replace the portable executable. No automatic download, self-updater, or unattended installation in the initial version. |
| 8 | Steam integration | Owner-approved revised direction: SteamReady should handle normal Steam shutdown, wait for complete client exit, apply and verify preference changes, then relaunch Steam for an explicitly user-initiated apply operation. Investigate and validate Steam's `steam.exe -shutdown` command with the current Windows client. Preserve previously approved Apply Changes Behaviour manual-confirmation default and optional queue modes; offer a user-initiated shutdown/apply/relaunch path without overriding manual choice. Never force-kill Steam or games. Proposed safety constraints: check for running games or an unsafe/uncertain client state, always display an explicit Yes/No confirmation before requesting shutdown of a running Steam client for a user-initiated apply; Yes authorizes graceful shutdown, apply, verify and relaunch; No leaves Steam running and makes no changes (the user may close Steam manually and retry). Never remember or bypass this confirmation, use a bounded shutdown wait and fall back to manual closure if needed; do not write while Steam is active, do not start duplicate instances, and do not automatically relaunch Steam after background-queued work triggered by the user's normal exit. Standalone Launch Steam action remains available. Validate shutdown/relaunch and recovery in a real Steam client before release. |
| 9 | Startup behaviour | Approved option C: user-selectable Windows startup (off, start with window visible, or start minimized to system tray). Defaults: background mode Off; start with Windows Off; start minimized On when startup is enabled. Closing the application window when background mode is enabled minimizes to tray; otherwise close the application normally. Provide an explicit Exit SteamReady tray action that fully terminates the application. Startup is opt-in and must not silently be enabled by imported settings. |
| 10 | Download management | REJECTED. Do not expand into bandwidth limits, download scheduling, or pending-update management. Keep scope focused on game update preferences. |

### Approved apply-changes behaviour (earlier decision, 2026-11-12)

Provide three independent, user-selectable modes:
1. **Manual confirmation (default):** If Steam is running, tell the user to close it and click Apply again. Preserve the existing behaviour.
2. **Apply when Steam closes:** Queue changes and automatically apply after Steam exits normally.
3. **Notify when ready:** Queue changes, detect Steam has closed, notify the user, and require confirmation before applying.

This setting is independent of automatic new-game detection. No automatic writes without explicit opt-in. Never force-close Steam. Recheck that Steam is closed and that the target manifest still matches expected state before writing.

### Other approved design principles
- Optional background operation, not mandatory; manual use remains available.
- New-game detection and configurable default preference, with optional notifications.
- Preserve the simple one-click bulk update workflow.
- UI design remains explicitly deferred for a separate expert-led design phase.

### Open planning
Owner authorized development to proceed after completing the functionality discussion. UI design remains on hold; implement approved functionality incrementally without redesigning the interface or publishing a public release.


## Checkpoint 0.2 — history engine (compiled and fixture-tested)
- Added `ChangeHistory` backend with durable pre-write intents, byte-exact backups, per-entry results and timestamps, configurable retention (default 50 completed batches; pending and legacy records are preserved), whole-batch and selected-app restoration, and restoration as a new batch.
- Existing Apply and Undo last batch actions now use the new backend. Existing layout and appearance are unchanged. The history browser, selection and retention controls are deferred to the separate UI phase; the backend APIs are available for integration.
- Older preview undo journal remains readable; old raw backups are never pruned by the new engine. Restoration checks current scanned path/app identity and expected preference; conflicting or moved/unavailable apps are skipped with errors.
- Added isolated write tests, backup/BOM verification, selective restore, preservation of unrelated changes, undo of restoration, stale/conflicting modes, partial failures, simulated Steam restart, and retention.
- [Windows CI run](https://github.com/daemien6667/SteamReady/actions/runs/43325406399) completed successfully: compile, fixture tests, portable publish and artifact upload. Owner regression test remains outstanding;
- Remaining: test crash/power-loss recovery and access-denied handling; build saved preferences/groups/profiles, import/export, automatic monitoring/queue and startup, graceful Steam shutdown/relaunch with explicit consent, and update notification. No public release or UI design authorized.


## Checkpoint 0.3 — persistent preferences backend (implementation, CI pending)
- Versioned local JSON store keyed by Steam app ID, not library path. Verified Apply/Undo writes now record successful preferences; failed writes never update the saved target. A preference persistence failure is reported without claiming the Steam write failed.
- Observation API establishes a baseline, detects new apps and missing/reinstalled apps, preserves preferences across uninstall, recognizes manual preference changes and supports default respect-as-exception or ask behavior. Pending observations persist across restart; reinstall planning defaults to automatic restoration eligibility, with optional confirmation.
- Incomplete scans, unreadable manifests, disconnected drives and duplicate app IDs cannot trigger uninstall/reinstall decisions. Scan observation is intentionally conservative and does not write Steam manifests. Forget is restricted to uninstalled apps.
- Restoration plans require a fresh complete scan and unchanged observed preference. No automatic writes occur in this checkpoint. The monitoring/queue layer will call observation and execute approved plans later; existing UI layout and code remain unchanged.
- Added fixtures for baseline, partial scans, duplicate IDs, reinstall on different drive, persistence, pending decisions, manual exceptions, stale plans, new games, forgetting and verified-write integration.
- Next: groups/profiles and explicit preference precedence; then import/export, monitoring/queue, shutdown/relaunch, startup and update notification. UI redesign and public release remain deferred.


## Checkpoint 0.4 — groups and profiles backend (implementation, CI pending)
- Versioned JSON profile catalog; custom names, multiple memberships, ordered group priority, group defaults, per-profile overrides and optional ungrouped default. Highest priority matching group wins; explicit individual exceptions take precedence. No artificial group/profile limit.
- Create, edit via validated catalog save, duplicate, reorder and delete; group removal cleans profile overrides. Active profile deletion is blocked until switching away.
- Preview lists every installed app, current/target mode, reason and conflicts. Incomplete/ambiguous scans and unresolved external changes prevent unsafe switching. Confirmed application requires an unchanged fresh preview, uses the existing journal/backup writer, and records a profile batch.
- Preserve individual base preference separately from effective profile target, so temporary modes do not destroy original settings. Successful profile writes update observed/effective values; full successful switch marks active profile. Partial failures are reported; active profile is not changed. Profile application is backend-only, with no automatic shutdown or background execution. Profile-aware history undo/automatic reconciliation and UI controls remain to be integrated.
- Added tests for overlapping groups, priority changes, profile defaults/overrides, duplication, invalid configuration, exception precedence, stale preview, actual fixture application, history and switching back to distinct individual modes.
- No change to MainForm or visual design. Next: selective export/import.
