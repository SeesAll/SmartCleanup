# SmartCleanup (Rust / uMod Plugin)

**Author:** SeesAll\
**Version:** 0.3.1

SmartCleanup is a modern replacement for legacy Rust cleanup plugins
such as EntityCleanup. It safely removes abandoned structures and
deployables while protecting legitimate player builds.

------------------------------------------------------------------------

## Why SmartCleanup Exists

Older cleanup plugins treated all deployables the same which could lead
to: - accidental removal of legitimate player storage - unsafe cleanup
inside base areas - unnecessary performance overhead

SmartCleanup introduces category-aware cleanup, safer privilege
detection, and performance-focused evaluation.

------------------------------------------------------------------------

## Key Features

### Intelligent Cleanup Categories

Server owners can control which deployable types are eligible for
cleanup:

-   Production deployables (furnaces, BBQs, campfires)
-   Lighting deployables (lanterns, tuna lamps)
-   Traps (bear traps, landmines, spike traps)
-   Utility deployables
-   Water and farming deployables
-   Electrical and industrial deployables
-   Storage deployables
-   Workbench deployables
-   Privilege deployables
-   Commerce deployables

------------------------------------------------------------------------

### Safety Systems

SmartCleanup includes multiple layers of protection:

-   TC privilege detection
-   Connected structure protection
-   Recent player activity protection
-   Health thresholds
-   Never-clean prefab list
-   Force-allow prefab list

These systems prevent accidental cleanup of legitimate bases.

------------------------------------------------------------------------

### Server Profiles

SmartCleanup supports preset environments:

1.  Auto Detect
2.  Fast PvP
3.  Balanced
4.  PvE / Conservative
5.  Custom

Profiles automatically adjust cleanup timings based on server style.

------------------------------------------------------------------------

### Performance-Focused Design

Compared to older cleanup plugins SmartCleanup improves performance by:

-   Tracking cleanup candidates instead of repeatedly scanning all
    entities
-   Evaluating entities in controlled batches
-   Running scheduled checks at configurable intervals
-   Avoiding unnecessary processing of protected structures

This design scales significantly better on medium and large Rust
servers.

------------------------------------------------------------------------

### Logging Controls

Admins can choose how much information appears in console:

-   Debug logging
-   Scheduled cleanup summaries
-   Removal-only logging (recommended for production)

------------------------------------------------------------------------

## Default Always-Allow Cleanup Prefabs

SmartCleanup always allows cleanup for common clutter deployables:

    campfire
    lantern.deployed
    bbq.deployed

These objects are frequently abandoned across the map and safe to remove
when outside TC.

------------------------------------------------------------------------

## Admin Commands

    /smartcleanup status
    /smartcleanup dryrun
    /smartcleanup run
    /smartcleanup rebuild

### Command Descriptions

  Command                 Description
  ----------------------- ---------------------------------------------
  /smartcleanup status    Shows current runtime configuration
  /smartcleanup dryrun    Simulates cleanup without removing entities
  /smartcleanup run       Executes cleanup immediately
  /smartcleanup rebuild   Rebuilds the internal candidate tracker

------------------------------------------------------------------------

## Testing Mode

For testing cleanup behaviour administrators can disable scheduled
cleanup:

    "Disable Scheduled Cleanup For Testing": true

------------------------------------------------------------------------

## Performance Notes

SmartCleanup was built with performance in mind.

Compared to legacy plugins it:

-   reduces full entity scans
-   processes entities in controlled batches
-   skips protected structures early
-   uses category filtering to minimize work

The result is a cleanup system that scales much better on long-wipe
servers with large entity counts.

------------------------------------------------------------------------

## License

MIT License
