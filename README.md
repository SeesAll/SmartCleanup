# SmartCleanup (Rust / Carbon and uMod Plugin)

**Author:** SeesAll

**Version:** 0.4.0

SmartCleanup is a safety-first replacement for legacy Rust cleanup plugins such as EntityCleanup. It identifies abandoned structures and selected deployables without repeatedly scanning and mutating the server's entire entity collection.

## Highlights

- Building and deployable cleanup with category controls
- Fast PvP, Balanced, PvE/Conservative, Custom, and auto-detected profiles
- Persistent entity age and owner-activity state across reloads/restarts
- Bounded startup/reconciliation indexing and cleanup batches
- Dry-run required before a manual destructive run
- Whole-building protection when any connected part is protected
- Event protection for RaidableBases, AirfieldEvent, MonumentAddons, CopyPaste, monument entities, unsaved entities, non-Steam owners, and configured skin IDs
- Extensible protection API for present and future event plugins
- Invalid-config backup and fail-safe scheduled-cleanup shutdown

## Safety Model

SmartCleanup checks protections before cleanup eligibility. By default it protects:

- whitelisted and never-clean prefabs;
- recently active owners;
- entities inside TC privilege and TC-authorized owners;
- an entire connected building when any part of it is protected;
- event and monument entities;
- unsaved or plugin-owned entities;
- entities registered by another plugin.

The default monument protection is intentionally conservative. If a server deliberately wants abandoned player deployables inside monuments cleaned, disable `Protect Entities Inside Monuments` only after testing with all installed event plugins.

## Profiles and Overrides

1. Auto Detect
2. Fast PvP
3. Balanced
4. PvE / Conservative
5. Custom

Profile defaults now remain authoritative unless the matching `Override Profile ...` switch is enabled. Custom profile values are always taken from the advanced settings. `AutoTuneWriteToConfig` pins the detected profile when explicitly enabled; set `ServerProfile` back to `1` to resume automatic detection.

## Admin Commands

Permission: `smartcleanup.admin`

```text
/smartcleanup status
/smartcleanup dryrun
/smartcleanup run confirm
/smartcleanup retune
/smartcleanup rebuild
```

`dryrun` performs no removals. A successful manual dry-run opens a 120-second confirmation window for `run confirm`. Scheduled cleanup does not require this manual confirmation gate.

## Event Plugin Integration

SmartCleanup has built-in conservative protection, but event authors can explicitly protect entities:

```csharp
SmartCleanup?.Call("API_ProtectEntity", entity, "MyEvent");
SmartCleanup?.Call("API_UnprotectEntity", entity, "MyEvent");
```

Alternatively, another plugin may block cleanup for an entity:

```csharp
private object CanSmartCleanupEntity(BaseEntity entity)
{
    return IsMyEventEntity(entity) ? (object)false : null;
}
```

Register event entities when the event starts and unregister them when it ends. Entity destruction is handled automatically.

## Testing and Deployment

For a first deployment, enable:

```json
"Disable Scheduled Cleanup For Testing": true
```

Then reload SmartCleanup, wait for the bounded index rebuild to finish, check `/smartcleanup status`, and run `/smartcleanup dryrun`. Only disable testing mode after reviewing the dry-run totals and protected-reason breakdown.

SmartCleanup stores runtime state in `oxide/data/SmartCleanup_State.json`. The state is reset automatically on a new Rust save. Configuration remains in `oxide/config/SmartCleanup.json` and is merged forward during schema upgrades.

Never run SmartCleanup and another automatic entity cleanup plugin at the same time.

## Default Cleanup Categories

Production, lighting, and trap deployables are eligible by default. Storage, workbenches, privilege, commerce, electrical/industrial, water/farming, and utility deployables remain protected unless enabled in configuration.

Default always-eligible clutter prefabs are `campfire`, `lantern.deployed`, and `bbq.deployed`; they still must pass age, location, activity, event, and other safety checks.

## License

MIT License
