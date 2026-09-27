using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("SmartCleanup", "SeesAll", "0.4.1")]
    [Description("Safe adaptive cleanup with persistent activity tracking, event protection, dry-runs, and bounded processing.")]
    public class SmartCleanup : CovalencePlugin
    {
        private const string PermAdmin = "smartcleanup.admin";
        private const string DataFileName = "SmartCleanup_State";
        private const double RunConfirmationLifetimeSeconds = 120d;

        [PluginReference] private Plugin RaidableBases;
        [PluginReference] private Plugin MonumentAddons;

        private ConfigData _config;
        private RuntimeSettings _runtime;

        private readonly Dictionary<uint, TrackedEntity> _tracked = new Dictionary<uint, TrackedEntity>();
        private readonly Dictionary<ulong, double> _lastOwnerSeenUtc = new Dictionary<ulong, double>();
        private readonly HashSet<string> _deployablePrefabs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _whitelist = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _neverCleanupPrefabs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _alwaysCleanupPrefabs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<uint> _airfieldEventEntities = new HashSet<uint>();
        private readonly HashSet<uint> _registeredProtectedEntities = new HashSet<uint>();
        private readonly Dictionary<uint, double> _temporaryProtectedUntilUtc = new Dictionary<uint, double>();
        private readonly Dictionary<string, double> _pendingRunConfirmations = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        private bool _initialized;
        private bool _runInProgress;
        private bool _rebuildInProgress;
        private bool _airfieldEventActive;
        private bool _dataDirty;
        private bool _configLoadFailed;
        private Timer _scheduledTimer;
        private Timer _dataSaveTimer;
        private Timer _reconciliationTimer;
        private CleanupJob _activeJob;
        private StoredData _storedData;
        private List<NetworkableId> _rebuildIds;
        private Dictionary<uint, TrackedEntity> _rebuildPrevious;
        private int _rebuildIndex;
        private bool _rebuildManual;
        private string _rebuildRequesterId;

        private enum CleanupMode
        {
            DryRun,
            Execute
        }

        private enum EntityKind
        {
            Unknown,
            Building,
            Deployable
        }

        private enum DeployableCategory
        {
            Unknown,
            Production,
            Lighting,
            Trap,
            Utility,
            WaterAndFarming,
            ElectricalAndIndustrial,
            Storage,
            Workbench,
            Privilege,
            Commerce
        }

        private class CleanupJob
        {
            public CleanupMode Mode;
            public List<uint> CandidateIds;
            public int Index;
            public int Removed;
            public int Evaluated;
            public int SkippedProtected;
            public int SkippedProtectedByCategory;
            public int SkippedProtectedOwnerless;
            public int SkippedProtectedByRecentActivity;
            public int SkippedProtectedInsidePrivilege;
            public int SkippedProtectedByCupboardAuth;
            public int SkippedProtectedOutsideDisabled;
            public int SkippedProtectedByEvent;
            public int SkippedProtectedByConnectedStructure;
            public int SkippedWhitelisted;
            public int SkippedInactiveTooSoon;
            public int SkippedHealthTooHigh;
            public int SkippedMissing;
            public int BuildingMatches;
            public int DeployableMatches;
            public bool ManualRequest;
            public string Trigger;
            public string RequesterId;
            public string RequesterName;
            public double StartedUtc;
            public readonly Dictionary<uint, bool> ConnectedStructureProtection = new Dictionary<uint, bool>();
        }

        private class TrackedEntity
        {
            public uint NetId;
            public ulong OwnerId;
            public EntityKind Kind;
            public DeployableCategory DeployableCategory;
            public string ShortPrefabName;
            public string PrefabName;
            public double FirstSeenUtc;
            public double LastActivityUtc;
            public double LastRefreshUtc;
            public bool InsideMonument;
        }

        private class PersistedEntityState
        {
            public double FirstSeenUtc;
            public double LastActivityUtc;
        }

        private class StoredData
        {
            public long WipeCreatedUtcTicks;
            public Dictionary<ulong, double> LastOwnerSeenUtc = new Dictionary<ulong, double>();
            public Dictionary<uint, PersistedEntityState> EntityStates = new Dictionary<uint, PersistedEntityState>();
            public Dictionary<uint, double> TemporaryProtectedUntilUtc = new Dictionary<uint, double>();
        }

        private class RuntimeSettings
        {
            public int ResolvedProfile;
            public string ResolvedProfileName;
            public double ScheduledEvaluationIntervalMinutes;
            public int MaxCandidatesPerTick;
            public double DeployablesOutsidePrivilegeCleanupHours;
            public double DisconnectedStructuresCleanupHours;
            public double InsidePrivilegeCleanupHours;
            public float OutsideHealthFractionThreshold;
            public float InsideHealthFractionThreshold;
            public double ProtectRecentlyActivePlayersHours;
            public bool CleanupBuildings;
            public bool CleanupDeployables;
            public bool RemoveInsidePrivilege;
            public bool RemoveOutsidePrivilege;
            public bool CheckCupboardAuthorization;
            public bool AnnounceScheduledCleanup;
            public bool AutoTuneEnabled;
        }

        #region Oxide Hooks

        private void Init()
        {
            permission.RegisterPermission(PermAdmin, this);
            AddCovalenceCommand("smartcleanup", nameof(CommandSmartCleanup));
        }

        private void OnServerInitialized()
        {
            LoadStoredData();
            BuildDeployableLookup();
            RebuildWhitelist();
            RebuildPrefabOverrides();
            ResolveRuntimeSettings(writeToConfig: false, logResolution: true);
            SeedRecentPlayers();
            _initialized = true;
            StartDataSaveTimer();
            StartTrackingIndexRebuild(manualRequest: false, requester: null);
        }

        private void Unload()
        {
            _scheduledTimer?.Destroy();
            _scheduledTimer = null;
            _dataSaveTimer?.Destroy();
            _dataSaveTimer = null;
            _reconciliationTimer?.Destroy();
            _reconciliationTimer = null;
            SaveStoredData(force: true);
            _tracked.Clear();
            _lastOwnerSeenUtc.Clear();
            _airfieldEventEntities.Clear();
            _registeredProtectedEntities.Clear();
            _temporaryProtectedUntilUtc.Clear();
            _pendingRunConfirmations.Clear();
            _rebuildIds = null;
            _rebuildPrevious = null;
            _activeJob = null;
            _runInProgress = false;
            _rebuildInProgress = false;
            _initialized = false;
        }

        private void OnServerSave()
        {
            SaveStoredData(force: false);
        }

        private void OnNewSave(string filename)
        {
            _lastOwnerSeenUtc.Clear();
            _temporaryProtectedUntilUtc.Clear();
            _storedData = new StoredData { WipeCreatedUtcTicks = GetWipeCreatedUtcTicks() };
            _dataDirty = true;
            SaveStoredData(force: true);
        }

        private void OnPlayerConnected(BasePlayer player)
        {
            if (player == null || player.userID == 0UL)
            {
                return;
            }

            _lastOwnerSeenUtc[player.userID] = UtcNow();
            MarkDataDirty();
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player == null || player.userID == 0UL)
            {
                return;
            }

            _lastOwnerSeenUtc[player.userID] = UtcNow();
            MarkDataDirty();
        }

        private void OnEntitySpawned(BaseNetworkable networkable)
        {
            if (!_initialized)
            {
                return;
            }

            var entity = networkable as BaseEntity;
            if (entity == null)
            {
                return;
            }

            NextTick(() =>
            {
                if (entity == null || entity.IsDestroyed || entity.net == null)
                {
                    return;
                }

                var id = (uint)entity.net.ID.Value;
                if (_airfieldEventActive)
                {
                    _airfieldEventEntities.Add(id);
                }

                TryTrackEntity(entity);
            });
        }

        private void OnEntityKill(BaseNetworkable networkable)
        {
            var entity = networkable as BaseEntity;
            if (entity?.net == null)
            {
                return;
            }

            var id = (uint)entity.net.ID.Value;
            _tracked.Remove(id);
            _airfieldEventEntities.Remove(id);
            _registeredProtectedEntities.Remove(id);
            _temporaryProtectedUntilUtc.Remove(id);
            if (_storedData?.EntityStates != null)
            {
                _storedData.EntityStates.Remove(id);
            }

            MarkDataDirty();
        }

        private void OnEntityTakeDamage(BaseCombatEntity combatEntity, HitInfo info)
        {
            if (combatEntity?.net == null)
            {
                return;
            }

            TrackedEntity tracked;
            if (_tracked.TryGetValue((uint)combatEntity.net.ID.Value, out tracked))
            {
                MarkEntityActivity(tracked, UtcNow());
            }
        }

        private void OnStructureRepair(BaseCombatEntity entity, BasePlayer player)
        {
            MarkEntityActivity(entity);
        }

        private void OnStructureUpgrade(BuildingBlock block, BasePlayer player, BuildingGrade.Enum grade)
        {
            MarkEntityActivity(block);
        }

        private void AirfieldEventStarted()
        {
            _airfieldEventActive = true;
            Puts("AirfieldEvent started; newly spawned entities will be protected from cleanup.");
        }

        private void AirfieldEventEnded()
        {
            _airfieldEventActive = false;
            _airfieldEventEntities.Clear();
            Puts("AirfieldEvent ended; temporary spawn protection was released.");
        }

        private void OnPasteFinished(List<BaseEntity> pastedEntities, string filename, BasePlayer player, Vector3 position)
        {
            if (!_config.EventSafety.Enabled || pastedEntities == null || _config.EventSafety.CopyPasteProtectionHours <= 0d)
            {
                return;
            }

            var expires = UtcNow() + (_config.EventSafety.CopyPasteProtectionHours * 3600d);
            foreach (var entity in pastedEntities)
            {
                if (entity?.net == null || entity.IsDestroyed)
                {
                    continue;
                }

                _temporaryProtectedUntilUtc[(uint)entity.net.ID.Value] = expires;
            }

            MarkDataDirty();
        }

        private void OnRaidableBaseStarted(object payload)
        {
            RegisterRaidableBasePayload(payload);
        }

        private void OnRaidableBaseEnded(object payload)
        {
            UnregisterRaidableBasePayload(payload);
        }

        private void OnRaidableBaseDespawned(object payload)
        {
            UnregisterRaidableBasePayload(payload);
        }

        #endregion

        #region Commands

        private void CommandSmartCleanup(IPlayer player, string command, string[] args)
        {
            if (!IsAuthorized(player))
            {
                Reply(player, "You do not have permission to use SmartCleanup.");
                return;
            }

            if (args == null || args.Length == 0)
            {
                Reply(player, "SmartCleanup commands: /smartcleanup status, dryrun, run confirm, retune, rebuild, help");
                return;
            }

            switch (args[0].ToLowerInvariant())
            {
                case "help":
                    Reply(player, "Commands: /smartcleanup status, /smartcleanup dryrun, /smartcleanup run confirm, /smartcleanup retune, /smartcleanup rebuild");
                    break;

                case "status":
                    Reply(player, BuildStatusText());
                    break;

                case "dryrun":
                    if (_runInProgress || _rebuildInProgress)
                    {
                        Reply(player, "A SmartCleanup job or index rebuild is already running.");
                        return;
                    }

                    StartCleanupJob(CleanupMode.DryRun, manualRequest: true, trigger: $"manual dryrun by {player.Name}", requester: player);
                    break;

                case "run":
                    if (_runInProgress || _rebuildInProgress)
                    {
                        Reply(player, "A SmartCleanup job or index rebuild is already running.");
                        return;
                    }

                    if (args.Length < 2 || !args[1].Equals("confirm", StringComparison.OrdinalIgnoreCase))
                    {
                        Reply(player, "Cleanup execution is locked. Run /smartcleanup dryrun first, review the result, then use /smartcleanup run confirm within 120 seconds.");
                        return;
                    }

                    double confirmationExpires;
                    if (!_pendingRunConfirmations.TryGetValue(player.Id, out confirmationExpires) || confirmationExpires < UtcNow())
                    {
                        _pendingRunConfirmations.Remove(player.Id);
                        Reply(player, "No current dry-run approval exists. Run /smartcleanup dryrun first.");
                        return;
                    }

                    _pendingRunConfirmations.Remove(player.Id);
                    StartCleanupJob(CleanupMode.Execute, manualRequest: true, trigger: $"manual run by {player.Name}", requester: player);
                    break;

                case "retune":
                    RebuildWhitelist();
                    RebuildPrefabOverrides();
                    ResolveRuntimeSettings(writeToConfig: _config.AutoTuneWriteToConfig, logResolution: true);
                    StartSchedule();
                    Reply(player, $"Retuned SmartCleanup. Active profile: {_runtime.ResolvedProfileName} ({_runtime.ResolvedProfile}).");
                    break;

                case "rebuild":
                    if (_runInProgress || _rebuildInProgress)
                    {
                        Reply(player, "A SmartCleanup job or index rebuild is already running.");
                        return;
                    }

                    RebuildWhitelist();
                    RebuildPrefabOverrides();
                    StartTrackingIndexRebuild(manualRequest: true, requester: player);
                    break;

                default:
                    Reply(player, "Unknown command. Use /smartcleanup help");
                    break;
            }
        }

        #endregion

        #region Tracking / Classification

        private void StartTrackingIndexRebuild(bool manualRequest, IPlayer requester)
        {
            if (_rebuildInProgress || _runInProgress)
            {
                Reply(requester, "A SmartCleanup job or index rebuild is already running.");
                return;
            }

            _rebuildInProgress = true;
            _rebuildManual = manualRequest;
            _rebuildRequesterId = requester?.Id;
            _rebuildPrevious = new Dictionary<uint, TrackedEntity>(_tracked);
            _tracked.Clear();
            _rebuildIds = new List<NetworkableId>();
            _rebuildIndex = 0;

            foreach (var networkable in BaseNetworkable.serverEntities)
            {
                if (networkable?.net != null)
                {
                    _rebuildIds.Add(networkable.net.ID);
                }
            }

            if (manualRequest)
            {
                Reply(requester, $"Started bounded tracking rebuild for {_rebuildIds.Count} server entities.");
            }

            ProcessTrackingIndexBatch();
        }

        private void ProcessTrackingIndexBatch()
        {
            if (!_rebuildInProgress || _rebuildIds == null)
            {
                return;
            }

            var budget = Mathf.Clamp(_config.AdvancedPerformance.IndexCandidatesPerTick, 50, 5000);
            var processed = 0;
            while (processed < budget && _rebuildIndex < _rebuildIds.Count)
            {
                var entity = BaseNetworkable.serverEntities.Find(_rebuildIds[_rebuildIndex++]) as BaseEntity;
                processed++;
                if (entity != null && !entity.IsDestroyed)
                {
                    TryTrackEntity(entity, _rebuildPrevious);
                }
            }

            if (_rebuildIndex < _rebuildIds.Count)
            {
                NextTick(ProcessTrackingIndexBatch);
                return;
            }

            FinishTrackingIndexRebuild();
        }

        private void FinishTrackingIndexRebuild()
        {
            _rebuildInProgress = false;
            _rebuildIds = null;
            _rebuildPrevious = null;
            PruneStoredEntityStates();
            SaveStoredData(force: false);

            var message = $"Tracking rebuild complete. Tracking {_tracked.Count} cleanup candidate(s).";
            if (_rebuildManual && !string.IsNullOrEmpty(_rebuildRequesterId))
            {
                Reply(players.FindPlayerById(_rebuildRequesterId), message);
            }
            else
            {
                Puts(message);
            }

            _rebuildManual = false;
            _rebuildRequesterId = null;
            StartSchedule();
            StartReconciliationTimer();
        }

        private void TryTrackEntity(BaseEntity entity, Dictionary<uint, TrackedEntity> previous = null)
        {
            if (entity == null || entity.IsDestroyed || entity.net == null)
            {
                return;
            }

            if (entity.OwnerID == 0UL)
            {
                return;
            }

            if (entity.parentEntity.IsSet())
            {
                return;
            }

            var kind = Classify(entity);
            if (kind == EntityKind.Unknown)
            {
                return;
            }

            var id = (uint)entity.net.ID.Value;
            var now = UtcNow();

            TrackedEntity tracked;
            if (!_tracked.TryGetValue(id, out tracked))
            {
                TrackedEntity existing = null;
                if (previous != null)
                {
                    previous.TryGetValue(id, out existing);
                }

                PersistedEntityState persistedState = null;
                _storedData?.EntityStates?.TryGetValue(id, out persistedState);
                var firstSeen = existing?.FirstSeenUtc ?? persistedState?.FirstSeenUtc ?? now;
                var lastActivity = Math.Max(existing?.LastActivityUtc ?? 0d, persistedState?.LastActivityUtc ?? 0d);

                tracked = new TrackedEntity
                {
                    NetId = id,
                    OwnerId = entity.OwnerID,
                    Kind = kind,
                    DeployableCategory = kind == EntityKind.Deployable ? GetDeployableCategory(entity) : DeployableCategory.Unknown,
                    ShortPrefabName = entity.ShortPrefabName ?? string.Empty,
                    PrefabName = entity.PrefabName ?? string.Empty,
                    FirstSeenUtc = firstSeen,
                    LastActivityUtc = lastActivity,
                    LastRefreshUtc = now,
                    InsideMonument = IsInsideMonument(entity.transform.position)
                };

                _tracked[id] = tracked;
                StoreTrackedEntityState(tracked);
                return;
            }

            tracked.OwnerId = entity.OwnerID;
            tracked.Kind = kind;
            tracked.DeployableCategory = kind == EntityKind.Deployable ? GetDeployableCategory(entity) : DeployableCategory.Unknown;
            tracked.ShortPrefabName = entity.ShortPrefabName ?? string.Empty;
            tracked.PrefabName = entity.PrefabName ?? string.Empty;
            tracked.LastRefreshUtc = now;
            tracked.InsideMonument = IsInsideMonument(entity.transform.position);
            StoreTrackedEntityState(tracked);
        }

        private EntityKind Classify(BaseEntity entity)
        {
            if (entity == null)
            {
                return EntityKind.Unknown;
            }

            if (entity is StabilityEntity)
            {
                return EntityKind.Building;
            }

            if (_deployablePrefabs.Contains(entity.PrefabName ?? string.Empty) ||
                _deployablePrefabs.Contains(entity.ShortPrefabName ?? string.Empty) ||
                _deployablePrefabs.Contains(entity.gameObject?.name ?? string.Empty))
            {
                return EntityKind.Deployable;
            }

            return EntityKind.Unknown;
        }

        private void BuildDeployableLookup()
        {
            _deployablePrefabs.Clear();

            foreach (var itemDef in ItemManager.GetItemDefinitions())
            {
                var modDeployable = itemDef?.GetComponent<ItemModDeployable>();
                if (modDeployable?.entityPrefab == null)
                {
                    continue;
                }

                var path = modDeployable.entityPrefab.resourcePath ?? string.Empty;
                if (!string.IsNullOrEmpty(path))
                {
                    _deployablePrefabs.Add(path);
                    _deployablePrefabs.Add(path.ToLowerInvariant());

                    var pathParts = path.Split('/');
                    var fileName = pathParts.Length > 0 ? pathParts[pathParts.Length - 1] : path;
                    _deployablePrefabs.Add(fileName);
                }
            }
        }

        private void RebuildWhitelist()
        {
            _whitelist.Clear();

            foreach (var entry in _config.Whitelist ?? new string[0])
            {
                var normalized = NormalizePrefabKey(entry);
                if (string.IsNullOrEmpty(normalized))
                {
                    continue;
                }

                _whitelist.Add(normalized);
            }
        }

        private void RebuildPrefabOverrides()
        {
            _neverCleanupPrefabs.Clear();
            _alwaysCleanupPrefabs.Clear();

            foreach (var entry in _config.NeverCleanupPrefabs ?? new string[0])
            {
                var normalized = NormalizePrefabKey(entry);
                if (!string.IsNullOrEmpty(normalized))
                {
                    _neverCleanupPrefabs.Add(normalized);
                }
            }

            foreach (var entry in _config.AlwaysCleanupPrefabs ?? new string[0])
            {
                var normalized = NormalizePrefabKey(entry);
                if (!string.IsNullOrEmpty(normalized))
                {
                    _alwaysCleanupPrefabs.Add(normalized);
                }
            }
        }

        private string NormalizePrefabKey(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();
        }

        private bool MatchesPrefabList(HashSet<string> set, BaseEntity entity, TrackedEntity tracked)
        {
            if (set == null || set.Count == 0 || entity == null)
            {
                return false;
            }

            return set.Contains(NormalizePrefabKey(entity.ShortPrefabName)) ||
                   set.Contains(NormalizePrefabKey(entity.PrefabName)) ||
                   set.Contains(NormalizePrefabKey(entity.gameObject?.name)) ||
                   (tracked != null && (set.Contains(NormalizePrefabKey(tracked.ShortPrefabName)) || set.Contains(NormalizePrefabKey(tracked.PrefabName))));
        }

        private DeployableCategory GetDeployableCategory(BaseEntity entity)
        {
            var shortName = NormalizePrefabKey(entity?.ShortPrefabName);
            var prefabName = NormalizePrefabKey(entity?.PrefabName);

            if (ContainsAny(shortName, prefabName, "cupboard.tool", "cupboard.tool.deployed"))
                return DeployableCategory.Privilege;

            if (ContainsAny(shortName, prefabName, "workbench1.deployed", "workbench2.deployed", "workbench3.deployed"))
                return DeployableCategory.Workbench;

            if (ContainsAny(shortName, prefabName, "vendingmachine.deployed", "shopfront.deployed"))
                return DeployableCategory.Commerce;

            if (ContainsAny(shortName, prefabName,
                "woodbox_deployed", "box.wooden.large", "coffin.storage", "small_stash_deployed",
                "fridge.deployed", "locker.deployed", "dropbox.deployed"))
                return DeployableCategory.Storage;

            if (ContainsAny(shortName, prefabName,
                "repairbench_deployed", "researchtable_deployed", "mixingtable.deployed"))
                return DeployableCategory.Utility;

            if (ContainsAny(shortName, prefabName,
                "furnace", "furnace.large", "refinery_small_deployed", "campfire", "bbq.deployed",
                "fireplace.deployed", "hobobarrel.deployed", "smoker.deployed", "oven"))
                return DeployableCategory.Production;

            if (ContainsAny(shortName, prefabName,
                "lantern.deployed", "lantern", "tunalight.deployed", "tuna-can-lamp.deployed",
                "ceilinglight.deployed", "searchlight.deployed", "light", "candlehat"))
                return DeployableCategory.Lighting;

            if (ContainsAny(shortName, prefabName,
                "beartrap", "bear.trap", "landmine", "spikes.floor", "spikes.floor.metal",
                "floor.spikes", "spike", "trap", "barricade.concrete", "barricade.metal", "barricade.wood"))
                return DeployableCategory.Trap;

            if (ContainsAny(shortName, prefabName,
                "water_catcher_small", "water_catcher_large", "planter.small", "planter.large",
                "composter", "sprinkler.deployed"))
                return DeployableCategory.WaterAndFarming;

            if (ContainsAny(shortName, prefabName,
                "electric", "battery", "generator", "switch", "conveyor", "industrial", "splitter", "combiner"))
                return DeployableCategory.ElectricalAndIndustrial;

            return DeployableCategory.Unknown;
        }

        private bool ContainsAny(string shortName, string prefabName, params string[] patterns)
        {
            foreach (var pattern in patterns)
            {
                var normalized = NormalizePrefabKey(pattern);
                if ((!string.IsNullOrEmpty(shortName) && shortName.Contains(normalized)) ||
                    (!string.IsNullOrEmpty(prefabName) && prefabName.Contains(normalized)))
                {
                    return true;
                }
            }

            return false;
        }

        private bool IsDeployableCategoryEnabled(DeployableCategory category)
        {
            switch (category)
            {
                case DeployableCategory.Production:
                    return _config.CategorySettings.CleanupProductionDeployables;
                case DeployableCategory.Lighting:
                    return _config.CategorySettings.CleanupLightingDeployables;
                case DeployableCategory.Trap:
                    return _config.CategorySettings.CleanupTrapDeployables;
                case DeployableCategory.Utility:
                    return _config.CategorySettings.CleanupUtilityDeployables;
                case DeployableCategory.WaterAndFarming:
                    return _config.CategorySettings.CleanupWaterAndFarmingDeployables;
                case DeployableCategory.ElectricalAndIndustrial:
                    return _config.CategorySettings.CleanupElectricalAndIndustrialDeployables;
                case DeployableCategory.Storage:
                    return _config.CategorySettings.CleanupStorageDeployables;
                case DeployableCategory.Workbench:
                    return _config.CategorySettings.CleanupWorkbenchDeployables;
                case DeployableCategory.Privilege:
                    return _config.CategorySettings.CleanupPrivilegeDeployables;
                case DeployableCategory.Commerce:
                    return _config.CategorySettings.CleanupCommerceDeployables;
                default:
                    return false;
            }
        }

        private void SeedRecentPlayers()
        {
            var now = UtcNow();
            foreach (var player in BasePlayer.activePlayerList)
            {
                if (player == null || player.userID == 0UL)
                {
                    continue;
                }

                _lastOwnerSeenUtc[player.userID] = now;
            }
        }

        #endregion

        #region Scheduling / Jobs

        private void StartSchedule()
        {
            _scheduledTimer?.Destroy();
            _scheduledTimer = null;

            if (_configLoadFailed)
            {
                PrintError("Scheduled cleanup is disabled because the configuration failed to load. Repair or regenerate the config, then reload SmartCleanup.");
                return;
            }

            if (_config.AdvancedTesting.DisableScheduledCleanupForTesting)
            {
                Puts("Scheduled cleanup is disabled by testing mode.");
                return;
            }

            if (_runtime.ScheduledEvaluationIntervalMinutes <= 0d)
            {
                Puts("Scheduled cleanup is disabled.");
                return;
            }

            _scheduledTimer = timer.Every((float)(_runtime.ScheduledEvaluationIntervalMinutes * 60d), () =>
            {
                if (_runInProgress || _rebuildInProgress)
                {
                    return;
                }

                StartCleanupJob(CleanupMode.Execute, manualRequest: false, trigger: "scheduled run", requester: null);
            });

            Puts($"Scheduled cleanup every {_runtime.ScheduledEvaluationIntervalMinutes:0.##} minute(s).");
        }

        private void StartCleanupJob(CleanupMode mode, bool manualRequest, string trigger, IPlayer requester)
        {
            if (_runInProgress || _rebuildInProgress)
            {
                Reply(requester, "A SmartCleanup job or index rebuild is already running.");
                return;
            }

            _runInProgress = true;
            _activeJob = new CleanupJob
            {
                Mode = mode,
                CandidateIds = _tracked.Keys.ToList(),
                Index = 0,
                ManualRequest = manualRequest,
                Trigger = trigger,
                RequesterId = requester?.Id,
                RequesterName = requester?.Name,
                StartedUtc = UtcNow()
            };

            var startMessage = $"SmartCleanup {(mode == CleanupMode.DryRun ? "dry-run" : "cleanup")} started ({trigger}). Tracking {_activeJob.CandidateIds.Count} candidate(s).";
            if (manualRequest && requester != null)
            {
                Reply(requester, startMessage);
            }
            else if (_runtime.AnnounceScheduledCleanup)
            {
                Broadcast(startMessage);
            }

            ProcessCleanupBatch();
        }

        private void ProcessCleanupBatch()
        {
            if (_activeJob == null)
            {
                _runInProgress = false;
                return;
            }

            var processed = 0;
            while (processed < _runtime.MaxCandidatesPerTick && _activeJob.Index < _activeJob.CandidateIds.Count)
            {
                var id = _activeJob.CandidateIds[_activeJob.Index++];
                processed++;
                EvaluateCandidate(id, _activeJob);
            }

            if (_activeJob.Index >= _activeJob.CandidateIds.Count)
            {
                FinishCleanupJob();
                return;
            }

            NextTick(ProcessCleanupBatch);
        }

        private void FinishCleanupJob()
        {
            var job = _activeJob;
            _activeJob = null;
            _runInProgress = false;

            var elapsedSeconds = Math.Max(0d, UtcNow() - job.StartedUtc);
            var matchCount = job.BuildingMatches + job.DeployableMatches;
            var protectedBreakdown = $"Protected={job.SkippedProtected} [Event={job.SkippedProtectedByEvent}, Connected={job.SkippedProtectedByConnectedStructure}, Category={job.SkippedProtectedByCategory}, Ownerless={job.SkippedProtectedOwnerless}, Recent={job.SkippedProtectedByRecentActivity}, InsidePrivilege={job.SkippedProtectedInsidePrivilege}, CupboardAuth={job.SkippedProtectedByCupboardAuth}, OutsideDisabled={job.SkippedProtectedOutsideDisabled}]";
            var actionSegment = job.Mode == CleanupMode.DryRun
                ? $"WouldRemove={matchCount}, Removed=0"
                : $"Matches={matchCount}, Removed={job.Removed}";
            var summary = $"SmartCleanup {(job.Mode == CleanupMode.DryRun ? "dry-run" : "run")} finished. " +
                          $"Evaluated={job.Evaluated}, {actionSegment}, " +
                          $"Buildings={job.BuildingMatches}, Deployables={job.DeployableMatches}, " +
                          $"{protectedBreakdown}, TooSoon={job.SkippedInactiveTooSoon}, HealthTooHigh={job.SkippedHealthTooHigh}, " +
                          $"Whitelisted={job.SkippedWhitelisted}, Missing={job.SkippedMissing}, Elapsed={elapsedSeconds:0.##}s.";

            if (ShouldLogSummary(job))
            {
                Puts(summary);
            }

            if (job.ManualRequest && !string.IsNullOrEmpty(job.RequesterId))
            {
                var requester = players.FindPlayerById(job.RequesterId);
                if (requester != null)
                {
                    Reply(requester, summary);
                    if (job.Mode == CleanupMode.DryRun)
                    {
                        _pendingRunConfirmations[job.RequesterId] = UtcNow() + RunConfirmationLifetimeSeconds;
                        Reply(requester, "Dry-run reviewed. To execute this result, use /smartcleanup run confirm within 120 seconds.");
                    }
                }
            }
            else if (_runtime.AnnounceScheduledCleanup && (job.Mode == CleanupMode.DryRun || job.Removed > 0 || _config.AdvancedLogging.DebugLogging))
            {
                Broadcast(summary);
            }
            else if (_config.AdvancedLogging.NotifyAdminsOnScheduledCleanup)
            {
                NotifyAdmins(summary);
            }
        }

        private bool ShouldLogSummary(CleanupJob job)
        {
            if (job == null)
            {
                return false;
            }

            if (job.ManualRequest)
            {
                return true;
            }

            if (_config.AdvancedLogging.DebugLogging)
            {
                return true;
            }

            if (!_config.AdvancedLogging.LogScheduledCleanupSummary)
            {
                return false;
            }

            if (_config.AdvancedLogging.LogScheduledSummariesOnlyWhenRemovalsOccur)
            {
                return job.Removed > 0;
            }

            return true;
        }

        private void EvaluateCandidate(uint id, CleanupJob job)
        {
            TrackedEntity tracked;
            if (!_tracked.TryGetValue(id, out tracked))
            {
                job.SkippedMissing++;
                return;
            }

            var entity = BaseNetworkable.serverEntities.Find(new NetworkableId(id)) as BaseEntity;
            if (entity == null || entity.IsDestroyed)
            {
                _tracked.Remove(id);
                job.SkippedMissing++;
                return;
            }

            if (entity.OwnerID == 0UL || entity.parentEntity.IsSet())
            {
                _tracked.Remove(id);
                job.SkippedMissing++;
                return;
            }

            tracked.LastRefreshUtc = UtcNow();
            job.Evaluated++;

            if (MatchesPrefabList(_whitelist, entity, tracked) || MatchesPrefabList(_neverCleanupPrefabs, entity, tracked))
            {
                job.SkippedWhitelisted++;
                return;
            }

            var evaluation = ShouldCleanup(entity, tracked, job);
            if (!evaluation.ShouldRemove)
            {
                switch (evaluation.Reason)
                {
                    case SkipReason.Protected:
                        job.SkippedProtected++;
                        break;
                    case SkipReason.ProtectedByCategory:
                        job.SkippedProtected++;
                        job.SkippedProtectedByCategory++;
                        break;
                    case SkipReason.ProtectedOwnerless:
                        job.SkippedProtected++;
                        job.SkippedProtectedOwnerless++;
                        break;
                    case SkipReason.ProtectedByRecentActivity:
                        job.SkippedProtected++;
                        job.SkippedProtectedByRecentActivity++;
                        break;
                    case SkipReason.ProtectedInsidePrivilege:
                        job.SkippedProtected++;
                        job.SkippedProtectedInsidePrivilege++;
                        break;
                    case SkipReason.ProtectedByCupboardAuth:
                        job.SkippedProtected++;
                        job.SkippedProtectedByCupboardAuth++;
                        break;
                    case SkipReason.ProtectedOutsideDisabled:
                        job.SkippedProtected++;
                        job.SkippedProtectedOutsideDisabled++;
                        break;
                    case SkipReason.ProtectedByEvent:
                        job.SkippedProtected++;
                        job.SkippedProtectedByEvent++;
                        break;
                    case SkipReason.ProtectedByConnectedStructure:
                        job.SkippedProtected++;
                        job.SkippedProtectedByConnectedStructure++;
                        break;
                    case SkipReason.TooSoon:
                        job.SkippedInactiveTooSoon++;
                        break;
                    case SkipReason.HealthTooHigh:
                        job.SkippedHealthTooHigh++;
                        break;
                }
                return;
            }

            if (tracked.Kind == EntityKind.Building)
            {
                job.BuildingMatches++;
            }
            else if (tracked.Kind == EntityKind.Deployable)
            {
                job.DeployableMatches++;
            }

            if (job.Mode == CleanupMode.Execute)
            {
                entity.Kill(BaseNetworkable.DestroyMode.Gib);
                _tracked.Remove(id);
                job.Removed++;
            }
        }

        private enum SkipReason
        {
            None,
            Protected,
            ProtectedByCategory,
            ProtectedOwnerless,
            ProtectedByRecentActivity,
            ProtectedInsidePrivilege,
            ProtectedByCupboardAuth,
            ProtectedOutsideDisabled,
            ProtectedByEvent,
            ProtectedByConnectedStructure,
            TooSoon,
            HealthTooHigh
        }

        private struct EvaluationResult
        {
            public bool ShouldRemove;
            public SkipReason Reason;
        }

        private EvaluationResult ShouldCleanup(BaseEntity entity, TrackedEntity tracked, CleanupJob job)
        {
            var result = new EvaluationResult { ShouldRemove = false, Reason = SkipReason.None };

            if (IsProtectedEventEntity(entity, tracked))
            {
                result.Reason = SkipReason.ProtectedByEvent;
                return result;
            }

            if (tracked.Kind == EntityKind.Building && IsConnectedStructureProtected(entity as BuildingBlock, job))
            {
                result.Reason = SkipReason.ProtectedByConnectedStructure;
                return result;
            }

            if (tracked.Kind == EntityKind.Building && !_runtime.CleanupBuildings)
            {
                return result;
            }

            if (tracked.Kind == EntityKind.Deployable)
            {
                if (!_runtime.CleanupDeployables)
                {
                    return result;
                }

                var forceAllow = MatchesPrefabList(_alwaysCleanupPrefabs, entity, tracked);
                if (!forceAllow && !IsDeployableCategoryEnabled(tracked.DeployableCategory))
                {
                    result.Reason = SkipReason.ProtectedByCategory;
                    return result;
                }
            }

            if (entity.OwnerID == 0UL)
            {
                result.Reason = SkipReason.ProtectedOwnerless;
                return result;
            }

            if (IsProtectedByRecentActivity(entity.OwnerID))
            {
                result.Reason = SkipReason.ProtectedByRecentActivity;
                return result;
            }

            var privilege = entity.GetBuildingPrivilege();
            var hasPrivilege = privilege != null;
            var now = UtcNow();
            var lastEntityActivity = Math.Max(tracked.FirstSeenUtc, tracked.LastActivityUtc);
            var ageHours = Math.Max(0d, (now - lastEntityActivity) / 3600d);
            var healthFraction = GetHealthFraction(entity);

            if (hasPrivilege)
            {
                if (!_runtime.RemoveInsidePrivilege)
                {
                    result.Reason = SkipReason.ProtectedInsidePrivilege;
                    return result;
                }

                if (_runtime.CheckCupboardAuthorization && privilege.IsAuthed(entity.OwnerID))
                {
                    result.Reason = SkipReason.ProtectedByCupboardAuth;
                    return result;
                }

                if (_runtime.InsidePrivilegeCleanupHours > 0d && ageHours < _runtime.InsidePrivilegeCleanupHours)
                {
                    result.Reason = SkipReason.TooSoon;
                    return result;
                }

                if (_runtime.InsideHealthFractionThreshold > 0f && healthFraction > _runtime.InsideHealthFractionThreshold)
                {
                    result.Reason = SkipReason.HealthTooHigh;
                    return result;
                }

                result.ShouldRemove = true;
                return result;
            }

            if (!_runtime.RemoveOutsidePrivilege)
            {
                result.Reason = SkipReason.ProtectedOutsideDisabled;
                return result;
            }

            var requiredHours = tracked.Kind == EntityKind.Building
                ? _runtime.DisconnectedStructuresCleanupHours
                : _runtime.DeployablesOutsidePrivilegeCleanupHours;

            if (requiredHours > 0d && ageHours < requiredHours)
            {
                result.Reason = SkipReason.TooSoon;
                return result;
            }

            if (_runtime.OutsideHealthFractionThreshold > 0f && healthFraction > _runtime.OutsideHealthFractionThreshold)
            {
                result.Reason = SkipReason.HealthTooHigh;
                return result;
            }

            result.ShouldRemove = true;
            return result;
        }

        #endregion

        #region Runtime Resolution / Auto Tune

        private void ResolveRuntimeSettings(bool writeToConfig, bool logResolution)
        {
            var resolvedProfile = ResolveProfile();
            var settings = GetProfileDefaults(resolvedProfile);
            ApplyOverrides(settings);
            _runtime = settings;

            if (writeToConfig)
            {
                if (_config.ServerProfile == 1 && _config.AutoTuneWriteToConfig)
                {
                    _config.ServerProfile = resolvedProfile;
                    PrintWarning($"AutoTuneWriteToConfig selected and saved profile {resolvedProfile} ({settings.ResolvedProfileName}). Auto-detection is now pinned until ServerProfile is set back to 1.");
                }

                SaveConfig();
            }

            if (logResolution)
            {
                Puts($"Active profile: {_runtime.ResolvedProfileName} ({_runtime.ResolvedProfile}). " +
                     $"Buildings={_runtime.CleanupBuildings}, Deployables={_runtime.CleanupDeployables}, " +
                     $"OutsideHours(Buildings/Deployables)={_runtime.DisconnectedStructuresCleanupHours:0.##}/{_runtime.DeployablesOutsidePrivilegeCleanupHours:0.##}, " +
                     $"InsideHours={_runtime.InsidePrivilegeCleanupHours:0.##}, Batch={_runtime.MaxCandidatesPerTick}, Interval={_runtime.ScheduledEvaluationIntervalMinutes:0.##}m.");
            }
        }

        private int ResolveProfile()
        {
            var configured = Mathf.Clamp(_config.ServerProfile, 1, 5);
            if (configured != 1 || !_config.AutoTuneEnabled)
            {
                return configured;
            }

            var score = 0;

            var decayScale = SafeDecayScale();
            var decayTick = SafeDecayTick();
            var cleanupInterval = SafeCleanupInterval();
            var itemDespawn = SafeItemDespawn();

            if (decayScale > 0f && decayScale < 0.65f)
            {
                score += 2;
            }
            else if (decayScale >= 0.65f && decayScale <= 0.9f)
            {
                score += 1;
            }

            if (decayTick >= 1200f)
            {
                score += 2;
            }
            else if (decayTick >= 600f)
            {
                score += 1;
            }

            if (cleanupInterval >= 1800f)
            {
                score += 2;
            }
            else if (cleanupInterval >= 900f)
            {
                score += 1;
            }

            if (itemDespawn >= 600f)
            {
                score += 1;
            }

            var entityCount = BaseNetworkable.serverEntities?.Count ?? 0;
            if (entityCount >= 250000)
            {
                score += 2;
            }
            else if (entityCount >= 150000)
            {
                score += 1;
            }

            var onlineCount = BasePlayer.activePlayerList?.Count ?? 0;
            if (onlineCount >= 120)
            {
                score += 2;
            }
            else if (onlineCount >= 60)
            {
                score += 1;
            }

            if (score >= 6)
            {
                return 2; // FastPvP-like
            }

            if (score >= 3)
            {
                return 3; // Balanced
            }

            return 4; // PvE conservative fallback
        }

        private RuntimeSettings GetProfileDefaults(int profile)
        {
            switch (profile)
            {
                case 2:
                    return new RuntimeSettings
                    {
                        ResolvedProfile = 2,
                        ResolvedProfileName = "FastPvP",
                        ScheduledEvaluationIntervalMinutes = 20d,
                        MaxCandidatesPerTick = 150,
                        DeployablesOutsidePrivilegeCleanupHours = 24d,
                        DisconnectedStructuresCleanupHours = 48d,
                        InsidePrivilegeCleanupHours = 0d,
                        OutsideHealthFractionThreshold = 0.75f,
                        InsideHealthFractionThreshold = 0f,
                        ProtectRecentlyActivePlayersHours = 24d,
                        CleanupBuildings = true,
                        CleanupDeployables = true,
                        RemoveInsidePrivilege = false,
                        RemoveOutsidePrivilege = true,
                        CheckCupboardAuthorization = true,
                        AnnounceScheduledCleanup = false,
                        AutoTuneEnabled = _config.AutoTuneEnabled
                    };

                case 3:
                    return new RuntimeSettings
                    {
                        ResolvedProfile = 3,
                        ResolvedProfileName = "Balanced",
                        ScheduledEvaluationIntervalMinutes = 30d,
                        MaxCandidatesPerTick = 100,
                        DeployablesOutsidePrivilegeCleanupHours = 48d,
                        DisconnectedStructuresCleanupHours = 72d,
                        InsidePrivilegeCleanupHours = 0d,
                        OutsideHealthFractionThreshold = 0.6f,
                        InsideHealthFractionThreshold = 0f,
                        ProtectRecentlyActivePlayersHours = 72d,
                        CleanupBuildings = true,
                        CleanupDeployables = true,
                        RemoveInsidePrivilege = false,
                        RemoveOutsidePrivilege = true,
                        CheckCupboardAuthorization = true,
                        AnnounceScheduledCleanup = false,
                        AutoTuneEnabled = _config.AutoTuneEnabled
                    };

                case 4:
                    return new RuntimeSettings
                    {
                        ResolvedProfile = 4,
                        ResolvedProfileName = "PvEConservative",
                        ScheduledEvaluationIntervalMinutes = 45d,
                        MaxCandidatesPerTick = 60,
                        DeployablesOutsidePrivilegeCleanupHours = 96d,
                        DisconnectedStructuresCleanupHours = 168d,
                        InsidePrivilegeCleanupHours = 0d,
                        OutsideHealthFractionThreshold = 0.35f,
                        InsideHealthFractionThreshold = 0f,
                        ProtectRecentlyActivePlayersHours = 168d,
                        CleanupBuildings = true,
                        CleanupDeployables = true,
                        RemoveInsidePrivilege = false,
                        RemoveOutsidePrivilege = true,
                        CheckCupboardAuthorization = true,
                        AnnounceScheduledCleanup = false,
                        AutoTuneEnabled = _config.AutoTuneEnabled
                    };

                case 5:
                    return new RuntimeSettings
                    {
                        ResolvedProfile = 5,
                        ResolvedProfileName = "Custom",
                        ScheduledEvaluationIntervalMinutes = _config.AdvancedTimingOverrides.EnableAdvancedTimingOverrides && _config.AdvancedTimingOverrides.ScheduledEvaluationIntervalMinutesOverride > 0d
                            ? _config.AdvancedTimingOverrides.ScheduledEvaluationIntervalMinutesOverride
                            : 30d,
                        MaxCandidatesPerTick = _config.AdvancedPerformance.MaxCandidatesPerTick,
                        DeployablesOutsidePrivilegeCleanupHours = _config.AdvancedTimingOverrides.EnableAdvancedTimingOverrides && _config.AdvancedTimingOverrides.DeployablesOutsideTCleanupHoursOverride > 0d
                            ? _config.AdvancedTimingOverrides.DeployablesOutsideTCleanupHoursOverride
                            : 48d,
                        DisconnectedStructuresCleanupHours = _config.AdvancedTimingOverrides.EnableAdvancedTimingOverrides && _config.AdvancedTimingOverrides.DisconnectedStructuresCleanupHoursOverride > 0d
                            ? _config.AdvancedTimingOverrides.DisconnectedStructuresCleanupHoursOverride
                            : 72d,
                        InsidePrivilegeCleanupHours = _config.AdvancedTimingOverrides.EnableAdvancedTimingOverrides && _config.AdvancedTimingOverrides.InsidePrivilegeCleanupHoursOverride > 0d
                            ? _config.AdvancedTimingOverrides.InsidePrivilegeCleanupHoursOverride
                            : 0d,
                        OutsideHealthFractionThreshold = _config.AdvancedThresholds.OutsidePrivilegeHealthFractionThreshold,
                        InsideHealthFractionThreshold = _config.AdvancedThresholds.InsidePrivilegeHealthFractionThreshold,
                        ProtectRecentlyActivePlayersHours = _config.AdvancedSafety.ProtectRecentlyActivePlayersHours,
                        CleanupBuildings = _config.CategorySettings.CleanupBuildings,
                        CleanupDeployables = _config.CategorySettings.CleanupDeployables,
                        RemoveInsidePrivilege = _config.AdvancedSafety.AllowInsidePrivilegeCleanup,
                        RemoveOutsidePrivilege = _config.AdvancedSafety.AllowOutsidePrivilegeCleanup,
                        CheckCupboardAuthorization = _config.AdvancedSafety.CheckCupboardAuthorization,
                        AnnounceScheduledCleanup = _config.AdvancedLogging.AnnounceScheduledCleanup,
                        AutoTuneEnabled = _config.AutoTuneEnabled
                    };

                default:
                    // Auto resolves to one of the real profiles before this method is called.
                    return GetProfileDefaults(3);
            }
        }

        private void ApplyOverrides(RuntimeSettings settings)
        {
            if (settings == null)
            {
                return;
            }

            settings.AnnounceScheduledCleanup = _config.AdvancedLogging.AnnounceScheduledCleanup;
            settings.CleanupBuildings = _config.CategorySettings.CleanupBuildings;
            settings.CleanupDeployables = _config.CategorySettings.CleanupDeployables;
            settings.RemoveInsidePrivilege = _config.AdvancedSafety.AllowInsidePrivilegeCleanup;
            settings.RemoveOutsidePrivilege = _config.AdvancedSafety.AllowOutsidePrivilegeCleanup;
            settings.CheckCupboardAuthorization = _config.AdvancedSafety.CheckCupboardAuthorization;

            if (settings.ResolvedProfile == 5 || _config.AdvancedPerformance.OverrideProfileBatchSize)
            {
                settings.MaxCandidatesPerTick = Mathf.Clamp(_config.AdvancedPerformance.MaxCandidatesPerTick, 10, 1000);
            }

            if (settings.ResolvedProfile == 5 || _config.AdvancedSafety.OverrideProfileRecentActivityProtection)
            {
                settings.ProtectRecentlyActivePlayersHours = Math.Max(0d, _config.AdvancedSafety.ProtectRecentlyActivePlayersHours);
            }

            if (settings.ResolvedProfile == 5 || _config.AdvancedThresholds.OverrideProfileHealthThresholds)
            {
                settings.OutsideHealthFractionThreshold = Mathf.Clamp(_config.AdvancedThresholds.OutsidePrivilegeHealthFractionThreshold, 0f, 1f);
                settings.InsideHealthFractionThreshold = Mathf.Clamp(_config.AdvancedThresholds.InsidePrivilegeHealthFractionThreshold, 0f, 1f);
            }

            if (!_config.AdvancedTimingOverrides.EnableAdvancedTimingOverrides)
            {
                return;
            }

            if (_config.AdvancedTimingOverrides.DeployablesOutsideTCleanupHoursOverride > 0d)
            {
                settings.DeployablesOutsidePrivilegeCleanupHours = _config.AdvancedTimingOverrides.DeployablesOutsideTCleanupHoursOverride;
            }

            if (_config.AdvancedTimingOverrides.DisconnectedStructuresCleanupHoursOverride > 0d)
            {
                settings.DisconnectedStructuresCleanupHours = _config.AdvancedTimingOverrides.DisconnectedStructuresCleanupHoursOverride;
            }

            if (_config.AdvancedTimingOverrides.InsidePrivilegeCleanupHoursOverride > 0d)
            {
                settings.InsidePrivilegeCleanupHours = _config.AdvancedTimingOverrides.InsidePrivilegeCleanupHoursOverride;
            }

            if (_config.AdvancedTimingOverrides.ScheduledEvaluationIntervalMinutesOverride > 0d)
            {
                settings.ScheduledEvaluationIntervalMinutes = _config.AdvancedTimingOverrides.ScheduledEvaluationIntervalMinutesOverride;
            }
        }

        #endregion

        #region Helpers

        private bool IsAuthorized(IPlayer player)
        {
            return player != null && (player.IsServer || player.HasPermission(PermAdmin));
        }

        private void MarkEntityActivity(BaseEntity entity)
        {
            if (entity?.net == null)
            {
                return;
            }

            TrackedEntity tracked;
            if (_tracked.TryGetValue((uint)entity.net.ID.Value, out tracked))
            {
                MarkEntityActivity(tracked, UtcNow());
            }
        }

        private void MarkEntityActivity(TrackedEntity tracked, double timestamp)
        {
            if (tracked == null)
            {
                return;
            }

            tracked.LastActivityUtc = timestamp;
            tracked.LastRefreshUtc = timestamp;
            StoreTrackedEntityState(tracked);
        }

        private long GetWipeCreatedUtcTicks()
        {
            try
            {
                return SaveRestore.SaveCreatedTime.ToUniversalTime().Ticks;
            }
            catch
            {
                return 0L;
            }
        }

        private void LoadStoredData()
        {
            try
            {
                _storedData = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(DataFileName) ?? new StoredData();
            }
            catch (Exception ex)
            {
                PrintWarning($"Could not read persistent state; starting a fresh state file. {ex.Message}");
                _storedData = new StoredData();
            }

            var wipeTicks = GetWipeCreatedUtcTicks();
            if (_storedData.WipeCreatedUtcTicks != 0L && wipeTicks != 0L && _storedData.WipeCreatedUtcTicks != wipeTicks)
            {
                Puts("Detected a new save; discarded cleanup activity state from the previous wipe.");
                _storedData = new StoredData();
            }

            _storedData.WipeCreatedUtcTicks = wipeTicks;
            _storedData.LastOwnerSeenUtc = _storedData.LastOwnerSeenUtc ?? new Dictionary<ulong, double>();
            _storedData.EntityStates = _storedData.EntityStates ?? new Dictionary<uint, PersistedEntityState>();
            _storedData.TemporaryProtectedUntilUtc = _storedData.TemporaryProtectedUntilUtc ?? new Dictionary<uint, double>();

            _lastOwnerSeenUtc.Clear();
            foreach (var pair in _storedData.LastOwnerSeenUtc)
            {
                _lastOwnerSeenUtc[pair.Key] = pair.Value;
            }

            _temporaryProtectedUntilUtc.Clear();
            var now = UtcNow();
            foreach (var pair in _storedData.TemporaryProtectedUntilUtc)
            {
                if (pair.Value > now)
                {
                    _temporaryProtectedUntilUtc[pair.Key] = pair.Value;
                }
            }

            _dataDirty = false;
        }

        private void MarkDataDirty()
        {
            _dataDirty = true;
        }

        private void StoreTrackedEntityState(TrackedEntity tracked)
        {
            if (tracked == null)
            {
                return;
            }

            if (_storedData == null)
            {
                _storedData = new StoredData { WipeCreatedUtcTicks = GetWipeCreatedUtcTicks() };
            }

            _storedData.EntityStates[tracked.NetId] = new PersistedEntityState
            {
                FirstSeenUtc = tracked.FirstSeenUtc,
                LastActivityUtc = tracked.LastActivityUtc
            };
            MarkDataDirty();
        }

        private void PruneStoredEntityStates()
        {
            if (_storedData?.EntityStates == null)
            {
                return;
            }

            var activeIds = new HashSet<uint>(_tracked.Keys);
            foreach (var id in _storedData.EntityStates.Keys.Where(id => !activeIds.Contains(id)).ToList())
            {
                _storedData.EntityStates.Remove(id);
                MarkDataDirty();
            }
        }

        private void StartDataSaveTimer()
        {
            _dataSaveTimer?.Destroy();
            var seconds = (float)(Math.Max(1d, _config.AdvancedPerformance.DataSaveIntervalMinutes) * 60d);
            _dataSaveTimer = timer.Every(seconds, () => SaveStoredData(force: false));
        }

        private void SaveStoredData(bool force)
        {
            if (_storedData == null || (!force && !_dataDirty))
            {
                return;
            }

            _storedData.WipeCreatedUtcTicks = GetWipeCreatedUtcTicks();
            _storedData.LastOwnerSeenUtc = new Dictionary<ulong, double>(_lastOwnerSeenUtc);
            _storedData.TemporaryProtectedUntilUtc = new Dictionary<uint, double>(_temporaryProtectedUntilUtc);

            try
            {
                Interface.Oxide.DataFileSystem.WriteObject(DataFileName, _storedData);
                _dataDirty = false;
            }
            catch (Exception ex)
            {
                PrintError($"Could not save persistent state: {ex.Message}");
            }
        }

        private void StartReconciliationTimer()
        {
            _reconciliationTimer?.Destroy();
            if (_config.AdvancedPerformance.ReconciliationIntervalHours <= 0d)
            {
                return;
            }

            var seconds = (float)(_config.AdvancedPerformance.ReconciliationIntervalHours * 3600d);
            _reconciliationTimer = timer.Every(seconds, () =>
            {
                if (!_runInProgress && !_rebuildInProgress)
                {
                    StartTrackingIndexRebuild(manualRequest: false, requester: null);
                }
            });
        }

        private bool IsInsideMonument(Vector3 position)
        {
            if (!_config.EventSafety.Enabled || !_config.EventSafety.ProtectEntitiesInsideMonuments || TerrainMeta.Path?.Monuments == null)
            {
                return false;
            }

            foreach (var monument in TerrainMeta.Path.Monuments)
            {
                if (monument != null && monument.IsInBounds(position))
                {
                    return true;
                }
            }

            return false;
        }

        private bool IsProtectedEventEntity(BaseEntity entity, TrackedEntity tracked)
        {
            if (!_config.EventSafety.Enabled || entity == null)
            {
                return false;
            }

            var id = entity.net == null ? 0u : (uint)entity.net.ID.Value;
            if (id != 0u && (_airfieldEventEntities.Contains(id) || _registeredProtectedEntities.Contains(id)))
            {
                return true;
            }

            double temporaryExpiry;
            if (id != 0u && _temporaryProtectedUntilUtc.TryGetValue(id, out temporaryExpiry))
            {
                if (temporaryExpiry > UtcNow())
                {
                    return true;
                }

                _temporaryProtectedUntilUtc.Remove(id);
                MarkDataDirty();
            }

            if (_config.EventSafety.ProtectUnsavedEntities && !entity.enableSaving)
            {
                return true;
            }

            if (_config.EventSafety.ProtectNonSteamOwnedEntities && entity.OwnerID != 0UL && entity.OwnerID < 76561197960265728UL)
            {
                return true;
            }

            if (_config.EventSafety.ProtectedSkinIds != null && _config.EventSafety.ProtectedSkinIds.Contains(entity.skinID))
            {
                return true;
            }

            if (_config.EventSafety.ProtectEntitiesInsideMonuments && (tracked?.InsideMonument == true || IsInsideMonument(entity.transform.position)))
            {
                return true;
            }

            if (_config.EventSafety.UseKnownPluginIntegrations)
            {
                if (MonumentAddons != null && ConvertToBool(MonumentAddons.Call("API_IsMonumentEntity", entity)))
                {
                    return true;
                }

                if (RaidableBases != null && ConvertToBool(RaidableBases.Call("HasEventEntity", entity)))
                {
                    return true;
                }
            }

            var hookResult = Interface.CallHook("CanSmartCleanupEntity", entity);
            return hookResult is bool && !(bool)hookResult;
        }

        private bool ConvertToBool(object value)
        {
            if (value is bool)
            {
                return (bool)value;
            }

            if (value == null)
            {
                return false;
            }

            bool result;
            return bool.TryParse(value.ToString(), out result) && result;
        }

        private bool IsConnectedStructureProtected(BuildingBlock block, CleanupJob job)
        {
            if (!_config.AdvancedSafety.ProtectEntireConnectedStructure || block == null || block.buildingID == 0u)
            {
                return false;
            }

            bool cached;
            if (job != null && job.ConnectedStructureProtection.TryGetValue(block.buildingID, out cached))
            {
                return cached;
            }

            var protectedStructure = false;
            var building = block.GetBuilding();
            if (building != null)
            {
                if (!_runtime.RemoveInsidePrivilege && building.buildingPrivileges != null && building.buildingPrivileges.Any(privilege => privilege != null && !privilege.IsDestroyed))
                {
                    protectedStructure = true;
                }

                if (!protectedStructure && building.buildingBlocks != null)
                {
                    foreach (var buildingBlock in building.buildingBlocks)
                    {
                        if (buildingBlock == null || buildingBlock.IsDestroyed)
                        {
                            continue;
                        }

                        if (IsProtectedByRecentActivity(buildingBlock.OwnerID))
                        {
                            protectedStructure = true;
                            break;
                        }

                        if (_runtime.CheckCupboardAuthorization && building.buildingPrivileges != null && building.buildingPrivileges.Any(privilege => privilege != null && !privilege.IsDestroyed && privilege.IsAuthed(buildingBlock.OwnerID)))
                        {
                            protectedStructure = true;
                            break;
                        }
                    }
                }
            }

            if (job != null)
            {
                job.ConnectedStructureProtection[block.buildingID] = protectedStructure;
            }

            return protectedStructure;
        }

        [HookMethod("API_ProtectEntity")]
        public bool API_ProtectEntity(BaseEntity entity, string source = null)
        {
            if (entity?.net == null || entity.IsDestroyed)
            {
                return false;
            }

            _registeredProtectedEntities.Add((uint)entity.net.ID.Value);
            return true;
        }

        [HookMethod("API_UnprotectEntity")]
        public bool API_UnprotectEntity(BaseEntity entity, string source = null)
        {
            return entity?.net != null && _registeredProtectedEntities.Remove((uint)entity.net.ID.Value);
        }

        private void RegisterRaidableBasePayload(object payload)
        {
            var values = payload as object[];
            if (values == null || values.Length <= 11)
            {
                return;
            }

            var entities = values[11] as IEnumerable<BaseEntity>;
            if (entities == null)
            {
                return;
            }

            foreach (var entity in entities)
            {
                API_ProtectEntity(entity, "RaidableBases");
            }
        }

        private void UnregisterRaidableBasePayload(object payload)
        {
            var values = payload as object[];
            if (values == null || values.Length <= 11)
            {
                return;
            }

            var entities = values[11] as IEnumerable<BaseEntity>;
            if (entities == null)
            {
                return;
            }

            foreach (var entity in entities)
            {
                API_UnprotectEntity(entity, "RaidableBases");
            }
        }

        private bool IsProtectedByRecentActivity(ulong ownerId)
        {
            if (ownerId == 0UL)
            {
                return false;
            }

            var protectWindowSeconds = Math.Max(0d, _runtime.ProtectRecentlyActivePlayersHours) * 3600d;

            BasePlayer ownerPlayer = BasePlayer.FindByID(ownerId) ?? BasePlayer.FindSleeping(ownerId);
            if (ownerPlayer != null && ownerPlayer.IsConnected)
            {
                _lastOwnerSeenUtc[ownerId] = UtcNow();
                return protectWindowSeconds > 0d;
            }

            double lastSeen;
            if (!_lastOwnerSeenUtc.TryGetValue(ownerId, out lastSeen))
            {
                return false;
            }

            return protectWindowSeconds > 0d && (UtcNow() - lastSeen) <= protectWindowSeconds;
        }

        private float GetHealthFraction(BaseEntity entity)
        {
            if (entity == null)
            {
                return 1f;
            }

            var maxHealth = entity.MaxHealth();
            if (maxHealth <= 0f)
            {
                return 1f;
            }

            return Mathf.Clamp01(entity.Health() / maxHealth);
        }

        private string BuildStatusText()
        {
            var totalTracked = _tracked.Count;
            var trackedBuildings = _tracked.Values.Count(x => x.Kind == EntityKind.Building);
            var trackedDeployables = _tracked.Values.Count(x => x.Kind == EntityKind.Deployable);

            return $"SmartCleanup status => Profile={_runtime.ResolvedProfileName} ({_runtime.ResolvedProfile}), " +
                   $"Tracked={totalTracked}, Buildings={trackedBuildings}, Deployables={trackedDeployables}, " +
                   $"Interval={_runtime.ScheduledEvaluationIntervalMinutes:0.##}m, Batch={_runtime.MaxCandidatesPerTick}, " +
                   $"OutsideHours(Buildings/Deployables)={_runtime.DisconnectedStructuresCleanupHours:0.##}/{_runtime.DeployablesOutsidePrivilegeCleanupHours:0.##}, " +
                   $"ProtectRecentOwners={_runtime.ProtectRecentlyActivePlayersHours:0.##}h, Events={_config.EventSafety.Enabled}, " +
                   $"ScheduledDisabled={_config.AdvancedTesting.DisableScheduledCleanupForTesting || _configLoadFailed}, " +
                   $"Running={_runInProgress}, Rebuilding={_rebuildInProgress}.";
        }

        private void Broadcast(string message)
        {
            foreach (var player in players.Connected)
            {
                player.Message($"[SmartCleanup] {message}");
            }
        }

        private void Reply(IPlayer player, string message)
        {
            if (player == null)
            {
                Puts(message);
                return;
            }

            player.Reply($"[SmartCleanup] {message}");
        }

        private double UtcNow()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        private float SafeDecayScale()
        {
            try { return ConVar.Decay.scale; } catch { return 1f; }
        }

        private float SafeDecayTick()
        {
            try { return ConVar.Decay.tick; } catch { return 600f; }
        }

        private float SafeCleanupInterval()
        {
            return ReadConVarFloat(typeof(ConVar.Server), 600f, "cleanupinterval", "cleanup_interval");
        }

        private float SafeItemDespawn()
        {
            return ReadConVarFloat(typeof(ConVar.Server), 300f, "itemdespawn", "item_despawn");
        }

        private float ReadConVarFloat(Type type, float fallback, params string[] memberNames)
        {
            try
            {
                foreach (var memberName in memberNames)
                {
                    var field = type.GetField(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
                    if (field != null)
                    {
                        return ConvertToFloat(field.GetValue(null), fallback);
                    }

                    var property = type.GetProperty(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
                    if (property != null)
                    {
                        return ConvertToFloat(property.GetValue(null, null), fallback);
                    }
                }
            }
            catch
            {
            }

            return fallback;
        }

        private float ConvertToFloat(object value, float fallback)
        {
            if (value == null)
            {
                return fallback;
            }

            try
            {
                return Convert.ToSingle(value);
            }
            catch
            {
                return fallback;
            }
        }

        #endregion

        #region Configuration

        protected override void LoadDefaultConfig()
        {
            _config = ConfigData.CreateDefault();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();

            var migrated = false;

            try
            {
                _config = Config.ReadObject<ConfigData>();
                if (_config == null)
                {
                    throw new Exception("Config file was empty.");
                }
            }
            catch (Exception ex)
            {
                _configLoadFailed = true;
                BackupInvalidConfig();
                PrintError($"Failed to read config. The invalid file was backed up and safe testing defaults were generated. Error: {ex.Message}");
                LoadDefaultConfig();
                _config.AdvancedTesting.DisableScheduledCleanupForTesting = true;
                migrated = true;
            }

            if (ApplyConfigDefaultsAndMigrations())
            {
                migrated = true;
            }

            if (migrated)
            {
                PrintWarning($"SmartCleanup configuration was updated to version {_config.ConfigVersion}.");
            }

            SaveConfig();
        }

        private void NotifyAdmins(string message)
        {
            foreach (var player in players.Connected)
            {
                if (player.BelongsToGroup("admin") || player.HasPermission(PermAdmin))
                {
                    player.Message($"[SmartCleanup] {message}");
                }
            }
        }

        private void BackupInvalidConfig()
        {
            try
            {
                var source = Path.Combine(Interface.Oxide.ConfigDirectory, $"{Name}.json");
                if (!File.Exists(source))
                {
                    return;
                }

                var backup = Path.Combine(Interface.Oxide.ConfigDirectory, $"{Name}.invalid-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json.bak");
                File.Copy(source, backup, false);
                PrintWarning($"Backed up the unreadable configuration to {Path.GetFileName(backup)}.");
            }
            catch (Exception ex)
            {
                PrintError($"Could not back up the unreadable configuration: {ex.Message}");
            }
        }

        private bool ApplyConfigDefaultsAndMigrations()
        {
            var changed = false;
            var defaults = ConfigData.CreateDefault();

            if (_config == null)
            {
                _config = defaults;
                return true;
            }

            var previousVersion = _config.ConfigVersion;

            if (_config.ConfigVersion < ConfigData.CurrentConfigVersion)
            {
                _config.ConfigVersion = ConfigData.CurrentConfigVersion;
                changed = true;
            }

            if (previousVersion > 0 && previousVersion < 3)
            {
                var preserveBuildings = _config.CategorySettings == null || _config.CategorySettings.CleanupBuildings;
                var preserveDeployables = _config.CategorySettings == null || _config.CategorySettings.CleanupDeployables;
                _config.CategorySettings = defaults.CategorySettings;
                _config.CategorySettings.CleanupBuildings = preserveBuildings;
                _config.CategorySettings.CleanupDeployables = preserveDeployables;
                changed = true;
            }

            if (_config.CategorySettings == null)
            {
                _config.CategorySettings = defaults.CategorySettings;
                changed = true;
            }

            if (_config.AdvancedTimingOverrides == null)
            {
                _config.AdvancedTimingOverrides = defaults.AdvancedTimingOverrides;
                changed = true;
            }

            if (_config.AdvancedSafety == null)
            {
                _config.AdvancedSafety = defaults.AdvancedSafety;
                changed = true;
            }

            if (_config.EventSafety == null)
            {
                _config.EventSafety = defaults.EventSafety;
                changed = true;
            }

            if (_config.AdvancedThresholds == null)
            {
                _config.AdvancedThresholds = defaults.AdvancedThresholds;
                changed = true;
            }

            if (_config.AdvancedPerformance == null)
            {
                _config.AdvancedPerformance = defaults.AdvancedPerformance;
                changed = true;
            }

            if (_config.AdvancedLogging == null)
            {
                _config.AdvancedLogging = defaults.AdvancedLogging;
                changed = true;
            }
            else
            {
                if (previousVersion > 0 && previousVersion < 4)
                {
                    _config.AdvancedLogging.DebugLogging = defaults.AdvancedLogging.DebugLogging;
                    _config.AdvancedLogging.LogScheduledSummariesOnlyWhenRemovalsOccur = defaults.AdvancedLogging.LogScheduledSummariesOnlyWhenRemovalsOccur;
                    changed = true;
                }

                if (previousVersion > 0 && previousVersion < 5)
                {
                    _config.AdvancedLogging.LogScheduledCleanupSummary = defaults.AdvancedLogging.LogScheduledCleanupSummary;
                    changed = true;
                }
            }

            if (_config.AdvancedTesting == null)
            {
                _config.AdvancedTesting = defaults.AdvancedTesting;
                changed = true;
            }

            if (_config.Whitelist == null)
            {
                _config.Whitelist = defaults.Whitelist;
                changed = true;
            }
            else
            {
                var distinctWhitelist = _config.Whitelist.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (distinctWhitelist.Length != _config.Whitelist.Length)
                {
                    _config.Whitelist = distinctWhitelist;
                    changed = true;
                }
            }

            if (_config.NeverCleanupPrefabs == null)
            {
                _config.NeverCleanupPrefabs = defaults.NeverCleanupPrefabs;
                changed = true;
            }
            else
            {
                var distinctNever = _config.NeverCleanupPrefabs.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (distinctNever.Length != _config.NeverCleanupPrefabs.Length)
                {
                    _config.NeverCleanupPrefabs = distinctNever;
                    changed = true;
                }
            }

            if (_config.AlwaysCleanupPrefabs == null)
            {
                _config.AlwaysCleanupPrefabs = defaults.AlwaysCleanupPrefabs;
                changed = true;
            }
            else
            {
                var alwaysList = _config.AlwaysCleanupPrefabs.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).ToList();
                if (previousVersion > 0 && previousVersion < 6)
                {
                    foreach (var prefab in defaults.AlwaysCleanupPrefabs)
                    {
                        if (!alwaysList.Contains(prefab, StringComparer.OrdinalIgnoreCase))
                        {
                            alwaysList.Add(prefab);
                            changed = true;
                        }
                    }
                }

                var distinctAlways = alwaysList.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (distinctAlways.Length != _config.AlwaysCleanupPrefabs.Length || !distinctAlways.SequenceEqual(_config.AlwaysCleanupPrefabs, StringComparer.OrdinalIgnoreCase))
                {
                    _config.AlwaysCleanupPrefabs = distinctAlways;
                    changed = true;
                }
            }

            if (_config.ServerProfile < 1 || _config.ServerProfile > 5)
            {
                _config.ServerProfile = defaults.ServerProfile;
                changed = true;
            }

            if (_config.AdvancedSafety.ProtectRecentlyActivePlayersHours < 0d)
            {
                _config.AdvancedSafety.ProtectRecentlyActivePlayersHours = defaults.AdvancedSafety.ProtectRecentlyActivePlayersHours;
                changed = true;
            }

            if (_config.AdvancedPerformance.MaxCandidatesPerTick <= 0)
            {
                _config.AdvancedPerformance.MaxCandidatesPerTick = defaults.AdvancedPerformance.MaxCandidatesPerTick;
                changed = true;
            }

            if (_config.AdvancedPerformance.IndexCandidatesPerTick <= 0)
            {
                _config.AdvancedPerformance.IndexCandidatesPerTick = defaults.AdvancedPerformance.IndexCandidatesPerTick;
                changed = true;
            }

            if (_config.AdvancedPerformance.DataSaveIntervalMinutes <= 0d)
            {
                _config.AdvancedPerformance.DataSaveIntervalMinutes = defaults.AdvancedPerformance.DataSaveIntervalMinutes;
                changed = true;
            }

            if (_config.AdvancedPerformance.ReconciliationIntervalHours < 0d)
            {
                _config.AdvancedPerformance.ReconciliationIntervalHours = defaults.AdvancedPerformance.ReconciliationIntervalHours;
                changed = true;
            }

            if (_config.EventSafety.CopyPasteProtectionHours < 0d)
            {
                _config.EventSafety.CopyPasteProtectionHours = defaults.EventSafety.CopyPasteProtectionHours;
                changed = true;
            }

            if (_config.EventSafety.ProtectedSkinIds == null)
            {
                _config.EventSafety.ProtectedSkinIds = defaults.EventSafety.ProtectedSkinIds;
                changed = true;
            }

            return changed;
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(_config, true);
        }

        private class ConfigData
        {
            public const int CurrentConfigVersion = 8;

            [JsonProperty("Config Version")]
            public int ConfigVersion = CurrentConfigVersion;

            [JsonProperty("ServerProfile (1=Auto Detect Server Environment, 2=FastPvP Servers, 3=Balanced Servers, 4=PvE/Conservative Servers, 5=Custom Manual Settings)")]
            public int ServerProfile = 1;

            [JsonProperty("AutoTune Enabled")]
            public bool AutoTuneEnabled = true;

            [JsonProperty("AutoTuneWriteToConfig")]
            public bool AutoTuneWriteToConfig = false;

            [JsonProperty("Category Settings")]
            public CategorySettings CategorySettings = new CategorySettings();

            [JsonProperty("Advanced Timing Overrides")]
            public AdvancedTimingOverrides AdvancedTimingOverrides = new AdvancedTimingOverrides();

            [JsonProperty("Advanced Safety")]
            public AdvancedSafety AdvancedSafety = new AdvancedSafety();

            [JsonProperty("Event Safety")]
            public EventSafety EventSafety = new EventSafety();

            [JsonProperty("Advanced Thresholds")]
            public AdvancedThresholds AdvancedThresholds = new AdvancedThresholds();

            [JsonProperty("Advanced Performance")]
            public AdvancedPerformance AdvancedPerformance = new AdvancedPerformance();

            [JsonProperty("Advanced Logging")]
            public AdvancedLogging AdvancedLogging = new AdvancedLogging();

            [JsonProperty("Advanced Testing")]
            public AdvancedTesting AdvancedTesting = new AdvancedTesting();

            [JsonProperty("Whitelist (ShortPrefabName or PrefabName)")]
            public string[] Whitelist =
            {
                "cupboard.tool.deployed",
                "cupboard.tool",
                "vendingmachine.deployed",
                "researchtable_deployed",
                "workbench1.deployed",
                "workbench2.deployed",
                "workbench3.deployed"
            };

            [JsonProperty("Never Cleanup These Prefabs (ShortPrefabName or PrefabName)")]
            public string[] NeverCleanupPrefabs =
            {
                "woodbox_deployed",
                "box.wooden.large",
                "coffin.storage",
                "small_stash_deployed"
            };

            [JsonProperty("Always Allow Cleanup For These Prefabs (ShortPrefabName or PrefabName)")]
            public string[] AlwaysCleanupPrefabs =
            {
                "campfire",
                "lantern.deployed",
                "bbq.deployed"
            };

            public static ConfigData CreateDefault()
            {
                return new ConfigData();
            }
        }

        private class CategorySettings
        {
            [JsonProperty("Cleanup Buildings")]
            public bool CleanupBuildings = true;

            [JsonProperty("Cleanup Deployables")]
            public bool CleanupDeployables = true;

            [JsonProperty("Cleanup Production Deployables")]
            public bool CleanupProductionDeployables = true;

            [JsonProperty("Cleanup Lighting Deployables")]
            public bool CleanupLightingDeployables = true;

            [JsonProperty("Cleanup Trap Deployables")]
            public bool CleanupTrapDeployables = true;

            [JsonProperty("Cleanup Utility Deployables")]
            public bool CleanupUtilityDeployables = false;

            [JsonProperty("Cleanup Water And Farming Deployables")]
            public bool CleanupWaterAndFarmingDeployables = false;

            [JsonProperty("Cleanup Electrical And Industrial Deployables")]
            public bool CleanupElectricalAndIndustrialDeployables = false;

            [JsonProperty("Cleanup Storage Deployables")]
            public bool CleanupStorageDeployables = false;

            [JsonProperty("Cleanup Workbench Deployables")]
            public bool CleanupWorkbenchDeployables = false;

            [JsonProperty("Cleanup Privilege Deployables")]
            public bool CleanupPrivilegeDeployables = false;

            [JsonProperty("Cleanup Commerce Deployables")]
            public bool CleanupCommerceDeployables = false;

        }

        private class AdvancedTimingOverrides
        {
            [JsonProperty("Enable Advanced Timing Overrides")]
            public bool EnableAdvancedTimingOverrides = false;

            [JsonProperty("Deployables Outside TC Cleanup Hours Override (0 = Use Profile Default)")]
            public double DeployablesOutsideTCleanupHoursOverride = 0d;

            [JsonProperty("Disconnected Structures Cleanup Hours Override (0 = Use Profile Default)")]
            public double DisconnectedStructuresCleanupHoursOverride = 0d;

            [JsonProperty("Inside Privilege Cleanup Hours Override (0 = Use Profile Default)")]
            public double InsidePrivilegeCleanupHoursOverride = 0d;

            [JsonProperty("Scheduled Evaluation Interval Minutes Override (0 = Use Profile Default)")]
            public double ScheduledEvaluationIntervalMinutesOverride = 0d;
        }

        private class AdvancedSafety
        {
            [JsonProperty("Allow Outside Privilege Cleanup")]
            public bool AllowOutsidePrivilegeCleanup = true;

            [JsonProperty("Allow Inside Privilege Cleanup")]
            public bool AllowInsidePrivilegeCleanup = false;

            [JsonProperty("Check Cupboard Authorization")]
            public bool CheckCupboardAuthorization = true;

            [JsonProperty("Protect Recently Active Players Hours")]
            public double ProtectRecentlyActivePlayersHours = 72d;

            [JsonProperty("Override Profile Recent Activity Protection")]
            public bool OverrideProfileRecentActivityProtection = false;

            [JsonProperty("Protect Entire Connected Structure")]
            public bool ProtectEntireConnectedStructure = true;
        }

        private class EventSafety
        {
            [JsonProperty("Enabled")]
            public bool Enabled = true;

            [JsonProperty("Use Known Plugin Integrations")]
            public bool UseKnownPluginIntegrations = true;

            [JsonProperty("Protect Unsaved Entities")]
            public bool ProtectUnsavedEntities = true;

            [JsonProperty("Protect Non-Steam Owned Entities")]
            public bool ProtectNonSteamOwnedEntities = true;

            [JsonProperty("Protect Entities Inside Monuments")]
            public bool ProtectEntitiesInsideMonuments = true;

            [JsonProperty("CopyPaste Protection Hours")]
            public double CopyPasteProtectionHours = 24d;

            [JsonProperty("Protected Skin IDs")]
            public ulong[] ProtectedSkinIds = { 3710562502UL, 755446UL };
        }

        private class AdvancedThresholds
        {
            [JsonProperty("Outside Privilege Health Fraction Threshold (0 = Ignore Health)")]
            public float OutsidePrivilegeHealthFractionThreshold = 0.6f;

            [JsonProperty("Inside Privilege Health Fraction Threshold (0 = Ignore Health)")]
            public float InsidePrivilegeHealthFractionThreshold = 0f;

            [JsonProperty("Override Profile Health Thresholds")]
            public bool OverrideProfileHealthThresholds = false;
        }

        private class AdvancedPerformance
        {
            [JsonProperty("Max Candidates Per Tick")]
            public int MaxCandidatesPerTick = 100;

            [JsonProperty("Override Profile Batch Size")]
            public bool OverrideProfileBatchSize = false;

            [JsonProperty("Index Candidates Per Tick")]
            public int IndexCandidatesPerTick = 500;

            [JsonProperty("Reconciliation Interval Hours (0 = Disabled)")]
            public double ReconciliationIntervalHours = 6d;

            [JsonProperty("Persistent State Save Interval Minutes")]
            public double DataSaveIntervalMinutes = 5d;
        }

        private class AdvancedLogging
        {
            [JsonProperty("Announce Scheduled Cleanup To Players")]
            public bool AnnounceScheduledCleanup = false;

            [JsonProperty("Debug Logging")]
            public bool DebugLogging = false;

            [JsonProperty("Log Scheduled Cleanup Summary")]
            public bool LogScheduledCleanupSummary = false;

            [JsonProperty("Log Scheduled Summaries Only When Removals Occur")]
            public bool LogScheduledSummariesOnlyWhenRemovalsOccur = true;

            [JsonProperty("Notify Connected Admins After Scheduled Cleanup")]
            public bool NotifyAdminsOnScheduledCleanup = true;
        }

        private class AdvancedTesting
        {
            [JsonProperty("Disable Scheduled Cleanup For Testing")]
            public bool DisableScheduledCleanupForTesting = false;
        }

        #endregion
    }
}
