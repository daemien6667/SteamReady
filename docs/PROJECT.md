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

Known limitations: manifest format unofficial; no guaranteed instant download, no forced shutdown, no background monitoring. Steam can be restarted externally during a write; process checks minimize but cannot eliminate this race. Do not start Steam until the utility finishes. The existing Undo control targets the latest restorable batch; the backend supports historical restores. Crash/power-loss and permission failure recovery still require validation. Development preview artifact exists; no public v1.0 release yet.

## Approved functionality decisions — 2026-11-12

Status: APPROVED DESIGN only. These decisions are not implementation or public-release authorization. Preserve existing working UI; no UI design work until explicitly authorized.

| # | Decision | Approved direction |
|---|---|---|
| 1 | Automatic management scope | User-selectable new games only (default when automatic management is enabled) or continuous enforcement across installed games, subject to individual exceptions. Background operation remains optional. |
| 2 | Individual exceptions | User-selectable: (A, default) respect deliberate changes made within Steam and automatically remember them as exceptions; (B) notify the user and ask whether to keep the new preference or restore the SteamReady preference. Applies independently of automatic management scope. |
| 3 | Uninstall/reinstall | Remember preferences by Steam app ID across uninstall and reinstall. User-selectable: (A, default) restore saved preferences automatically on reinstall, subject to the chosen Apply Changes Behaviour; (B) ask whether to restore saved preferences on reinstall. Offer an option to forget individual uninstalled games entirely. |
| 4 | Backup retention | Keep the last 50 change batches by default, with an option to retain more. Approved option C: restore an entire historical batch or selected individual games from any retained batch. Display date, affected game count and identities, and previous/new preferences. Record each restoration as a new history entry so restorations can themselves be undone. Restore preferences only, not entire historical manifest files; respect Apply Changes Behaviour and guard against stale data. Multi-batch recovery backend is implemented; the existing interface exposes latest-batch undo only. |
| 5 | Built-in update categories | SUPERSEDED by owner on 2026-11-12: remove customizable groups, multiple membership, group priorities and switchable profiles from release scope. Use the three existing Steam update preferences as categories: Immediately download updates, Let Steam decide, Only update when launched. Select/deselect all or individual games, choose one preference in a dropdown, and apply to the selected games. Future UI phase: filter all games or by existing preference; show Mixed preferences when selection contains different values (not a writable preference). |
| 6 | Portable preferences | DEFERRED / out of initial release scope by owner on 2026-11-12. No export/import implementation. Saved local per-game preferences and backup history remain in scope. |
| 7 | SteamReady updates | Owner-approved revision: automatic checks/notifications on by default, optional, at most daily; manual Check for Updates, release notes, skip version. Show Download & Install Update only when a newer compatible stable official GitHub release exists. Explicit user click/confirmation downloads, verifies SHA-256, replaces portable executable after exit and relaunches. Never unattended installation. Keep old executable for recovery; no Steam data/settings changes. |
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
- Remaining: test crash/power-loss recovery and access-denied handling; build saved preferences, import/export, automatic monitoring/queue and startup, graceful Steam shutdown/relaunch with explicit consent, and update notification. No public release or UI design authorized.


## Checkpoint 0.3 — persistent preferences backend (compiled and fixture-tested)
- Versioned local JSON store keyed by Steam app ID, not library path. Verified Apply/Undo writes now record successful preferences; failed writes never update the saved target. A preference persistence failure is reported without claiming the Steam write failed.
- Observation API establishes a baseline, detects new apps and missing/reinstalled apps, preserves preferences across uninstall, recognizes manual preference changes and supports default respect-as-exception or ask behavior. Pending observations persist across restart; reinstall planning defaults to automatic restoration eligibility, with optional confirmation.
- Incomplete scans, unreadable manifests, disconnected drives and duplicate app IDs cannot trigger uninstall/reinstall decisions. Scan observation is intentionally conservative and does not write Steam manifests. Forget is restricted to uninstalled apps.
- Restoration plans require a fresh complete scan and unchanged observed preference. No automatic writes occur in this checkpoint. The monitoring/queue layer will call observation and execute approved plans later; existing UI layout and code remain unchanged.
- Added fixtures for baseline, partial scans, duplicate IDs, reinstall on different drive, persistence, pending decisions, manual exceptions, stale plans, new games, forgetting and verified-write integration.
- [Windows CI](https://github.com/daemien6667/SteamReady/actions/runs/43330299063) completed successfully. Next: import/export, monitoring/queue, shutdown/relaunch, startup and update notification. UI redesign and public release remain deferred.


## Checkpoint 0.4 — groups and profiles (SUPERSEDED)
- Historical implementation: commit [06654c8](https://github.com/daemien6667/SteamReady/commit/06654c81049f374e9d1b873cd808676707ddbebb). [Windows CI](https://github.com/daemien6667/SteamReady/actions/runs/43330524711) passed compile, fixture tests and portable publish.
- Owner subsequently approved dropping custom groups, priorities and switchable profiles because the three built-in preferences and multi-selection cover the intended use cases. This overrides the earlier Decision #5 and the groups/profiles portion of Decision #6. The old code remains recoverable in Git history; it is not part of the active application or future export format.

## Checkpoint 0.4R — simplified scope (compiled and fixture-tested)
- Remove ProfileStore, profile-only fields and logic, profile application entry point, and profile-specific fixture tests. Restore the previously tested 0.3 preference/history backend and fixtures while preserving current documentation and approved decisions.
- Existing game list, checkboxes, Select all/Clear selection, three-choice dropdown and Apply remain intact. Filter by preference and mixed-selection display are approved functional requirements for the later UI phase; no UI change in this checkpoint.
- Keep persistent per-game preferences, manual exceptions, reinstall detection, 50-batch history with restoration, and planned selective JSON import/export, optional automatic monitoring, Steam shutdown/relaunch consent, optional startup and update notifications.
- No public release or UI redesign authorized. No existing local data files are deleted or migrated by this change; the removed profile engine was backend-only and not exposed through the existing interface.
- [Windows CI](https://github.com/daemien6667/SteamReady/actions/runs/43331085891) completed successfully. Export/import subsequently deferred by owner; proceed to automatic management.


## Checkpoint 0.5 — export/import (DEFERRED)
Owner agreed the simple three-choice workflow makes migration easy without export/import. Remove from initial release roadmap; no code added.

## Checkpoint 0.6 — automatic management policy engine (compiled and fixture-tested)
- Persist validated settings: automatic management disabled by default, new-only scope, immediate target, manual apply; optional continuous scope, auto-after-close and notify modes, manual-change/reinstall confirmation and notifications.
- Rebuild pending candidates from durable observations and fresh complete scans; no automatic writes in manual/notify modes, while Steam is running, or for decisions requiring confirmation. New-only establishes a baseline. Continuous enforces default except explicit choices; SteamReady manual Apply/Undo writes become individual exceptions so automation cannot immediately reverse them.
- Automatic/reinstall actions use normal backup/history writer and remember verified outcomes. Explicit confirmation revalidates current settings, scan and candidate identity/mode; stale decisions fail closed. Keep current creates an exception. Pending candidates survive via saved observations rather than replaying old manifest paths.
- Added isolated fixtures for opt-in, baseline, new-only, continuous, all apply behaviours, Steam running, confirmation, manual exceptions, reinstall, stale plans, disable and history.
- Backend only: no timer, tray, notifications UI, explicit manual batch queue or Steam shutdown/relaunch wired yet. The future serialized foreground/tray controller will call this engine; no new background process or UI changes in this checkpoint.
- [Windows CI](https://github.com/daemien6667/SteamReady/actions/runs/43331411298) passed compile, fixtures and portable publish. Next: graceful Steam shutdown/relaunch consent path, then controller/background/startup and version notifications. Public release and UI redesign remain deferred.


## Checkpoint 0.7 — Steam shutdown/relaunch controller (compiled and fixture-tested)
- User-initiated workflow requires fresh explicit shutdown consent each time the client is idle/running; No leaves it alone. Busy/unknown state falls back to manual closure. Recheck after consent; bounded 30-second polling for full client/helper exit; no force termination. Existing manifest writer performs final closed-client and stale-data checks.
- Relaunch only if this operation requested shutdown and observed closure; do not reopen a client that independently exited or duplicate an externally restarted client. Attempt relaunch after a completed or failed apply, preserve apply result separately from launch errors. Standalone launch refuses duplicate/uncertain state.
- Windows adapter invokes the documented `steam.exe -shutdown` command and starts the verified selected installation executable; registry running-game indicators and accessible process paths under Steam libraries are conservative heuristics, not proof no game is running. Future consent text must ask user to close all games before Yes. Missing indicators, mismatched client path/session, or persistent helper processes can require manual closure. Reference: https://developer.valvesoftware.com/wiki/Command_line_options_(Steam) and https://stackoverflow.com/questions/48136859/c-qt-check-if-a-steam-game-is-running-or-not-programmatically.
- Fixture tests use fake process adapter for decline, repeated consent, running/unknown game state, timeout, rechecks, successful cycle, errors, independent exit, duplicate prevention and standalone launch. No live Steam process accessed in tests.
- Backend only: MainForm unchanged; manual close-and-retry remains the existing UI. Consent dialog and apply/launch event integration are pending, alongside explicit manual batch queue and serialized foreground/tray polling. Real Windows Steam shutdown/relaunch and game-detection checks are required before release; command documentation is not proof of current-client behavior.
- [Windows CI](https://github.com/daemien6667/SteamReady/actions/runs/43331744982) passed compile, fixtures and portable publish. Next: background controller/startup and integration, update notification, UI phase and owner tests.


## Checkpoint 0.8 — runtime/background and explicit queue (compiled and fixture-tested)
- Persist runtime settings (background off, Windows startup off, minimized startup on). Startup command quotes the executable and uses --startup; HKCU Run adapter only changes registration when explicitly called, never at ordinary app launch.
- Functional WinForms ApplicationContext hosts the existing unchanged window, 15-second serialized UI-thread polling, optional tray icon with Open/Exit, hide-on-close only in enabled background mode, startup hide only when configured, clean timer/tray disposal. Manual actions suspend polling. No visual layout redesign; default settings retain normal window and close behavior.
- Durable explicit pending batch is independent of automatic management, supports auto/notify modes, waits for Steam to close, never launches Steam, revalidates complete scan/app identity/path/expected mode/current settings, creates history and exceptions for verified changes. One pending batch at a time; no silent replacement. Mark as needs-review before writes so crash/partial failure cannot blindly replay; cancellation requires matching batch ID. Explicit queue blocks automatic policy work until resolved.
- Optional tray notifications on state changes; settings, pending-review UI, queue creation and confirmation buttons and the shutdown controller's Apply integration remain for functional UI integration. Startup registry adapter not yet called by user-facing settings; no actual startup registration changed by development. Runtime settings can be persisted by backend; default behavior is unchanged.
- Added tests for defaults, close/startup policy, safe startup command, persistence, independent explicit queue, wait, notify confirmation, stale data, mode changes, cancel, and history. Windows registry/tray/timer live behavior requires real Windows validation; tests do not mutate real startup registry.
- Next: update checker; then functional integration and expert UI phase, owner regression tests, release approval.


## Checkpoint 0.9 — update checker and opt-in portable installer (implementation; CI pending)
- Windows CI for 0.8 passed: https://github.com/daemien6667/SteamReady/actions/runs/43332657235.
- Stable GitHub latest-release metadata, strict numeric version comparison, exact repository asset URLs, HTTPS response-host validation, bounded downloads, SHA256SUMS.txt verification, and no offer for same/older versions or missing assets. Automatic check attempt persisted at most once daily, configurable; manual check and skip version.
- Minimal functional controls added to existing top bar: Check for updates, Check automatically, Download & Install version and Skip (the latter two only visible when a newer compatible release is found). Manual check/install displays release notes; install requires explicit confirmation. Background checks can signal tray notification. No visual redesign.
- User-initiated installer stages verified executable beside current portable file, copies current executable as helper, waits for single-instance mutex, rechecks original and staged hashes, replaces executable with previous.exe recovery copy, and starts updated executable. Attempts rollback if process launch fails; no unattended install, no elevation, no changes to Steam or local preferences. Writable portable directory required. Local helper/recovery folder remains for manual recovery; launch success is not proof updated app is healthy.
- Release contract: stable tag vMAJOR.MINOR.PATCH matching assembly version, raw SteamReady.exe and SHA256SUMS.txt attached to official GitHub release. Existing CI artifacts are not public releases. No release created. SHA-256 detects corruption, not independent publisher authentication; signed release binaries and live update/recovery validation are recommended before release.
- Tests cover version gates, asset validation, missing assets, checksums, settings and daily throttle. Actual network download, process replacement, relaunch, rollback and tray behavior need isolated Windows end-to-end testing with a real release or controlled fixture. No automatic or public release authorized.
- Next: functional integration (Apply shutdown consent, queue and management controls, history browser, filtering), expert-led UI design and owner regression tests.


## Checkpoint 1.0-alpha — functional integration (implementation; CI pending)
- Connected the existing Apply and Undo actions plus historical restore to the consent-gated Steam controller. Added Launch Steam, explicit queue action, review/cancel pending changes, automatic management decisions, saved games and forget uninstalled app, and settings dialog for runtime/management/retention.
- Added current-mode filtering and mixed-selection dropdown placeholder; select all/clear apply to visible games only, and Apply only targets checked visible games. No mixed value can be written. Refresh preserves selections where possible.
- Settings explicitly control startup registry, automatic-write permission and tray operation. Polling is paused during modal/explicit operations. Existing layout style is retained, with development controls added; not final UI design.
- Baseline observation now runs on manual scans even with automation disabled; verified writes and restores retain existing history/preferences safeguards.
- Remaining: CI compile/fixture run; real Windows end-to-end owner testing for all functional controls, including live Steam shutdown/relaunch, startup/tray, pending notifications, and updater replacement. Settings writes span separate files/registry and are not transactional. No release created. UI expert to refine controls, messaging and layout after functional validation.
