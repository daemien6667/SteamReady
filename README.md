# SteamReady

**Every game. Updated. Ready to play.**

A free, independent Windows utility for changing Steam's installed-app update preferences in bulk. No Steam login, telemetry, or background service.

## Status

Early development / pre-release. Do not use on a real Steam library until the Windows build and Steam-client compatibility have been verified.

## Planned v1

- Detect the Steam installation and all registered libraries.
- Display installed apps and current update preference; select all or individual apps.
- Apply "Immediately download updates", "Follow Steam's default", or "Only when launched".
- Require Steam to exit normally before writing; never force-close Steam.
- Preview changes, preserve original manifests in local backups, use safe replacement and provide preference-only undo.
- Portable Windows x64 executable, built from public source.

Steam's appmanifest files are an undocumented implementation detail. Community evidence maps AutoUpdateBehavior 0 to default, 1 to on launch, 2 to immediate/high priority. Confirm all three against the current client before release. Steam still controls its download queue, bandwidth, scheduling and in-game restrictions; SteamReady does not force a download or change game content.

References:
- [Steam download settings](https://help.steampowered.com/en/faqs/view/71ab-698d-57eb-178c)
- [Steam community manifest mapping](https://steamcommunity.com/discussions/forum/1/595140603083242373/)

## Development

See [project plan](docs/PROJECT.md) when available. Target: .NET 10 LTS, Windows Forms, self-contained x64 publish. No official Valve affiliation. Steam is a trademark of Valve Corporation.
