# SmartCleanup Changelog

## v0.4.0

- Added layered event protection for RaidableBases, AirfieldEvent, MonumentAddons, CopyPaste, unsaved entities, plugin-owned entities, monument bounds, and protected skin IDs.
- Added `API_ProtectEntity`, `API_UnprotectEntity`, and `CanSmartCleanupEntity` integration paths for future event plugins.
- Added whole-connected-building protection so cleanup cannot partially remove a protected base.
- Persisted entity first-seen/activity state and recent-owner activity across plugin reloads and server restarts, with automatic wipe reset.
- Replaced synchronous full-index rebuilding with bounded per-tick batches and periodic reconciliation.
- Added a mandatory successful dry-run and 120-second confirmation window before manual cleanup execution.
- Fixed profile settings being unintentionally overwritten by advanced values; added explicit override switches.
- Made `AutoTuneWriteToConfig` pin the resolved profile only when explicitly enabled.
- Added invalid-config backup and fail-safe testing mode instead of silently running scheduled cleanup with defaults.
- Added event and connected-structure totals to dry-run/run summaries and expanded status reporting.
- Updated configuration schema to version 7.

## v0.3.1

- Added default Always Allow Cleanup prefabs: `campfire`, `lantern.deployed`, and `bbq.deployed`.
- Improved config migration logic.
- Updated config schema to version 6.

## v0.3.0

- Added testing mode and scheduled cleanup logging controls.
- Improved deployable classification and rebuild age preservation.
- Reduced scheduled-run console noise and improved dry-run reporting.

## v0.2.x

- Added deployable cleanup categories, prefab override lists, and protected-reason reporting.
- Fixed recent-owner protection override.

## v0.1.x

- Initial SmartCleanup rewrite with TC protection, disconnected structure cleanup, and admin commands.
