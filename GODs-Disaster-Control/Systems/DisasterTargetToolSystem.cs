using System;
using System.Collections.Generic;
using Game.Common;
using Game.Buildings;
using Game.Citizens;
using Game.Creatures;
using Game.Events;
using Game.Effects;
using Game.Objects;
using Game.Prefabs;
using Game.Rendering;
using Game.Simulation;
using Game.Tools;
using Game.Vehicles;
using Unity.Mathematics;
using Unity.Jobs;
using Unity.Entities;
using Unity.Collections;
using UnityEngine.Scripting;
using CreatureResident = Game.Creatures.Resident;

namespace GODsDisasterControl.Systems
{
    public sealed partial class DisasterTargetToolSystem : ToolBaseSystem
    {
        private const int CoordinatePublishInterval = 8;
        private const int ConfirmCooldownFrames = 6;
        private const uint VehicleCleanupDelayFrames = 240u;
        private const float LightningCitizenSplashRadius = 3.5f;
        private const int LightningCitizenFatalityLimit = 2;
        private const double HailstormDurationSeconds = 60.0;
        private const double HailDamagePulseSeconds = 5.0;
        private const int HailVehiclesPerPulse = 8;
        private const int HailBuildingsPerPulse = 4;
        private const float MeteorImpactRadius = 100f;
        private const float MeteorFireSpreadRadius = 150f;
        private const int MeteorImpactFireLimit = 8;
        private const float MeteorDropHeight = 500f;
        private const float MeteorVisualBurialDepth = 4f;
        private const float MeteorNetworkPenetrationDepth = 0.65f;
        private const double MeteorImpactHoldSeconds = 0.5;
        private const double MeteorFallSeconds = 2.75;
        private const double MeteorImpactMarkSeconds = 60.0;
        private const double MeteorImpactMarkFadeSeconds = 15.0;
        private const float MeteorImpactMarkSize = 38f;
        private const float MissileImpactMarkSize = MeteorImpactMarkSize * 0.75f;
        private const float NuclearImpactMarkSize = NuclearInnerDestructionRadius * 2f;
        private const int MaximumConcurrentMeteors = 4;
        private const float MissileImpactRadius = 24f;
        private const float MissileFireRadius = 42f;
        private const int MissileFireLimit = 4;
        private const double MissileFlightSeconds = 1.65;
        private const int MaximumConcurrentMissiles = 3;
        private const float NuclearInnerDestructionRadius = 357.5f;
        private const float NuclearOuterDestructionRadius = 450f;
        private const float NuclearFireRadius = 520f;
        private const float NuclearCraterDepth = 58f;
        private const float NuclearCraterDiameter = NuclearInnerDestructionRadius * 2f;
        private const int NuclearFireLimit = 160;
        private const double NuclearFlightSeconds = 3.75;
        private const float NuclearLaunchHeight = 950f;
        private const double NuclearBlastDomeSeconds = 2.25;
        private const int MaximumConcurrentNuclearMissiles = 1;
        private static readonly float3[] MeteorNativeFireOffsets =
        {
            // Bury most of the cloned tree inside the 11 m meteor. Its native
            // wildfire plume remains above the rock while the tree itself is
            // largely hidden by the meteor mesh.
            new float3(0f, -3.5f, 0f)
        };
        private ToolBaseSystem m_PreviousTool;
        private string m_ArmedDisaster = string.Empty;
        private bool m_Armed;
        private int m_PublishCountdown;
        private float3 m_LastPosition;
        private float3 m_LastSurfacePosition;
        private Entity m_LastEntity;
        private bool m_HasTarget;
        private int m_ConfirmCooldown;
        private string m_ReticleStyle = "auto";
        private OverlayRenderSystem m_OverlayRenderSystem;
        private ClimateRenderSystem m_ClimateRenderSystem;
        private ClimateSystem m_ClimateSystem;
        private SimulationSystem m_SimulationSystem;
        private bool m_HailstormActive;
        private bool m_NuclearCraterEnabled = true;
        private DateTime m_HailstormEndsUtc;
        private DateTime m_NextHailDamagePulseUtc;
        private int m_HailDamagePulseCount;
        private Unity.Mathematics.Random m_HailRandom;
        private float m_PreviousHail;
        private bool m_PreviousPrecipitationOverrideState;
        private float m_PreviousPrecipitationOverrideValue;
        private bool m_PreviousCloudinessOverrideState;
        private float m_PreviousCloudinessOverrideValue;
        private EntityQuery m_FirePrefabQuery;
        private EntityQuery m_AllPrefabQuery;
        private EntityQuery m_ResidentTargetQuery;
        private EntityQuery m_LightningFuelQuery;
        private EntityQuery m_EarthquakeBuildingQuery;
        private EntityQuery m_HailVehicleQuery;
        private EntityQuery m_MeteorObjectQuery;
        private Entity m_WildfireEventSource;
        private Entity m_BuildingFireEventSource;
        private Entity m_LightningDecalPrefab;
        private bool m_LightningDecalLookupFailed;
        private bool m_MeteorCatalogLogged;
        private Entity m_MeteorBoulderPrefab;
        private Entity m_MeteorExplosionPrefab;
        private Entity m_MeteorSmokePrefab;
        private Entity m_MeteorFireCarrierPrefab;
        private readonly Dictionary<string, Entity> m_MeteorPrefabCache =
            new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase);
        private readonly List<MeteorFlight> m_MeteorFlights = new List<MeteorFlight>();
        private readonly List<MeteorImpactMark> m_MeteorImpactMarks =
            new List<MeteorImpactMark>();
        private readonly List<MissileFlight> m_MissileFlights = new List<MissileFlight>();
        private readonly List<NuclearFlight> m_NuclearFlights = new List<NuclearFlight>();
        private readonly List<NuclearBlastDome> m_NuclearBlastDomes =
            new List<NuclearBlastDome>();
        private readonly List<TimedEntityCleanup> m_MeteorEffectCleanup =
            new List<TimedEntityCleanup>();
        private readonly Dictionary<Entity, Entity> m_MeteorEffectDefinitions =
            new Dictionary<Entity, Entity>();
        private readonly Dictionary<Entity, int> m_LightningBuildingStrikes =
            new Dictionary<Entity, int>();
        private readonly Dictionary<Entity, uint> m_PendingVehicleCleanup =
            new Dictionary<Entity, uint>();
        private readonly List<Entity> m_CompletedVehicleCleanup =
            new List<Entity>();

        internal event Action<float3, string> TargetMoved;
        internal event Action<string, float3> TargetConfirmed;
        internal event Action TargetCancelled;
        internal event Action<string> TargetNotice;

        internal void SetNuclearCraterEnabled(bool enabled)
        {
            m_NuclearCraterEnabled = enabled;
            Mod.Log.Info($"Nuclear terrain crater option set to {(enabled ? "ON" : "OFF")}");
        }

        public override string toolID => "GODsDisasterTargetTool";
        public override PrefabBase GetPrefab() => null;
        public override bool TrySetPrefab(PrefabBase prefab) => false;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            m_OverlayRenderSystem = World.GetOrCreateSystemManaged<OverlayRenderSystem>();
            m_ClimateRenderSystem = World.GetOrCreateSystemManaged<ClimateRenderSystem>();
            m_ClimateSystem = World.GetOrCreateSystemManaged<ClimateSystem>();
            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_FirePrefabQuery = GetEntityQuery(ComponentType.ReadOnly<FireData>());
            // Decals are registered prefabs, but are not guaranteed to carry
            // StaticObjectData on their prefab entity. Search the complete
            // prefab registry so landscaping decals such as SmMulch01 are visible.
            m_AllPrefabQuery = GetEntityQuery(ComponentType.ReadOnly<PrefabData>());
            m_ResidentTargetQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<CreatureResident>(),
                    ComponentType.ReadOnly<Game.Objects.Transform>()
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>()
                }
            });
            m_LightningFuelQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Objects.Transform>(),
                    ComponentType.ReadOnly<PrefabRef>()
                },
                Any = new[]
                {
                    ComponentType.ReadOnly<Building>(),
                    ComponentType.ReadOnly<Tree>()
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Destroyed>(),
                    ComponentType.ReadOnly<Deleted>()
                }
            });
            m_EarthquakeBuildingQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Building>(),
                    ComponentType.ReadOnly<Game.Objects.Transform>(),
                    ComponentType.ReadOnly<PrefabRef>()
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Destroyed>(),
                    ComponentType.ReadOnly<Deleted>()
                }
            });
            m_HailVehicleQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Vehicle>(),
                    ComponentType.ReadOnly<PrefabRef>()
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Destroyed>(),
                    ComponentType.ReadOnly<Deleted>()
                }
            });
            m_MeteorObjectQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Game.Objects.Transform>()
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>()
                }
            });
            m_HailRandom = new Unity.Mathematics.Random(0x6E624EB7u);
            m_WildfireEventSource = Entity.Null;
            m_BuildingFireEventSource = Entity.Null;
            m_LightningDecalPrefab = Entity.Null;
            m_LightningDecalLookupFailed = false;
            m_MeteorBoulderPrefab = Entity.Null;
            m_MeteorExplosionPrefab = Entity.Null;
            m_MeteorSmokePrefab = Entity.Null;
            m_MeteorFireCarrierPrefab = Entity.Null;
            Enabled = false;
            Mod.Log.Info("Disaster target tool created (Batch 0.2A)");
        }

        protected override void OnStartRunning()
        {
            base.OnStartRunning();
            applyAction.shouldBeEnabled = true;
            cancelAction.shouldBeEnabled = true;
            Mod.Log.Info($"Target actions enabled: apply={applyAction.enabled}, cancel={cancelAction.enabled}");
        }

        protected override void OnDestroy()
        {
            if (m_HailstormActive) StopHailstorm(false);
            for (int i = 0; i < m_MeteorFlights.Count; i++)
            {
                if (m_MeteorFlights[i].Visual != null)
                    DestroyDedicatedMeteorVisual(m_MeteorFlights[i].Visual);
                Entity[] nativeFireEffects = m_MeteorFlights[i].NativeFireEffects;
                if (nativeFireEffects != null)
                {
                    for (int effectIndex = 0; effectIndex < nativeFireEffects.Length; effectIndex++)
                    {
                        Entity nativeFireEffect = nativeFireEffects[effectIndex];
                        if (nativeFireEffect != Entity.Null && EntityManager.Exists(nativeFireEffect))
                            EntityManager.DestroyEntity(nativeFireEffect);
                    }
                }
                Entity nativeFireRequest = m_MeteorFlights[i].NativeFireRequest;
                if (nativeFireRequest != Entity.Null && EntityManager.Exists(nativeFireRequest))
                    EntityManager.DestroyEntity(nativeFireRequest);
            }
            m_MeteorFlights.Clear();
            for (int i = 0; i < m_MeteorImpactMarks.Count; i++)
                DestroyMeteorImpactMark(m_MeteorImpactMarks[i]);
            m_MeteorImpactMarks.Clear();
            for (int i = 0; i < m_MissileFlights.Count; i++)
                DestroyMissileVisual(m_MissileFlights[i].Visual);
            m_MissileFlights.Clear();
            for (int i = 0; i < m_NuclearFlights.Count; i++)
                DestroyMissileVisual(m_NuclearFlights[i].Visual);
            m_NuclearFlights.Clear();
            for (int i = 0; i < m_NuclearBlastDomes.Count; i++)
                DestroyNuclearBlastDome(m_NuclearBlastDomes[i]);
            m_NuclearBlastDomes.Clear();
            base.OnDestroy();
        }

        public override void InitializeRaycast()
        {
            base.InitializeRaycast();
            m_ToolRaycastSystem.typeMask = TypeMask.Terrain | TypeMask.StaticObjects |
                TypeMask.MovingObjects | TypeMask.Net | TypeMask.Lanes;
            m_ToolRaycastSystem.collisionMask = CollisionMask.OnGround | CollisionMask.Overground;
            // TypeMask.Net alone is not enough: Layer.None (the default value)
            // filters every road/rail/sidewalk network out of the raycast.
            // Native selection tools use -1 when every network layer is valid.
            m_ToolRaycastSystem.netLayerMask = unchecked((Game.Net.Layer)(-1));
            m_ToolRaycastSystem.raycastFlags |= RaycastFlags.SubElements;
        }

        internal void Arm(string disasterId)
        {
            if (string.IsNullOrWhiteSpace(disasterId)) return;
            if (m_ToolSystem.activeTool != this) m_PreviousTool = m_ToolSystem.activeTool;

            m_ArmedDisaster = disasterId.Trim();
            m_Armed = true;
            m_PublishCountdown = 0;
            m_ConfirmCooldown = 0;
            m_HasTarget = false;
            m_LastEntity = Entity.Null;
            Enabled = true;
            m_ToolSystem.activeTool = this;
            Mod.Log.Info("Target tool armed for " + m_ArmedDisaster);
        }

        internal void CancelFromUI()
        {
            if (m_HailstormActive) StopHailstorm(true);
            else if (m_Armed) CancelTargeting(true);
        }

        internal void StartHailstorm()
        {
            if (m_ClimateSystem == null)
            {
                TargetNotice?.Invoke("HAILSTORM FAILED: the native climate system is unavailable.");
                Mod.Log.Error("Native hailstorm could not start because ClimateSystem is unavailable.");
                return;
            }

            // A global weather event does not use the world-targeting tool.
            if (m_Armed) CancelTargeting(false);

            if (!m_HailstormActive)
            {
                m_PreviousHail = m_ClimateSystem.hail;
                m_PreviousPrecipitationOverrideState = m_ClimateSystem.precipitation.overrideState;
                m_PreviousPrecipitationOverrideValue = m_ClimateSystem.precipitation.overrideValue;
                m_PreviousCloudinessOverrideState = m_ClimateSystem.cloudiness.overrideState;
                m_PreviousCloudinessOverrideValue = m_ClimateSystem.cloudiness.overrideValue;
            }

            m_HailstormActive = true;
            m_HailstormEndsUtc = DateTime.UtcNow.AddSeconds(HailstormDurationSeconds);
            m_NextHailDamagePulseUtc = DateTime.UtcNow.AddSeconds(HailDamagePulseSeconds);
            m_HailDamagePulseCount = 0;
            ApplyHailstormWeather();
            Enabled = true;
            Mod.Log.Info("Native citywide hailstorm started for 60 seconds.");
        }

        internal void SetReticleStyle(string style)
        {
            switch (style)
            {
                case "crosshair":
                case "rings":
                    m_ReticleStyle = style;
                    break;
                default:
                    m_ReticleStyle = "auto";
                    break;
            }
            Mod.Log.Info("Target reticle style set to " + m_ReticleStyle);
        }

        protected override JobHandle OnUpdate(JobHandle inputDeps)
        {
            ProcessHailstorm();
            ProcessPendingVehicleCleanup();
            ProcessMeteorFlights();
            ProcessMeteorImpactMarks();
            ProcessMissileFlights();
            ProcessNuclearFlights();
            ProcessNuclearBlastDomes();
            ProcessMeteorEffectCleanup();
            if (!m_Armed || m_ToolSystem.activeTool != this) return inputDeps;

            // ToolBaseSystem.UpdateActions is empty. Native tools enable these in
            // their own overrides, so our standalone tool must keep them enabled.
            applyAction.shouldBeEnabled = true;
            cancelAction.shouldBeEnabled = true;
            if (m_ConfirmCooldown > 0) m_ConfirmCooldown--;

            ControlPoint point;
            bool hasTarget = GetRaycastResult(out point);
            m_HasTarget = hasTarget;
            if (hasTarget)
            {
                m_LastPosition = point.m_HitPosition;
                m_LastSurfacePosition = point.m_Position;
                m_LastEntity = point.m_OriginalEntity;
                DrawTargetMarker(ref inputDeps);
                if (m_PublishCountdown <= 0)
                {
                    TargetMoved?.Invoke(m_LastPosition, GetTargetDetails(m_LastEntity));
                    m_PublishCountdown = CoordinatePublishInterval;
                }
                else m_PublishCountdown--;
            }

            if (cancelAction.WasPressedThisFrame())
            {
                CancelTargeting(true);
                return inputDeps;
            }

            if (hasTarget && m_ConfirmCooldown == 0 && applyAction.WasPressedThisFrame())
            {
                string disasterId = m_ArmedDisaster;
                float3 confirmedPosition = m_LastPosition;
                if (disasterId == "lightning")
                {
                    TriggerLightningStrike(m_LastEntity, confirmedPosition,
                        m_LastSurfacePosition);
                }
                else if (disasterId == "wildfire" && !TriggerWildfire(m_LastEntity, confirmedPosition))
                {
                    m_ConfirmCooldown = ConfirmCooldownFrames;
                    return inputDeps;
                }
                else if (disasterId == "earthquake" && !TriggerEarthquake(confirmedPosition))
                {
                    m_ConfirmCooldown = ConfirmCooldownFrames;
                    return inputDeps;
                }
                else if (disasterId == "meteor")
                {
                    // Net must be part of the raycast mask for roads, rails and
                    // sidewalks to become real targets. ControlPoint.m_Position
                    // is the resolved visible surface; m_HitPosition can still
                    // be the terrain hidden underneath a network.
                    if (math.all(math.isfinite(m_LastSurfacePosition)))
                        confirmedPosition = m_LastSurfacePosition;
                    bool networkTarget = IsNetworkTarget(m_LastEntity);
                    if (!LaunchMeteor(confirmedPosition, networkTarget))
                    {
                        m_ConfirmCooldown = ConfirmCooldownFrames;
                        return inputDeps;
                    }
                }
                else if (disasterId == "missile")
                {
                    if (math.all(math.isfinite(m_LastSurfacePosition)))
                        confirmedPosition = m_LastSurfacePosition;
                    if (!LaunchMissile(confirmedPosition))
                    {
                        m_ConfirmCooldown = ConfirmCooldownFrames;
                        return inputDeps;
                    }
                }
                else if (disasterId == "nuclear")
                {
                    if (math.all(math.isfinite(m_LastSurfacePosition)))
                        confirmedPosition = m_LastSurfacePosition;
                    if (!LaunchNuclearMissile(confirmedPosition))
                    {
                        m_ConfirmCooldown = ConfirmCooldownFrames;
                        return inputDeps;
                    }
                }
                m_ConfirmCooldown = ConfirmCooldownFrames;
                TargetConfirmed?.Invoke(disasterId, confirmedPosition);
                Mod.Log.Info($"Target confirmed: {disasterId} at {FormatPosition(confirmedPosition)}");
            }

            return inputDeps;
        }

        private void InspectMeteorAssetCatalog()
        {
            if (m_MeteorCatalogLogged)
            {
                TargetNotice?.Invoke("METEOR ASSET CATALOG already written to GODsDisasterControl.log.");
                return;
            }

            m_MeteorCatalogLogged = true;
            NativeArray<Entity> prefabs = m_AllPrefabQuery.ToEntityArray(Allocator.Temp);
            int namedRockPrefabs = 0;
            int placeableRockCandidates = 0;
            int namedEffectPrefabs = 0;
            int nativeEffectCandidates = 0;

            Mod.Log.Info($"METEOR ASSET CATALOG BEGIN: scanning {prefabs.Length} registered prefabs");
            try
            {
                // Pass 1: only report complete, placeable rock objects. The R1
                // catalog was intentionally broad and mostly found RenderPrefab
                // mesh dependencies. Those cannot be spawned as world objects.
                for (int i = 0; i < prefabs.Length; i++)
                {
                    Entity prefab = prefabs[i];
                    string prefabName = m_PrefabSystem.GetPrefabName(prefab);
                    if (!IsStrictRockName(prefabName)) continue;
                    namedRockPrefabs++;

                    bool hasStaticObject = EntityManager.HasComponent<StaticObjectData>(prefab);
                    bool hasObjectGeometry = EntityManager.HasComponent<ObjectGeometryData>(prefab);
                    if (!hasStaticObject || !hasObjectGeometry || IsInternalMeteorAsset(prefabName)) continue;

                    placeableRockCandidates++;
                    Mod.Log.Info($"METEOR PLACEABLE ROCK {placeableRockCandidates}: " +
                        $"name='{prefabName}', entity={prefab.Index}:{prefab.Version}, " +
                        $"staticObject={hasStaticObject}, objectGeometry={hasObjectGeometry}, " +
                        $"placeableObject={EntityManager.HasComponent<PlaceableObjectData>(prefab)}, " +
                        $"destructible={EntityManager.HasComponent<DestructibleObjectData>(prefab)}");
                }

                // Pass 2: report only actual effect prefabs. Matching a word
                // such as "fire" alone included fire stations and hydrants in
                // R1; requiring effect ECS data removes that garbage.
                for (int i = 0; i < prefabs.Length; i++)
                {
                    Entity prefab = prefabs[i];
                    string prefabName = m_PrefabSystem.GetPrefabName(prefab);
                    if (!IsStrictMeteorEffectName(prefabName)) continue;
                    namedEffectPrefabs++;

                    bool hasEffectData = EntityManager.HasComponent<EffectData>(prefab);
                    bool hasVfxData = EntityManager.HasComponent<VFXData>(prefab);
                    bool hasAudioData = EntityManager.HasComponent<AudioEffectData>(prefab);
                    bool hasLightData = EntityManager.HasComponent<LightEffectData>(prefab);
                    if (!hasEffectData && !hasVfxData && !hasAudioData && !hasLightData) continue;

                    nativeEffectCandidates++;
                    Mod.Log.Info($"METEOR NATIVE EFFECT {nativeEffectCandidates}: " +
                        $"name='{prefabName}', entity={prefab.Index}:{prefab.Version}, " +
                        $"effectData={hasEffectData}, vfxData={hasVfxData}, " +
                        $"audioEffectData={hasAudioData}, lightEffectData={hasLightData}");
                }
            }
            finally
            {
                prefabs.Dispose();
            }

            Mod.Log.Info($"METEOR ASSET CATALOG END: namedRockPrefabs={namedRockPrefabs}, " +
                $"placeableRockCandidates={placeableRockCandidates}, " +
                $"namedEffectPrefabs={namedEffectPrefabs}, " +
                $"nativeEffectCandidates={nativeEffectCandidates}");
            TargetNotice?.Invoke("METEOR ASSET INSPECTION R2 COMPLETE. Send GODsDisasterControl.log so the actual boulder and impact effects can be selected.");
        }

        private struct MeteorFlight
        {
            public UnityEngine.GameObject Visual;
            public Entity[] NativeFireEffects;
            public Entity NativeFireRequest;
            public Entity NativeFirePrefab;
            public DateTime NativeFireResolveDeadlineUtc;
            public float3 Start;
            public float3 Impact;
            public float3 VisualEnd;
            public bool NetworkTarget;
            public bool ImpactTriggered;
            public DateTime CleanupUtc;
            public DateTime StartedUtc;
        }

        private struct TimedEntityCleanup
        {
            public Entity Entity;
            public Entity Definition;
            public DateTime RemoveUtc;
            public bool DestroyNow;
        }

        private struct MeteorImpactMark
        {
            public UnityEngine.GameObject Visual;
            public UnityEngine.Material Material;
            public UnityEngine.Texture2D Texture;
            public UnityEngine.Mesh Mesh;
            public DateTime FadeUtc;
            public DateTime RemoveUtc;
        }

        private struct MissileFlight
        {
            public UnityEngine.GameObject Visual;
            public float3 Start;
            public float3 Impact;
            public DateTime StartedUtc;
        }

        private struct NuclearFlight
        {
            public UnityEngine.GameObject Visual;
            public float3 Start;
            public float3 Impact;
            public DateTime StartedUtc;
        }

        private struct NuclearBlastDome
        {
            public UnityEngine.GameObject Visual;
            public UnityEngine.Material Material;
            public DateTime StartedUtc;
        }

        private bool LaunchNuclearMissile(float3 impactPosition)
        {
            if (m_NuclearFlights.Count >= MaximumConcurrentNuclearMissiles)
            {
                TargetNotice?.Invoke("NUCLEAR LIMIT: wait for the active ICBM to impact.");
                return false;
            }

            float3 start = impactPosition + new float3(-520f, NuclearLaunchHeight, -520f);
            UnityEngine.GameObject visual = CreateNuclearMissileVisual(start, impactPosition);
            if (visual == null)
            {
                TargetNotice?.Invoke("NUCLEAR FAILED: ICBM visual could not be created.");
                return false;
            }
            m_NuclearFlights.Add(new NuclearFlight
            {
                Visual = visual,
                Start = start,
                Impact = impactPosition,
                StartedUtc = DateTime.UtcNow
            });
            Enabled = true;
            Mod.Log.Info($"Nuclear ICBM launched from {FormatPosition(start)} toward " +
                $"{FormatPosition(impactPosition)}; flightSeconds={NuclearFlightSeconds:F2}, " +
                $"guaranteedDestructionRadius={NuclearInnerDestructionRadius:F0}m, " +
                $"outerBlastRadius={NuclearOuterDestructionRadius:F0}m");
            return true;
        }

        private UnityEngine.GameObject CreateNuclearMissileVisual(float3 start, float3 impact)
        {
            try
            {
                UnityEngine.GameObject root = new UnityEngine.GameObject("GODs Nuclear ICBM Visual");
                root.hideFlags = UnityEngine.HideFlags.DontSave;
                root.transform.position = new UnityEngine.Vector3(start.x, start.y, start.z);
                UnityEngine.Vector3 direction = new UnityEngine.Vector3(
                    impact.x - start.x, impact.y - start.y, impact.z - start.z).normalized;
                root.transform.rotation = UnityEngine.Quaternion.FromToRotation(
                    UnityEngine.Vector3.up, direction);

                UnityEngine.Shader shader = UnityEngine.Shader.Find("HDRP/Lit");
                if (shader == null) shader = UnityEngine.Shader.Find("Shader Graphs/DefaultLit");
                if (shader == null) throw new InvalidOperationException("Nuclear ICBM material shader unavailable.");
                UnityEngine.Material white = new UnityEngine.Material(shader);
                white.name = "GODs Missile Nuclear White Material";
                UnityEngine.Color bodyColor = new UnityEngine.Color(0.91f, 0.92f, 0.94f, 1f);
                if (white.HasProperty("_BaseColor")) white.SetColor("_BaseColor", bodyColor);
                else white.color = bodyColor;
                if (white.HasProperty("_Smoothness")) white.SetFloat("_Smoothness", 0.28f);

                UnityEngine.Material black = new UnityEngine.Material(white);
                black.name = "GODs Missile Nuclear Black Nose Material";
                UnityEngine.Color noseColor = new UnityEngine.Color(0.035f, 0.04f, 0.05f, 1f);
                if (black.HasProperty("_BaseColor")) black.SetColor("_BaseColor", noseColor);
                else black.color = noseColor;

                UnityEngine.GameObject body = UnityEngine.GameObject.CreatePrimitive(
                    UnityEngine.PrimitiveType.Cylinder);
                body.name = "GODs Missile Nuclear Body";
                body.transform.SetParent(root.transform, false);
                // 175% of the compact Missile Strike body dimensions.
                body.transform.localScale = new UnityEngine.Vector3(2.01f, 7.70f, 2.01f);
                body.GetComponent<UnityEngine.MeshRenderer>().sharedMaterial = white;
                UnityEngine.Collider bodyCollider = body.GetComponent<UnityEngine.Collider>();
                if (bodyCollider != null) UnityEngine.Object.Destroy(bodyCollider);

                UnityEngine.GameObject nose = UnityEngine.GameObject.CreatePrimitive(
                    UnityEngine.PrimitiveType.Sphere);
                nose.name = "GODs Missile Nuclear Black Nose";
                nose.transform.SetParent(root.transform, false);
                nose.transform.localPosition = new UnityEngine.Vector3(0f, 7.96f, 0f);
                nose.transform.localScale = new UnityEngine.Vector3(2.05f, 3.06f, 2.05f);
                nose.GetComponent<UnityEngine.MeshRenderer>().sharedMaterial = black;
                UnityEngine.Collider noseCollider = nose.GetComponent<UnityEngine.Collider>();
                if (noseCollider != null) UnityEngine.Object.Destroy(noseCollider);

                const float engineRadius = 1.23f;
                for (int i = 0; i < 6; i++)
                {
                    float angle = math.radians(i * 60f);
                    UnityEngine.GameObject engine = new UnityEngine.GameObject(
                        $"GODs Nuclear Engine {i + 1}");
                    engine.hideFlags = UnityEngine.HideFlags.DontSave;
                    engine.transform.SetParent(root.transform, false);
                    engine.transform.localPosition = new UnityEngine.Vector3(
                        math.cos(angle) * engineRadius, -8.12f,
                        math.sin(angle) * engineRadius);
                    CreateMeteorParticleTrail(engine, 1);
                    CreateMeteorParticleTrail(engine, 2);
                }
                return root;
            }
            catch (Exception exception)
            {
                Mod.Log.Error(exception, "Nuclear ICBM visual creation failed.");
                return null;
            }
        }

        private bool LaunchMissile(float3 impactPosition)
        {
            if (m_MissileFlights.Count >= MaximumConcurrentMissiles)
            {
                TargetNotice?.Invoke("MISSILE LIMIT: wait for an active missile to impact.");
                return false;
            }

            float3 start = impactPosition + new float3(-145f, 220f, -145f);
            UnityEngine.GameObject visual = CreateMissileVisual(start, impactPosition);
            if (visual == null)
            {
                TargetNotice?.Invoke("MISSILE FAILED: projectile visual could not be created.");
                return false;
            }
            m_MissileFlights.Add(new MissileFlight
            {
                Visual = visual,
                Start = start,
                Impact = impactPosition,
                StartedUtc = DateTime.UtcNow
            });
            Enabled = true;
            Mod.Log.Info($"Missile launched from {FormatPosition(start)} toward " +
                $"{FormatPosition(impactPosition)}; flightSeconds={MissileFlightSeconds:F2}, " +
                $"impactRadius={MissileImpactRadius:F0}m");
            return true;
        }

        private UnityEngine.GameObject CreateMissileVisual(float3 start, float3 impact)
        {
            try
            {
                UnityEngine.GameObject root = new UnityEngine.GameObject("GODs Missile Visual");
                root.hideFlags = UnityEngine.HideFlags.DontSave;
                root.transform.position = new UnityEngine.Vector3(start.x, start.y, start.z);
                UnityEngine.Vector3 direction = new UnityEngine.Vector3(
                    impact.x - start.x, impact.y - start.y, impact.z - start.z).normalized;
                root.transform.rotation = UnityEngine.Quaternion.FromToRotation(
                    UnityEngine.Vector3.up, direction);

                UnityEngine.Shader shader = UnityEngine.Shader.Find("HDRP/Lit");
                if (shader == null) shader = UnityEngine.Shader.Find("Shader Graphs/DefaultLit");
                if (shader == null) throw new InvalidOperationException("Missile material shader unavailable.");
                UnityEngine.Material white = new UnityEngine.Material(shader);
                white.name = "GODs Missile White Material";
                UnityEngine.Color whiteColor = new UnityEngine.Color(0.88f, 0.90f, 0.92f, 1f);
                if (white.HasProperty("_BaseColor")) white.SetColor("_BaseColor", whiteColor);
                else white.color = whiteColor;
                if (white.HasProperty("_Smoothness")) white.SetFloat("_Smoothness", 0.32f);

                UnityEngine.Material finMaterial = new UnityEngine.Material(white);
                finMaterial.name = "GODs Missile Fin Material";
                UnityEngine.Color finColor = new UnityEngine.Color(0.55f, 0.58f, 0.61f, 1f);
                if (finMaterial.HasProperty("_BaseColor")) finMaterial.SetColor("_BaseColor", finColor);
                else finMaterial.color = finColor;

                UnityEngine.GameObject body = UnityEngine.GameObject.CreatePrimitive(
                    UnityEngine.PrimitiveType.Cylinder);
                body.name = "GODs Missile Body";
                body.transform.SetParent(root.transform, false);
                body.transform.localScale = new UnityEngine.Vector3(1.15f, 4.4f, 1.15f);
                body.GetComponent<UnityEngine.MeshRenderer>().sharedMaterial = white;
                UnityEngine.Collider bodyCollider = body.GetComponent<UnityEngine.Collider>();
                if (bodyCollider != null) UnityEngine.Object.Destroy(bodyCollider);

                UnityEngine.GameObject nose = UnityEngine.GameObject.CreatePrimitive(
                    UnityEngine.PrimitiveType.Sphere);
                nose.name = "GODs Missile Nose";
                nose.transform.SetParent(root.transform, false);
                nose.transform.localPosition = new UnityEngine.Vector3(0f, 4.55f, 0f);
                nose.transform.localScale = new UnityEngine.Vector3(1.17f, 1.75f, 1.17f);
                nose.GetComponent<UnityEngine.MeshRenderer>().sharedMaterial = white;
                UnityEngine.Collider noseCollider = nose.GetComponent<UnityEngine.Collider>();
                if (noseCollider != null) UnityEngine.Object.Destroy(noseCollider);

                for (int i = 0; i < 4; i++)
                {
                    CreateMissileFin(root.transform, finMaterial, i * 90f, -4.25f, -1.75f, 2.55f,
                        "Rear");
                    CreateMissileFin(root.transform, finMaterial, i * 90f + 45f, 2.05f, 3.70f, 1.85f,
                        "Front");
                }

                UnityEngine.GameObject engine = new UnityEngine.GameObject("GODs Missile Engine");
                engine.hideFlags = UnityEngine.HideFlags.DontSave;
                engine.transform.SetParent(root.transform, false);
                engine.transform.localPosition = new UnityEngine.Vector3(0f, -4.65f, 0f);
                CreateMeteorParticleTrail(engine, 0);
                CreateMeteorParticleTrail(engine, 1);
                CreateMeteorParticleTrail(engine, 2);
                return root;
            }
            catch (Exception exception)
            {
                Mod.Log.Error(exception, "Missile visual creation failed.");
                return null;
            }
        }

        private static void CreateMissileFin(UnityEngine.Transform parent,
            UnityEngine.Material material, float angle, float rearY, float frontY,
            float radialTip, string group)
        {
            UnityEngine.GameObject fin = new UnityEngine.GameObject($"GODs Missile {group} Fin");
            fin.hideFlags = UnityEngine.HideFlags.DontSave;
            fin.transform.SetParent(parent, false);
            fin.transform.localRotation = UnityEngine.Quaternion.Euler(0f, angle, 0f);
            UnityEngine.Mesh mesh = new UnityEngine.Mesh();
            mesh.name = $"GODs Missile {group} Fin Mesh";
            mesh.vertices = new[]
            {
                new UnityEngine.Vector3(1.0f, rearY, 0f),
                new UnityEngine.Vector3(radialTip, rearY, 0f),
                new UnityEngine.Vector3(1.0f, frontY, 0f)
            };
            mesh.triangles = new[] { 0, 1, 2, 2, 1, 0 };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            fin.AddComponent<UnityEngine.MeshFilter>().sharedMesh = mesh;
            fin.AddComponent<UnityEngine.MeshRenderer>().sharedMaterial = material;
        }

        private bool LaunchMeteor(float3 impactPosition, bool networkTarget)
        {
            if (m_MeteorFlights.Count >= MaximumConcurrentMeteors)
            {
                string message = $"METEOR LIMIT: wait for one of the " +
                    $"{MaximumConcurrentMeteors} active meteors to impact.";
                TargetNotice?.Invoke(message);
                Mod.Log.Info(message);
                return false;
            }

            float3 start = impactPosition + new float3(-110f, MeteorDropHeight, -70f);
            // The dedicated visual is not a placeable CS2 landscaping object,
            // so roads, sidewalks and other networks cannot reject or hide it.
            // Keep its center above the clicked surface by roughly half the
            // rock diameter so it visibly reaches the surface at impact.
            float3 visualEnd = impactPosition + new float3(0f, 5.5f, 0f);
            UnityEngine.GameObject visual = CreateDedicatedMeteorVisual(start);
            if (visual == null)
            {
                const string message = "METEOR FAILED: dedicated Unity visual could not be created.";
                TargetNotice?.Invoke(message);
                Mod.Log.Error(message);
                return false;
            }

            // Native trees/bushes are placeable simulation objects. Using one
            // as an airborne carrier creates placement-overlap warnings and
            // still does not expose the wildfire renderer during flight.
            // Keep the flight visual isolated from the native object pipeline.
            Entity nativeFirePrefab = Entity.Null;
            Entity nativeFireRequest = Entity.Null;

            m_MeteorFlights.Add(new MeteorFlight
            {
                Visual = visual,
                NativeFireEffects = null,
                NativeFireRequest = nativeFireRequest,
                NativeFirePrefab = nativeFirePrefab,
                NativeFireResolveDeadlineUtc = DateTime.UtcNow.AddSeconds(1.5),
                Start = start,
                Impact = impactPosition,
                VisualEnd = visualEnd,
                NetworkTarget = networkTarget,
                ImpactTriggered = false,
                CleanupUtc = DateTime.MinValue,
                StartedUtc = DateTime.UtcNow
            });

            Enabled = true;
            Mod.Log.Info($"Dedicated meteor visual launched " +
                $"from {FormatPosition(start)} toward {FormatPosition(impactPosition)}; " +
                $"visualEnd={FormatPosition(visualEnd)}, networkTarget={networkTarget}, " +
                $"fallSeconds={MeteorFallSeconds:F2}");
            return true;
        }

        private UnityEngine.GameObject CreateDedicatedMeteorVisual(float3 position)
        {
            try
            {
                UnityEngine.GameObject visual = UnityEngine.GameObject.CreatePrimitive(
                    UnityEngine.PrimitiveType.Sphere);
                visual.name = "GODs Dedicated Meteor Visual";
                visual.hideFlags = UnityEngine.HideFlags.DontSave;
                visual.transform.position = new UnityEngine.Vector3(position.x, position.y, position.z);
                visual.transform.rotation = UnityEngine.Quaternion.Euler(35f, 20f, 55f);
                visual.transform.localScale = new UnityEngine.Vector3(9.8f, 9.1f, 9.5f);

                UnityEngine.MeshFilter filter = visual.GetComponent<UnityEngine.MeshFilter>();
                if (filter != null && filter.sharedMesh != null)
                {
                    UnityEngine.Mesh rockMesh = UnityEngine.Object.Instantiate(filter.sharedMesh);
                    rockMesh.name = "GODs Procedural Meteor Rock";
                    UnityEngine.Vector3[] vertices = rockMesh.vertices;
                    for (int i = 0; i < vertices.Length; i++)
                    {
                        UnityEngine.Vector3 vertex = vertices[i];
                        float coarse = UnityEngine.Mathf.Sin(vertex.x * 8.7f + vertex.z * 5.3f);
                        float medium = UnityEngine.Mathf.Sin(vertex.y * 17.3f - vertex.x * 13.1f);
                        float fine = UnityEngine.Mathf.Sin(
                            vertex.x * 31.7f + vertex.y * 23.9f + vertex.z * 29.3f);
                        float noise = 1f + coarse * 0.10f + medium * 0.055f + fine * 0.025f;
                        vertices[i] = vertex * noise;
                    }
                    rockMesh.vertices = vertices;
                    rockMesh.RecalculateNormals();
                    rockMesh.RecalculateBounds();
                    filter.sharedMesh = rockMesh;
                }

                UnityEngine.MeshRenderer renderer = visual.GetComponent<UnityEngine.MeshRenderer>();
                if (renderer != null)
                {
                    UnityEngine.Shader shader = UnityEngine.Shader.Find("HDRP/Lit");
                    if (shader == null) shader = UnityEngine.Shader.Find("Shader Graphs/DefaultLit");
                    if (shader != null)
                    {
                        UnityEngine.Material material = new UnityEngine.Material(shader);
                        material.name = "GODs Meteor Rock Material";
                        UnityEngine.Color rockColor = new UnityEngine.Color(0.13f, 0.14f, 0.145f, 1f);
                        if (material.HasProperty("_BaseColor"))
                            material.SetColor("_BaseColor", rockColor);
                        else material.color = rockColor;
                        if (material.HasProperty("_Smoothness"))
                            material.SetFloat("_Smoothness", 0.08f);
                        if (material.HasProperty("_Metallic"))
                            material.SetFloat("_Metallic", 0f);
                        if (material.HasProperty("_CoatMask"))
                            material.SetFloat("_CoatMask", 0f);

                        UnityEngine.Texture2D rockTexture = CreateMeteorRockTexture();
                        if (material.HasProperty("_BaseColorMap"))
                        {
                            material.SetTexture("_BaseColorMap", rockTexture);
                            material.SetTextureScale("_BaseColorMap", new UnityEngine.Vector2(2.5f, 2.5f));
                        }
                        else if (material.HasProperty("_BaseMap"))
                        {
                            material.SetTexture("_BaseMap", rockTexture);
                            material.SetTextureScale("_BaseMap", new UnityEngine.Vector2(2.5f, 2.5f));
                        }
                        else if (material.HasProperty("_MainTex"))
                        {
                            material.SetTexture("_MainTex", rockTexture);
                            material.SetTextureScale("_MainTex", new UnityEngine.Vector2(2.5f, 2.5f));
                        }
                        renderer.sharedMaterial = material;
                    }
                }

                // Keep flight VFX entirely inside this disposable Unity visual.
                // World-space particles remain behind as the parent moves,
                // producing a trail without creating any ECS/world objects.
                CreateMeteorParticleTrail(visual, 0); // expanding charcoal smoke
                CreateMeteorParticleTrail(visual, 1); // orange/red flame body
                CreateMeteorParticleTrail(visual, 2); // yellow/white hot core

                return visual;
            }
            catch (Exception ex)
            {
                Mod.Log.Error(ex, "Dedicated meteor visual creation failed.");
                return null;
            }
        }

        private Entity RequestMeteorNativeFireCarrier(float3 position, out Entity carrierPrefab)
        {
            carrierPrefab = m_MeteorFireCarrierPrefab;
            if (carrierPrefab == Entity.Null || !EntityManager.Exists(carrierPrefab))
            {
                NativeArray<Entity> prefabs = m_AllPrefabQuery.ToEntityArray(Allocator.Temp);
                try
                {
                    for (int i = 0; i < prefabs.Length; i++)
                    {
                        Entity prefab = prefabs[i];
                        if (!string.Equals(m_PrefabSystem.GetPrefabName(prefab), "FlowerBushWild01",
                            StringComparison.OrdinalIgnoreCase)) continue;
                        carrierPrefab = prefab;
                        m_MeteorFireCarrierPrefab = prefab;
                        break;
                    }
                }
                finally { prefabs.Dispose(); }
            }

            if (carrierPrefab == Entity.Null)
            {
                Mod.Log.Error("Meteor wildfire carrier request skipped: FlowerBushWild01 prefab was not found.");
                return Entity.Null;
            }

            try
            {
                Entity request = EntityManager.CreateEntity(
                    ComponentType.ReadWrite<CreationDefinition>(),
                    ComponentType.ReadWrite<ObjectDefinition>(),
                    ComponentType.ReadWrite<Updated>());
                EntityManager.SetComponentData(request, new CreationDefinition
                {
                    m_Prefab = carrierPrefab,
                    m_Original = Entity.Null,
                    m_Flags = (CreationFlags)0
                });
                EntityManager.SetComponentData(request, new ObjectDefinition
                {
                    m_Position = position,
                    m_Rotation = quaternion.identity,
                    m_LocalPosition = position,
                    m_LocalRotation = quaternion.identity,
                    m_ParentMesh = -1,
                    m_Probability = 100,
                    m_PrefabSubIndex = -1,
                    m_Scale = new float3(1f),
                    m_Intensity = 1f
                });
                Mod.Log.Info($"Meteor native FlowerBushWild01 creation requested through ObjectCreationSystem: " +
                    $"request={request.Index}:{request.Version}, position={FormatPosition(position)}");
                return request;
            }
            catch (Exception exception)
            {
                Mod.Log.Error($"Meteor native bush creation request failed; rock-only fallback active. {exception}");
                carrierPrefab = Entity.Null;
                return Entity.Null;
            }
        }

        private void ResolveMeteorNativeFireCarrier(ref MeteorFlight flight)
        {
            if (flight.NativeFireEffects != null || flight.NativeFirePrefab == Entity.Null) return;
            if (DateTime.UtcNow > flight.NativeFireResolveDeadlineUtc)
            {
                Mod.Log.Info("Meteor native bush was not initialized before its deadline; rock-only fallback active.");
                flight.NativeFirePrefab = Entity.Null;
                return;
            }

            NativeArray<Entity> objects = m_MeteorObjectQuery.ToEntityArray(Allocator.Temp);
            try
            {
                float3 expectedPosition = flight.Start + MeteorNativeFireOffsets[0];
                for (int i = 0; i < objects.Length; i++)
                {
                    Entity candidate = objects[i];
                    if (!EntityManager.HasComponent<Game.Objects.Tree>(candidate)) continue;
                    if (EntityManager.HasComponent<Created>(candidate)) continue;
                    PrefabRef prefabRef = EntityManager.GetComponentData<PrefabRef>(candidate);
                    if (prefabRef.m_Prefab != flight.NativeFirePrefab) continue;
                    Game.Objects.Transform transform =
                        EntityManager.GetComponentData<Game.Objects.Transform>(candidate);
                    if (math.distancesq(transform.m_Position, expectedPosition) > 64f) continue;

                    flight.NativeFireEffects = new[] { candidate };
                    Entity eventSource = EnsureWildfireEventSource();
                    if (eventSource != Entity.Null)
                        QueueNativeIgnition(candidate, eventSource, 1f);
                    Mod.Log.Info($"Meteor native bush initialized and wildfire ignition queued: " +
                        $"carrier={candidate.Index}:{candidate.Version}, position={FormatPosition(transform.m_Position)}");
                    return;
                }
            }
            finally { objects.Dispose(); }
        }

        private void CreateMeteorParticleTrail(UnityEngine.GameObject meteor, int layer)
        {
            bool smoke = layer == 0;
            bool hotCore = layer == 2;
            UnityEngine.GameObject effect = new UnityEngine.GameObject(
                smoke ? "GODs Meteor Smoke Trail" :
                hotCore ? "GODs Meteor Hot Core Trail" : "GODs Meteor Flame Trail");
            effect.hideFlags = UnityEngine.HideFlags.DontSave;
            effect.transform.SetParent(meteor.transform, false);

            UnityEngine.ParticleSystem particles = effect.AddComponent<UnityEngine.ParticleSystem>();
            UnityEngine.ParticleSystem.MainModule main = particles.main;
            main.loop = true;
            main.playOnAwake = true;
            main.simulationSpace = UnityEngine.ParticleSystemSimulationSpace.World;
            main.maxParticles = smoke ? 260 : hotCore ? 180 : 230;
            main.startLifetime = smoke ? 2.2f : hotCore ? 0.34f : 0.72f;
            main.startSpeed = smoke ? 1.4f : hotCore ? 0.25f : 0.7f;
            main.startSize = smoke ? 6.2f : hotCore ? 2.2f : 4.1f;
            main.startColor = smoke
                ? new UnityEngine.ParticleSystem.MinMaxGradient(
                    new UnityEngine.Color(0.09f, 0.10f, 0.11f, 1f),
                    new UnityEngine.Color(0.25f, 0.26f, 0.27f, 1f))
                : hotCore
                    ? new UnityEngine.ParticleSystem.MinMaxGradient(
                        new UnityEngine.Color(1f, 0.92f, 0.28f, 1f),
                        new UnityEngine.Color(1f, 0.55f, 0.04f, 1f))
                    : new UnityEngine.ParticleSystem.MinMaxGradient(
                        new UnityEngine.Color(1f, 0.46f, 0.025f, 1f),
                        new UnityEngine.Color(0.72f, 0.045f, 0.006f, 1f));
            main.startRotation3D = true;
            main.startRotationX = new UnityEngine.ParticleSystem.MinMaxCurve(0f, 6.28318f);
            main.startRotationY = new UnityEngine.ParticleSystem.MinMaxCurve(0f, 6.28318f);
            main.startRotationZ = new UnityEngine.ParticleSystem.MinMaxCurve(0f, 6.28318f);
            main.gravityModifier = smoke ? -0.08f : 0f;

            UnityEngine.ParticleSystem.EmissionModule emission = particles.emission;
            emission.enabled = true;
            emission.rateOverTime = smoke ? 36f : hotCore ? 82f : 58f;

            UnityEngine.ParticleSystem.ShapeModule shape = particles.shape;
            shape.enabled = true;
            shape.shapeType = UnityEngine.ParticleSystemShapeType.Sphere;
            shape.radius = smoke ? 2.2f : hotCore ? 1.25f : 1.9f;

            UnityEngine.ParticleSystem.NoiseModule noise = particles.noise;
            noise.enabled = true;
            noise.strength = smoke ? 2.2f : hotCore ? 0.25f : 0.75f;
            noise.frequency = smoke ? 0.32f : 0.55f;
            noise.scrollSpeed = smoke ? 0.22f : 0.45f;

            UnityEngine.ParticleSystem.ColorOverLifetimeModule color = particles.colorOverLifetime;
            color.enabled = true;
            UnityEngine.Gradient gradient = new UnityEngine.Gradient();
            if (smoke)
            {
                gradient.SetKeys(
                    new[]
                    {
                        new UnityEngine.GradientColorKey(new UnityEngine.Color(0.22f, 0.23f, 0.24f), 0f),
                        new UnityEngine.GradientColorKey(new UnityEngine.Color(0.08f, 0.085f, 0.09f), 1f)
                    },
                    new[]
                    {
                        new UnityEngine.GradientAlphaKey(0.62f, 0f),
                        new UnityEngine.GradientAlphaKey(0.32f, 0.60f),
                        new UnityEngine.GradientAlphaKey(0f, 1f)
                    });
            }
            else if (hotCore)
            {
                gradient.SetKeys(
                    new[]
                    {
                        new UnityEngine.GradientColorKey(new UnityEngine.Color(1f, 1f, 0.72f), 0f),
                        new UnityEngine.GradientColorKey(new UnityEngine.Color(1f, 0.72f, 0.08f), 0.55f),
                        new UnityEngine.GradientColorKey(new UnityEngine.Color(1f, 0.25f, 0.01f), 1f)
                    },
                    new[]
                    {
                        new UnityEngine.GradientAlphaKey(1f, 0f),
                        new UnityEngine.GradientAlphaKey(0.92f, 0.65f),
                        new UnityEngine.GradientAlphaKey(0f, 1f)
                    });
            }
            else
            {
                gradient.SetKeys(
                    new[]
                    {
                        new UnityEngine.GradientColorKey(new UnityEngine.Color(1f, 0.72f, 0.10f), 0f),
                        new UnityEngine.GradientColorKey(new UnityEngine.Color(1f, 0.08f, 0.01f), 0.55f),
                        new UnityEngine.GradientColorKey(new UnityEngine.Color(0.16f, 0.02f, 0.01f), 1f)
                    },
                    new[]
                    {
                        new UnityEngine.GradientAlphaKey(0.95f, 0f),
                        new UnityEngine.GradientAlphaKey(0.72f, 0.58f),
                        new UnityEngine.GradientAlphaKey(0f, 1f)
                    });
            }
            color.color = gradient;

            UnityEngine.ParticleSystem.SizeOverLifetimeModule sizeOverLifetime =
                particles.sizeOverLifetime;
            sizeOverLifetime.enabled = true;
            sizeOverLifetime.size = new UnityEngine.ParticleSystem.MinMaxCurve(
                1f, new UnityEngine.AnimationCurve(
                    new UnityEngine.Keyframe(0f, hotCore ? 0.55f : 0.35f),
                    new UnityEngine.Keyframe(smoke ? 0.35f : 0.18f, 1f),
                    new UnityEngine.Keyframe(1f, smoke ? 1.35f : 0.08f)));

            UnityEngine.ParticleSystemRenderer particleRenderer =
                effect.GetComponent<UnityEngine.ParticleSystemRenderer>();
            if (particleRenderer != null)
            {
                // A soft circular alpha texture removes the square/mesh look
                // from the earlier prototypes. Billboards always face the
                // camera, while world-space simulation leaves them behind.
                particleRenderer.renderMode = UnityEngine.ParticleSystemRenderMode.Billboard;
                UnityEngine.Shader shader = UnityEngine.Shader.Find("Particles/Standard Unlit");
                if (shader == null)
                    shader = UnityEngine.Shader.Find("Legacy Shaders/Particles/Alpha Blended");
                if (shader == null) shader = UnityEngine.Shader.Find("HDRP/Unlit");
                if (shader != null)
                {
                    UnityEngine.Material material = new UnityEngine.Material(shader);
                    material.name = smoke
                        ? "GODs Meteor Smoke Particle Material"
                        : "GODs Meteor Fire Particle Material";
                    UnityEngine.Color materialColor = UnityEngine.Color.white;
                    if (material.HasProperty("_BaseColor"))
                        material.SetColor("_BaseColor", materialColor);
                    else if (material.HasProperty("_UnlitColor"))
                        material.SetColor("_UnlitColor", materialColor);
                    else material.color = materialColor;

                    UnityEngine.Texture2D softTexture = CreateSoftParticleTexture(smoke);
                    if (material.HasProperty("_BaseColorMap"))
                        material.SetTexture("_BaseColorMap", softTexture);
                    if (material.HasProperty("_UnlitColorMap"))
                        material.SetTexture("_UnlitColorMap", softTexture);
                    if (material.HasProperty("_BaseMap"))
                        material.SetTexture("_BaseMap", softTexture);
                    if (material.HasProperty("_MainTex"))
                        material.SetTexture("_MainTex", softTexture);

                    // Configure transparent alpha blending explicitly. These
                    // property names cover the built-in particle shader and
                    // CS2's HDRP fallback without depending on a game prefab.
                    if (material.HasProperty("_Surface")) material.SetFloat("_Surface", 1f);
                    if (material.HasProperty("_SurfaceType")) material.SetFloat("_SurfaceType", 1f);
                    // Unity's Particles/Standard Unlit shader uses _Mode,
                    // not HDRP's _Surface flags. Mode 2 is Fade; without it
                    // the alpha texture is sampled onto an opaque rectangle.
                    if (material.HasProperty("_Mode")) material.SetFloat("_Mode", 2f);
                    if (material.HasProperty("_BlendMode")) material.SetFloat("_BlendMode", 0f);
                    if (material.HasProperty("_SrcBlend")) material.SetFloat("_SrcBlend", 5f);
                    if (material.HasProperty("_DstBlend")) material.SetFloat("_DstBlend", 10f);
                    if (material.HasProperty("_ZWrite")) material.SetFloat("_ZWrite", 0f);
                    material.SetOverrideTag("RenderType", "Transparent");
                    material.DisableKeyword("_ALPHATEST_ON");
                    material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                    material.EnableKeyword("_ALPHABLEND_ON");
                    material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                    material.renderQueue = 3000;
                    particleRenderer.sharedMaterial = material;
                    Mod.Log.Info($"Meteor particle layer created: " +
                        $"layer={(smoke ? "smoke" : hotCore ? "hotCore" : "flame")}, " +
                        $"shader='{shader.name}', alphaMode=fade, " +
                        $"rate={(smoke ? 36 : hotCore ? 82 : 58)}/s");
                }
                else
                {
                    Mod.Log.Error("Meteor particle trail disabled: no compatible transparent particle shader was found.");
                    UnityEngine.Object.Destroy(effect);
                    return;
                }
            }

            particles.Play(true);
        }

        private UnityEngine.Mesh CreateMeteorParticleMesh(bool smoke, bool hotCore)
        {
            UnityEngine.GameObject source = UnityEngine.GameObject.CreatePrimitive(
                UnityEngine.PrimitiveType.Sphere);
            UnityEngine.Mesh sourceMesh = source.GetComponent<UnityEngine.MeshFilter>().sharedMesh;
            UnityEngine.Mesh mesh = UnityEngine.Object.Instantiate(sourceMesh);
            UnityEngine.Object.Destroy(source);
            mesh.name = smoke
                ? "GODs Rounded Smoke Particle Mesh"
                : hotCore ? "GODs Rounded Hot Core Particle Mesh" : "GODs Rounded Flame Particle Mesh";
            UnityEngine.Vector3[] vertices = mesh.vertices;
            for (int i = 0; i < vertices.Length; i++)
            {
                UnityEngine.Vector3 v = vertices[i];
                float warp = 1f + 0.10f * UnityEngine.Mathf.Sin(v.x * 9.1f + v.y * 6.7f + v.z * 8.3f);
                v *= warp;
                if (!smoke) v.y *= hotCore ? 1.15f : 1.28f;
                vertices[i] = v;
            }
            mesh.vertices = vertices;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private UnityEngine.Texture2D CreateSoftParticleTexture(bool smoke)
        {
            const int size = 64;
            UnityEngine.Texture2D texture = new UnityEngine.Texture2D(
                size, size, UnityEngine.TextureFormat.RGBA32, true);
            texture.name = smoke
                ? "GODs Soft Smoke Particle Texture"
                : "GODs Soft Fire Particle Texture";
            texture.wrapMode = UnityEngine.TextureWrapMode.Clamp;
            texture.filterMode = UnityEngine.FilterMode.Bilinear;
            UnityEngine.Color[] pixels = new UnityEngine.Color[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float nx = (x + 0.5f) / size * 2f - 1f;
                    float ny = (y + 0.5f) / size * 2f - 1f;
                    float radius = UnityEngine.Mathf.Sqrt(nx * nx + ny * ny);
                    float edge = UnityEngine.Mathf.Clamp01(1f - radius);
                    float breakup = 0.72f + 0.28f * UnityEngine.Mathf.PerlinNoise(
                        x * 0.13f + (smoke ? 10f : 30f),
                        y * 0.13f + (smoke ? 20f : 40f));
                    float alpha = edge * edge * breakup;
                    UnityEngine.Color rgb = smoke
                        ? new UnityEngine.Color(0.58f, 0.60f, 0.62f, alpha)
                        : new UnityEngine.Color(1f, 0.48f + edge * 0.42f, 0.04f, alpha);
                    pixels[y * size + x] = rgb;
                }
            }
            texture.SetPixels(pixels);
            texture.Apply(true, false);
            return texture;
        }

        private UnityEngine.Texture2D CreateMeteorRockTexture()
        {
            const int size = 128;
            UnityEngine.Texture2D texture = new UnityEngine.Texture2D(
                size, size, UnityEngine.TextureFormat.RGBA32, true);
            texture.name = "GODs Procedural Charcoal Rock Texture";
            texture.wrapMode = UnityEngine.TextureWrapMode.Repeat;
            texture.filterMode = UnityEngine.FilterMode.Bilinear;
            UnityEngine.Color[] pixels = new UnityEngine.Color[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float broad = UnityEngine.Mathf.PerlinNoise(x * 0.055f, y * 0.055f);
                    float detail = UnityEngine.Mathf.PerlinNoise(
                        20f + x * 0.19f, 40f + y * 0.19f);
                    float value = UnityEngine.Mathf.Clamp01(0.45f + broad * 0.38f + detail * 0.17f);
                    pixels[y * size + x] = new UnityEngine.Color(
                        value * 0.72f, value * 0.75f, value * 0.77f, 1f);
                }
            }
            texture.SetPixels(pixels);
            texture.Apply(true, false);
            return texture;
        }

        private void DestroyDedicatedMeteorVisual(UnityEngine.GameObject visual)
        {
            if (visual == null) return;
            UnityEngine.MeshFilter filter = visual.GetComponent<UnityEngine.MeshFilter>();
            UnityEngine.Mesh mesh = filter != null ? filter.sharedMesh : null;
            UnityEngine.MeshRenderer renderer = visual.GetComponent<UnityEngine.MeshRenderer>();
            UnityEngine.Material material = renderer != null ? renderer.sharedMaterial : null;
            UnityEngine.Texture texture = null;
            if (material != null)
            {
                if (material.HasProperty("_BaseColorMap"))
                    texture = material.GetTexture("_BaseColorMap");
                else if (material.HasProperty("_BaseMap"))
                    texture = material.GetTexture("_BaseMap");
                else if (material.HasProperty("_MainTex"))
                    texture = material.GetTexture("_MainTex");
            }
            UnityEngine.ParticleSystemRenderer[] particleRenderers =
                visual.GetComponentsInChildren<UnityEngine.ParticleSystemRenderer>(true);
            UnityEngine.Material[] particleMaterials =
                new UnityEngine.Material[particleRenderers.Length];
            UnityEngine.Mesh[] particleMeshes =
                new UnityEngine.Mesh[particleRenderers.Length];
            UnityEngine.Texture[] particleTextures =
                new UnityEngine.Texture[particleRenderers.Length];
            for (int i = 0; i < particleRenderers.Length; i++)
            {
                particleMaterials[i] = particleRenderers[i].sharedMaterial;
                particleMeshes[i] = particleRenderers[i].mesh;
                UnityEngine.Material particleMaterial = particleMaterials[i];
                if (particleMaterial == null) continue;
                if (particleMaterial.HasProperty("_BaseColorMap"))
                    particleTextures[i] = particleMaterial.GetTexture("_BaseColorMap");
                else if (particleMaterial.HasProperty("_UnlitColorMap"))
                    particleTextures[i] = particleMaterial.GetTexture("_UnlitColorMap");
                else if (particleMaterial.HasProperty("_MainTex"))
                    particleTextures[i] = particleMaterial.GetTexture("_MainTex");
            }

            UnityEngine.Object.Destroy(visual);
            if (mesh != null && mesh.name == "GODs Procedural Meteor Rock")
                UnityEngine.Object.Destroy(mesh);
            if (material != null && material.name == "GODs Meteor Rock Material")
                UnityEngine.Object.Destroy(material);
            if (texture != null && texture.name == "GODs Procedural Charcoal Rock Texture")
                UnityEngine.Object.Destroy(texture);
            for (int i = 0; i < particleMaterials.Length; i++)
            {
                UnityEngine.Material particleMaterial = particleMaterials[i];
                if (particleMaterial != null && particleMaterial.name.StartsWith("GODs Meteor "))
                    UnityEngine.Object.Destroy(particleMaterial);
                UnityEngine.Texture particleTexture = particleTextures[i];
                if (particleTexture != null && particleTexture.name.StartsWith("GODs Soft "))
                    UnityEngine.Object.Destroy(particleTexture);
                UnityEngine.Mesh particleMesh = particleMeshes[i];
                if (particleMesh != null &&
                    (particleMesh.name.StartsWith("GODs Faceted ") ||
                     particleMesh.name.StartsWith("GODs Rounded ")))
                    UnityEngine.Object.Destroy(particleMesh);
            }
        }

        private bool IsNetworkTarget(Entity entity)
        {
            if (entity == Entity.Null || !EntityManager.Exists(entity)) return false;
            return EntityManager.HasComponent<Game.Net.Edge>(entity) ||
                EntityManager.HasComponent<Game.Net.Node>(entity) ||
                EntityManager.HasComponent<Game.Net.Lane>(entity) ||
                EntityManager.HasComponent<Game.Net.Road>(entity) ||
                EntityManager.HasComponent<Game.Net.Composition>(entity) ||
                EntityManager.HasComponent<Game.Net.Aggregated>(entity) ||
                EntityManager.HasComponent<Game.Objects.NetObject>(entity);
        }

        private void CopyMeteorRenderBuffers(Entity prefab, Entity meteor)
        {
            int subMeshCount = 0;
            int subMeshGroupCount = 0;

            if (EntityManager.HasBuffer<Game.Prefabs.SubMesh>(prefab) &&
                EntityManager.HasBuffer<Game.Prefabs.SubMesh>(meteor))
            {
                DynamicBuffer<Game.Prefabs.SubMesh> source =
                    EntityManager.GetBuffer<Game.Prefabs.SubMesh>(prefab, true);
                DynamicBuffer<Game.Prefabs.SubMesh> destination =
                    EntityManager.GetBuffer<Game.Prefabs.SubMesh>(meteor);
                destination.Clear();
                for (int i = 0; i < source.Length; i++) destination.Add(source[i]);
                subMeshCount = source.Length;
            }

            if (EntityManager.HasBuffer<SubMeshGroup>(prefab) &&
                EntityManager.HasBuffer<SubMeshGroup>(meteor))
            {
                DynamicBuffer<SubMeshGroup> source =
                    EntityManager.GetBuffer<SubMeshGroup>(prefab, true);
                DynamicBuffer<SubMeshGroup> destination =
                    EntityManager.GetBuffer<SubMeshGroup>(meteor);
                destination.Clear();
                for (int i = 0; i < source.Length; i++) destination.Add(source[i]);
                subMeshGroupCount = source.Length;
            }

            Mod.Log.Info($"Meteor render buffers initialized from Boulder07: " +
                $"subMeshes={subMeshCount}, subMeshGroups={subMeshGroupCount}");
        }

        private void ProcessMeteorFlights()
        {
            for (int i = m_MeteorFlights.Count - 1; i >= 0; i--)
            {
                MeteorFlight flight = m_MeteorFlights[i];
                if (flight.Visual == null)
                {
                    ScheduleNativeMeteorFireCleanup(flight.NativeFireEffects, 0d);
                    if (flight.NativeFireRequest != Entity.Null && EntityManager.Exists(flight.NativeFireRequest))
                        EntityManager.DestroyEntity(flight.NativeFireRequest);
                    m_MeteorFlights.RemoveAt(i);
                    continue;
                }

                if (flight.ImpactTriggered)
                {
                    if (DateTime.UtcNow < flight.CleanupUtc) continue;
                    DestroyDedicatedMeteorVisual(flight.Visual);
                    m_MeteorFlights.RemoveAt(i);
                    continue;
                }

                float progress = math.saturate((float)((DateTime.UtcNow - flight.StartedUtc).TotalSeconds / MeteorFallSeconds));
                float eased = progress * progress;
                float3 position = math.lerp(flight.Start, flight.VisualEnd, eased);
                flight.Visual.transform.position =
                    new UnityEngine.Vector3(position.x, position.y, position.z);
                flight.Visual.transform.Rotate(2.6f, 4.3f, 2.0f, UnityEngine.Space.Self);
                ResolveMeteorNativeFireCarrier(ref flight);
                MoveMeteorNativeFireCluster(flight.NativeFireEffects, position);

                if (progress < 1f)
                {
                    m_MeteorFlights[i] = flight;
                    continue;
                }
                TriggerMeteorImpact(flight.Impact);
                ScheduleNativeMeteorFireCleanup(flight.NativeFireEffects, MeteorImpactHoldSeconds);
                if (flight.NativeFireRequest != Entity.Null && EntityManager.Exists(flight.NativeFireRequest))
                    EntityManager.DestroyEntity(flight.NativeFireRequest);
                flight.ImpactTriggered = true;
                flight.CleanupUtc = DateTime.UtcNow.AddSeconds(MeteorImpactHoldSeconds);
                m_MeteorFlights[i] = flight;
            }
        }

        private void ProcessMissileFlights()
        {
            for (int i = m_MissileFlights.Count - 1; i >= 0; i--)
            {
                MissileFlight flight = m_MissileFlights[i];
                if (flight.Visual == null)
                {
                    m_MissileFlights.RemoveAt(i);
                    continue;
                }
                float progress = math.saturate((float)((DateTime.UtcNow - flight.StartedUtc)
                    .TotalSeconds / MissileFlightSeconds));
                float eased = progress * progress;
                float3 position = math.lerp(flight.Start, flight.Impact, eased);
                flight.Visual.transform.position = new UnityEngine.Vector3(
                    position.x, position.y, position.z);
                if (progress < 1f) continue;

                TriggerMissileImpact(flight.Impact);
                DestroyMissileVisual(flight.Visual);
                m_MissileFlights.RemoveAt(i);
            }
        }

        private void TriggerMissileImpact(float3 impactPosition)
        {
            bool impactMark = CreateMissileImpactMark(impactPosition);
            int fires = IgniteMissileImpactRing(impactPosition);
            int buildings = DestroyMeteorBuildings(impactPosition, MissileImpactRadius);
            int vehicles = DestroyMeteorVehicles(impactPosition, MissileImpactRadius);
            int citizens = KillMeteorCitizens(impactPosition, MissileImpactRadius);
            int trees = DestroyMeteorTrees(impactPosition, MissileImpactRadius);
            Mod.Log.Info($"Missile impact at {FormatPosition(impactPosition)}: " +
                $"radius={MissileImpactRadius:F0}m, buildings={buildings}, vehicles={vehicles}, " +
                $"citizens={citizens}, trees={trees}, nearbyFires={fires}, impactMark={impactMark}");
            TargetNotice?.Invoke($"MISSILE IMPACT: {buildings} buildings, {vehicles} vehicles and " +
                $"{citizens} citizens hit inside {MissileImpactRadius:F0} m; {fires} nearby fires started.");
        }

        private void ProcessNuclearFlights()
        {
            for (int i = m_NuclearFlights.Count - 1; i >= 0; i--)
            {
                NuclearFlight flight = m_NuclearFlights[i];
                if (flight.Visual == null)
                {
                    m_NuclearFlights.RemoveAt(i);
                    continue;
                }
                float progress = math.saturate((float)((DateTime.UtcNow - flight.StartedUtc)
                    .TotalSeconds / NuclearFlightSeconds));
                float eased = progress * progress;
                float3 position = math.lerp(flight.Start, flight.Impact, eased);
                flight.Visual.transform.position = new UnityEngine.Vector3(
                    position.x, position.y, position.z);
                if (progress < 1f) continue;

                TriggerNuclearImpact(flight.Impact);
                DestroyMissileVisual(flight.Visual);
                m_NuclearFlights.RemoveAt(i);
            }
        }

        private void TriggerNuclearImpact(float3 impactPosition)
        {
            bool impactMark = CreateNuclearImpactMark(impactPosition);
            bool dome = CreateNuclearBlastDome(impactPosition);
            bool terrainCrater = m_NuclearCraterEnabled && ApplyNuclearTerrainCrater(impactPosition);
            if (!m_NuclearCraterEnabled)
                Mod.Log.Info("Nuclear terrain crater skipped: user option is OFF.");
            int erasedObjects = EraseNuclearCoreObjects(impactPosition, NuclearInnerDestructionRadius);
            int innerCitizens = KillMeteorCitizens(impactPosition, NuclearInnerDestructionRadius);
            int outerBuildings = DestroyNuclearOuterBuildings(impactPosition);
            int outerVehicles = DestroyNuclearOuterVehicles(impactPosition);
            int outerCitizens = KillNuclearOuterCitizens(impactPosition);
            int outerTrees = DestroyNuclearOuterTrees(impactPosition);
            int fires = IgniteNuclearFireField(impactPosition);
            int contaminatedMaps = ApplyNuclearContamination(impactPosition, NuclearFireRadius);

            Mod.Log.Info($"Nuclear impact at {FormatPosition(impactPosition)}: " +
                $"innerRadius={NuclearInnerDestructionRadius:F0}m, outerRadius={NuclearOuterDestructionRadius:F0}m, " +
                $"erasedCoreObjects={erasedObjects}, innerCitizens={innerCitizens}, " +
                $"outerBuildings={outerBuildings}, outerVehicles={outerVehicles}, " +
                $"outerCitizens={outerCitizens}, outerTrees={outerTrees}, fires={fires}, " +
                $"contaminatedMaps={contaminatedMaps}/3, dome={dome}, impactMark={impactMark}, " +
                $"terrainCrater={terrainCrater}");
            TargetNotice?.Invoke($"NUCLEAR IMPACT: total destruction inside " +
                $"{NuclearInnerDestructionRadius:F0} m; severe blast and {fires} fires out to " +
                $"{NuclearFireRadius:F0} m.");
        }

        private bool ApplyNuclearTerrainCrater(float3 center)
        {
            EntityQuery terraformingQuery = default;
            EntityQuery brushQuery = default;
            NativeArray<Entity> terraformingPrefabs = default;
            NativeArray<Entity> brushPrefabs = default;
            try
            {
                terraformingQuery = GetEntityQuery(
                    ComponentType.ReadOnly<PrefabData>(),
                    ComponentType.ReadOnly<TerraformingData>());
                brushQuery = GetEntityQuery(
                    ComponentType.ReadOnly<PrefabData>(),
                    ComponentType.ReadOnly<BrushData>());
                terraformingPrefabs = terraformingQuery.ToEntityArray(Allocator.Temp);
                brushPrefabs = brushQuery.ToEntityArray(Allocator.Temp);

                Entity shiftTerrainPrefab = Entity.Null;
                string shiftTerrainName = string.Empty;
                TerraformingData shiftTerrainData = default;
                for (int i = 0; i < terraformingPrefabs.Length; i++)
                {
                    Entity candidate = terraformingPrefabs[i];
                    TerraformingData data = EntityManager.GetComponentData<TerraformingData>(candidate);
                    // The inspected game build routes target 0 to terrain height.
                    // Type 0 is its signed shift operation; unlike the other
                    // height modes it accepts negative strength unchanged.
                    if ((int)data.m_Target != 0 || (int)data.m_Type != 0) continue;
                    string name = m_PrefabSystem.GetPrefabName(candidate) ?? string.Empty;
                    if (shiftTerrainPrefab == Entity.Null ||
                        name.IndexOf("shift", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        shiftTerrainPrefab = candidate;
                        shiftTerrainName = name;
                        shiftTerrainData = data;
                    }
                    if (name.IndexOf("shift", StringComparison.OrdinalIgnoreCase) >= 0) break;
                }

                Entity circularBrushPrefab = Entity.Null;
                string circularBrushName = string.Empty;
                for (int i = 0; i < brushPrefabs.Length; i++)
                {
                    Entity candidate = brushPrefabs[i];
                    string name = m_PrefabSystem.GetPrefabName(candidate) ?? string.Empty;
                    if (circularBrushPrefab == Entity.Null)
                    {
                        circularBrushPrefab = candidate;
                        circularBrushName = name;
                    }
                    bool circular = name.IndexOf("circle", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("round", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool smooth = name.IndexOf("smooth", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("soft", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!circular) continue;
                    circularBrushPrefab = candidate;
                    circularBrushName = name;
                    if (smooth) break;
                }

                if (shiftTerrainPrefab == Entity.Null || circularBrushPrefab == Entity.Null)
                {
                    Mod.Log.Error("Nuclear terrain crater skipped: compatible signed height-shift " +
                        $"prefab={shiftTerrainPrefab != Entity.Null}, brushPrefab={circularBrushPrefab != Entity.Null}.");
                    return false;
                }

                Brush brush = new Brush
                {
                    m_Tool = shiftTerrainPrefab,
                    m_Position = center,
                    m_Target = center,
                    m_Start = center,
                    m_Angle = 0f,
                    m_Size = NuclearCraterDiameter,
                    m_Strength = -NuclearCraterDepth,
                    m_Opacity = 1f
                };
                ApplyBrushesSystem applySystem =
                    World.GetOrCreateSystemManaged<ApplyBrushesSystem>();
                System.Reflection.MethodInfo applyHeight = typeof(ApplyBrushesSystem).GetMethod(
                    "ApplyHeight", System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic);
                if (applyHeight == null)
                {
                    Mod.Log.Error("Nuclear terrain crater skipped: ApplyBrushesSystem.ApplyHeight was not found.");
                    return false;
                }

                // Invoke the exact native method found by the 0.14C inspector.
                // This avoids a transient Temp brush entity being discarded before
                // ApplyBrushesSystem sees it, which happened in v0.14.5.
                applyHeight.Invoke(applySystem,
                    new object[] { brush, circularBrushPrefab, shiftTerrainData.m_Type });

                Mod.Log.Info($"Native nuclear terrain crater applied: " +
                    $"center={FormatPosition(center)}, diameter={NuclearCraterDiameter:F1}m, " +
                    $"depthDelta=-{NuclearCraterDepth:F1}m, tool='{shiftTerrainName}', brush='{circularBrushName}'.");
                return true;
            }
            catch (Exception exception)
            {
                Mod.Log.Error(exception, "Nuclear terrain crater request failed; blast effects continued.");
                return false;
            }
            finally
            {
                if (terraformingPrefabs.IsCreated) terraformingPrefabs.Dispose();
                if (brushPrefabs.IsCreated) brushPrefabs.Dispose();
            }
        }

        private int ApplyNuclearContamination(float3 center, float radius)
        {
            int success = 0;
            if (TryMaxPollutionMap("Game.Simulation.GroundPollutionSystem", center, radius)) success++;
            if (TryMaxPollutionMap("Game.Simulation.AirPollutionSystem", center, radius)) success++;
            if (TryMaxPollutionMap("Game.Simulation.WaterPollutionSystem", center, radius)) success++;
            return success;
        }

        private bool TryMaxPollutionMap(string systemTypeName, float3 center, float radius)
        {
            try
            {
                Type systemType = typeof(SimulationSystem).Assembly.GetType(systemTypeName, false);
                if (systemType == null)
                {
                    Mod.Log.Info($"Nuclear contamination: {systemTypeName} is not present in this game build.");
                    return false;
                }

                object pollutionSystem = null;
                System.Reflection.MethodInfo[] worldMethods = typeof(World).GetMethods();
                for (int i = 0; i < worldMethods.Length; i++)
                {
                    System.Reflection.MethodInfo candidate = worldMethods[i];
                    if (candidate.Name != "GetOrCreateSystemManaged" ||
                        !candidate.IsGenericMethodDefinition ||
                        candidate.GetGenericArguments().Length != 1 ||
                        candidate.GetParameters().Length != 0) continue;
                    pollutionSystem = candidate.MakeGenericMethod(systemType).Invoke(World, null);
                    break;
                }
                if (pollutionSystem == null) return false;

                System.Reflection.MethodInfo getMap = null;
                System.Reflection.MethodInfo[] methods = systemType.GetMethods();
                for (int i = 0; i < methods.Length; i++)
                {
                    System.Reflection.ParameterInfo[] parameters = methods[i].GetParameters();
                    if (methods[i].Name == "GetMap" && parameters.Length == 2 &&
                        parameters[0].ParameterType == typeof(bool) && parameters[1].IsOut)
                    {
                        getMap = methods[i];
                        break;
                    }
                }
                if (getMap == null) return false;

                object[] arguments = { false, default(JobHandle) };
                object map = getMap.Invoke(pollutionSystem, arguments);
                if (arguments[1] is JobHandle dependency) dependency.Complete();
                if (map == null) return false;

                Type mapType = map.GetType();
                System.Reflection.PropertyInfo lengthProperty = mapType.GetProperty("Length");
                System.Reflection.PropertyInfo itemProperty = mapType.GetProperty("Item");
                if (lengthProperty == null || itemProperty == null || !itemProperty.CanWrite) return false;
                int length = (int)lengthProperty.GetValue(map);

                System.Reflection.MethodInfo getCellCenter = systemType.GetMethod(
                    "GetCellCenter", System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.Static, null, new[] { typeof(int) }, null);
                if (getCellCenter == null) return false;

                int changed = 0;
                float radiusSquared = radius * radius;
                for (int index = 0; index < length; index++)
                {
                    object cellCenter = getCellCenter.Invoke(null, new object[] { index });
                    if (!TryReadHorizontalPosition(cellCenter, out float2 horizontal) ||
                        math.distancesq(horizontal, center.xz) > radiusSquared) continue;

                    object cell = itemProperty.GetValue(map, new object[] { index });
                    if (cell == null) continue;
                    System.Reflection.FieldInfo pollutionField = cell.GetType().GetField("m_Pollution");
                    if (pollutionField == null) continue;
                    pollutionField.SetValue(cell, MaximumNumericValue(pollutionField.FieldType));
                    itemProperty.SetValue(map, cell, new object[] { index });
                    changed++;
                }
                Mod.Log.Info($"Nuclear contamination maxed {changed} cells in {systemType.Name} " +
                    $"within {radius:F0}m of {FormatPosition(center)}");
                return changed > 0;
            }
            catch (Exception exception)
            {
                Mod.Log.Error($"Nuclear contamination write failed for {systemTypeName}; " +
                    $"the strike continued safely. {exception}");
                return false;
            }
        }

        private static bool TryReadHorizontalPosition(object value, out float2 position)
        {
            position = float2.zero;
            if (value is float3 point3)
            {
                position = point3.xz;
                return true;
            }
            if (value is float2 point2)
            {
                position = point2;
                return true;
            }
            return false;
        }

        private static object MaximumNumericValue(Type type)
        {
            if (type == typeof(byte)) return byte.MaxValue;
            if (type == typeof(sbyte)) return sbyte.MaxValue;
            if (type == typeof(short)) return short.MaxValue;
            if (type == typeof(ushort)) return ushort.MaxValue;
            if (type == typeof(int)) return int.MaxValue;
            if (type == typeof(uint)) return uint.MaxValue;
            if (type == typeof(float)) return 1f;
            if (type == typeof(double)) return 1d;
            return Activator.CreateInstance(type);
        }

        private bool CreateNuclearBlastDome(float3 position)
        {
            UnityEngine.GameObject visual = null;
            UnityEngine.Material material = null;
            try
            {
                visual = UnityEngine.GameObject.CreatePrimitive(UnityEngine.PrimitiveType.Sphere);
                visual.name = "GODs Nuclear White Blast Dome";
                visual.hideFlags = UnityEngine.HideFlags.DontSave;
                visual.transform.position = new UnityEngine.Vector3(position.x, position.y, position.z);
                visual.transform.localScale = UnityEngine.Vector3.one * 2f;
                UnityEngine.Collider collider = visual.GetComponent<UnityEngine.Collider>();
                if (collider != null) UnityEngine.Object.Destroy(collider);

                UnityEngine.Shader shader = UnityEngine.Shader.Find("HDRP/Unlit");
                if (shader == null) shader = UnityEngine.Shader.Find("Unlit/Transparent");
                if (shader == null) shader = UnityEngine.Shader.Find("Particles/Standard Unlit");
                if (shader == null) throw new InvalidOperationException("Nuclear dome shader unavailable.");
                material = new UnityEngine.Material(shader);
                material.name = "GODs Missile Nuclear Blast Dome Material";
                UnityEngine.Color color = new UnityEngine.Color(1f, 0.98f, 0.90f, 0.72f);
                if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
                if (material.HasProperty("_UnlitColor")) material.SetColor("_UnlitColor", color);
                if (material.HasProperty("_Color")) material.SetColor("_Color", color);
                if (material.HasProperty("_EmissiveColor"))
                    material.SetColor("_EmissiveColor", new UnityEngine.Color(8f, 7f, 5f, 1f));
                if (material.HasProperty("_SurfaceType")) material.SetFloat("_SurfaceType", 1f);
                if (material.HasProperty("_Surface")) material.SetFloat("_Surface", 1f);
                if (material.HasProperty("_SrcBlend")) material.SetFloat("_SrcBlend", 5f);
                if (material.HasProperty("_DstBlend")) material.SetFloat("_DstBlend", 10f);
                if (material.HasProperty("_ZWrite")) material.SetFloat("_ZWrite", 0f);
                material.SetOverrideTag("RenderType", "Transparent");
                material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                material.EnableKeyword("_ALPHABLEND_ON");
                material.renderQueue = 3100;
                visual.GetComponent<UnityEngine.MeshRenderer>().sharedMaterial = material;
                m_NuclearBlastDomes.Add(new NuclearBlastDome
                {
                    Visual = visual,
                    Material = material,
                    StartedUtc = DateTime.UtcNow
                });
                Enabled = true;
                return true;
            }
            catch (Exception exception)
            {
                Mod.Log.Error(exception, "Nuclear blast dome creation failed; damage continued.");
                if (visual != null) UnityEngine.Object.Destroy(visual);
                if (material != null) UnityEngine.Object.Destroy(material);
                return false;
            }
        }

        private void ProcessNuclearBlastDomes()
        {
            DateTime now = DateTime.UtcNow;
            for (int i = m_NuclearBlastDomes.Count - 1; i >= 0; i--)
            {
                NuclearBlastDome dome = m_NuclearBlastDomes[i];
                float progress = math.saturate((float)((now - dome.StartedUtc).TotalSeconds /
                    NuclearBlastDomeSeconds));
                if (dome.Visual == null || progress >= 1f)
                {
                    DestroyNuclearBlastDome(dome);
                    m_NuclearBlastDomes.RemoveAt(i);
                    continue;
                }
                float expansion = 1f - (1f - progress) * (1f - progress);
                float diameter = math.lerp(2f, NuclearInnerDestructionRadius * 2f, expansion);
                dome.Visual.transform.localScale = UnityEngine.Vector3.one * diameter;
                float alpha = 0.72f * (1f - progress);
                UnityEngine.Color color = new UnityEngine.Color(1f, 0.98f, 0.90f, alpha);
                if (dome.Material != null)
                {
                    if (dome.Material.HasProperty("_BaseColor")) dome.Material.SetColor("_BaseColor", color);
                    if (dome.Material.HasProperty("_UnlitColor")) dome.Material.SetColor("_UnlitColor", color);
                    if (dome.Material.HasProperty("_Color")) dome.Material.SetColor("_Color", color);
                }
            }
        }

        private static void DestroyNuclearBlastDome(NuclearBlastDome dome)
        {
            if (dome.Visual != null) UnityEngine.Object.Destroy(dome.Visual);
            if (dome.Material != null) UnityEngine.Object.Destroy(dome.Material);
        }

        private int IgniteMissileImpactRing(float3 center)
        {
            Entity eventSource = EnsureWildfireEventSource();
            if (eventSource == Entity.Null) return 0;
            NativeArray<Entity> entities = m_LightningFuelQuery.ToEntityArray(Allocator.Temp);
            NativeArray<Game.Objects.Transform> transforms =
                m_LightningFuelQuery.ToComponentDataArray<Game.Objects.Transform>(Allocator.Temp);
            float innerSquared = MissileImpactRadius * MissileImpactRadius;
            float outerSquared = MissileFireRadius * MissileFireRadius;
            int count = 0;
            try
            {
                for (int i = 0; i < entities.Length && count < MissileFireLimit; i++)
                {
                    Entity target = entities[i];
                    float distanceSquared = math.distancesq(transforms[i].m_Position.xz, center.xz);
                    if (distanceSquared <= innerSquared || distanceSquared > outerSquared) continue;
                    if (EntityManager.HasComponent<OnFire>(target)) continue;
                    QueueNativeIgnition(target, eventSource, 0.65f);
                    count++;
                }
            }
            finally
            {
                transforms.Dispose();
                entities.Dispose();
            }
            return count;
        }

        private static bool ShouldNuclearOuterDestroy(Entity entity, float distance)
        {
            uint roll = ((uint)entity.Index * 2654435761u) % 100u;
            return distance <= 360f ? roll < 65u : roll < 28u;
        }

        private int EraseNuclearCoreObjects(float3 center, float radius)
        {
            NativeArray<Entity> entities = m_MeteorObjectQuery.ToEntityArray(Allocator.Temp);
            NativeArray<Game.Objects.Transform> transforms =
                m_MeteorObjectQuery.ToComponentDataArray<Game.Objects.Transform>(Allocator.Temp);
            float radiusSquared = radius * radius;
            int count = 0;
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity entity = entities[i];
                    if (math.distancesq(transforms[i].m_Position.xz, center.xz) > radiusSquared)
                        continue;

                    // Raw deletion of network topology is not safe: nodes, edges
                    // and lanes must be removed through the native network tool
                    // so junction and pathfinding references are rebuilt.
                    if (EntityManager.HasComponent<Game.Net.Node>(entity) ||
                        EntityManager.HasComponent<Game.Net.Edge>(entity) ||
                        EntityManager.HasComponent<Game.Net.Lane>(entity) ||
                        EntityManager.HasComponent<PrefabData>(entity) ||
                        EntityManager.HasComponent<Temp>(entity)) continue;

                    // Deleted is CS2's native cleanup marker. Unlike a damage or
                    // collapse event, it removes the world object through its
                    // owning cleanup systems, including buildings, props, trees,
                    // parked/moving vehicles and their rendered instances.
                    if (!EntityManager.HasComponent<Deleted>(entity))
                    {
                        EntityManager.AddComponent<Deleted>(entity);
                        count++;
                    }
                }
            }
            finally { transforms.Dispose(); entities.Dispose(); }
            return count;
        }

        private int DestroyNuclearOuterBuildings(float3 center)
        {
            NativeArray<Entity> entities = m_EarthquakeBuildingQuery.ToEntityArray(Allocator.Temp);
            NativeArray<Game.Objects.Transform> transforms =
                m_EarthquakeBuildingQuery.ToComponentDataArray<Game.Objects.Transform>(Allocator.Temp);
            int count = 0;
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    float distance = math.distance(transforms[i].m_Position.xz, center.xz);
                    if (distance <= NuclearInnerDestructionRadius ||
                        distance > NuclearOuterDestructionRadius ||
                        !ShouldNuclearOuterDestroy(entities[i], distance)) continue;
                    QueueNativeDestruction(entities[i]);
                    count++;
                }
            }
            finally { transforms.Dispose(); entities.Dispose(); }
            return count;
        }

        private int DestroyNuclearOuterVehicles(float3 center)
        {
            NativeArray<Entity> entities = m_HailVehicleQuery.ToEntityArray(Allocator.Temp);
            int count = 0;
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity entity = entities[i];
                    if (!EntityManager.HasComponent<Game.Objects.Transform>(entity)) continue;
                    float distance = math.distance(EntityManager.GetComponentData<Game.Objects.Transform>(entity)
                        .m_Position.xz, center.xz);
                    if (distance <= NuclearInnerDestructionRadius ||
                        distance > NuclearOuterDestructionRadius ||
                        !ShouldNuclearOuterDestroy(entity, distance)) continue;
                    QueueVehicleDeletion(entity);
                    count++;
                }
            }
            finally { entities.Dispose(); }
            return count;
        }

        private int KillNuclearOuterCitizens(float3 center)
        {
            NativeArray<Entity> entities = m_ResidentTargetQuery.ToEntityArray(Allocator.Temp);
            NativeArray<Game.Objects.Transform> transforms =
                m_ResidentTargetQuery.ToComponentDataArray<Game.Objects.Transform>(Allocator.Temp);
            int count = 0;
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    float distance = math.distance(transforms[i].m_Position.xz, center.xz);
                    if (distance <= NuclearInnerDestructionRadius ||
                        distance > NuclearOuterDestructionRadius ||
                        !ShouldNuclearOuterDestroy(entities[i], distance)) continue;
                    if (QueueNativeCitizenDeath(entities[i], center)) count++;
                }
            }
            finally { transforms.Dispose(); entities.Dispose(); }
            return count;
        }

        private int DestroyNuclearOuterTrees(float3 center)
        {
            NativeArray<Entity> entities = m_LightningFuelQuery.ToEntityArray(Allocator.Temp);
            NativeArray<Game.Objects.Transform> transforms =
                m_LightningFuelQuery.ToComponentDataArray<Game.Objects.Transform>(Allocator.Temp);
            int count = 0;
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity entity = entities[i];
                    if (!EntityManager.HasComponent<Tree>(entity)) continue;
                    float distance = math.distance(transforms[i].m_Position.xz, center.xz);
                    if (distance <= NuclearInnerDestructionRadius ||
                        distance > NuclearOuterDestructionRadius ||
                        !ShouldNuclearOuterDestroy(entity, distance)) continue;
                    QueueNativeDestruction(entity);
                    count++;
                }
            }
            finally { transforms.Dispose(); entities.Dispose(); }
            return count;
        }

        private int IgniteNuclearFireField(float3 center)
        {
            Entity eventSource = EnsureWildfireEventSource();
            if (eventSource == Entity.Null) return 0;
            NativeArray<Entity> entities = m_LightningFuelQuery.ToEntityArray(Allocator.Temp);
            NativeArray<Game.Objects.Transform> transforms =
                m_LightningFuelQuery.ToComponentDataArray<Game.Objects.Transform>(Allocator.Temp);
            float innerSquared = NuclearInnerDestructionRadius * NuclearInnerDestructionRadius;
            float outerSquared = NuclearFireRadius * NuclearFireRadius;
            int count = 0;
            try
            {
                // Query order is intentionally used instead of randomness: every
                // eligible surviving fuel target in the ring ignites until the
                // performance cap is reached.
                for (int i = 0; i < entities.Length && count < NuclearFireLimit; i++)
                {
                    float distanceSquared = math.distancesq(transforms[i].m_Position.xz, center.xz);
                    if (distanceSquared <= innerSquared || distanceSquared > outerSquared) continue;
                    Entity target = entities[i];
                    float distance = math.sqrt(distanceSquared);
                    if (distance <= NuclearOuterDestructionRadius &&
                        ShouldNuclearOuterDestroy(target, distance)) continue;
                    if (EntityManager.HasComponent<OnFire>(target)) continue;
                    QueueNativeIgnition(target, eventSource, 1f);
                    count++;
                }
            }
            finally { transforms.Dispose(); entities.Dispose(); }
            return count;
        }

        private static void DestroyMissileVisual(UnityEngine.GameObject visual)
        {
            if (visual == null) return;
            UnityEngine.MeshFilter[] filters = visual.GetComponentsInChildren<UnityEngine.MeshFilter>(true);
            UnityEngine.Renderer[] renderers = visual.GetComponentsInChildren<UnityEngine.Renderer>(true);
            var meshes = new HashSet<UnityEngine.Mesh>();
            var materials = new HashSet<UnityEngine.Material>();
            var textures = new HashSet<UnityEngine.Texture>();
            for (int i = 0; i < filters.Length; i++)
            {
                UnityEngine.Mesh mesh = filters[i].sharedMesh;
                if (mesh != null && mesh.name.StartsWith("GODs Missile ")) meshes.Add(mesh);
            }
            for (int i = 0; i < renderers.Length; i++)
            {
                UnityEngine.Material material = renderers[i].sharedMaterial;
                if (material == null) continue;
                if (material.name.StartsWith("GODs Missile ") ||
                    material.name.StartsWith("GODs Meteor ")) materials.Add(material);
                if (material.HasProperty("_MainTex"))
                {
                    UnityEngine.Texture texture = material.GetTexture("_MainTex");
                    if (texture != null && texture.name.StartsWith("GODs Soft ")) textures.Add(texture);
                }
            }
            UnityEngine.Object.Destroy(visual);
            foreach (UnityEngine.Mesh mesh in meshes) UnityEngine.Object.Destroy(mesh);
            foreach (UnityEngine.Material material in materials) UnityEngine.Object.Destroy(material);
            foreach (UnityEngine.Texture texture in textures) UnityEngine.Object.Destroy(texture);
        }

        private void TriggerMeteorImpact(float3 impactPosition)
        {
            bool impactMark = CreateMeteorImpactMark(impactPosition);
            // Ignite surviving native fuel just outside the direct-destruction
            // radius. These are real initialized world objects, so CS2's own
            // wildfire renderer supplies the flames and smoke safely.
            int secondaryFires = IgniteMeteorImpactRing(impactPosition);
            int buildings = DestroyMeteorBuildings(impactPosition, MeteorImpactRadius);
            int vehicles = DestroyMeteorVehicles(impactPosition, MeteorImpactRadius);
            int citizens = KillMeteorCitizens(impactPosition, MeteorImpactRadius);
            int trees = DestroyMeteorTrees(impactPosition, MeteorImpactRadius);
            Mod.Log.Info($"Meteor impact at {FormatPosition(impactPosition)}: radius={MeteorImpactRadius:F0}m, " +
                $"buildings={buildings}, vehicles={vehicles}, citizens={citizens}, trees={trees}, " +
                $"secondaryFires={secondaryFires}, visualMode=dedicatedUnityMeteor, " +
                $"impactMark={impactMark}");
            TargetNotice?.Invoke($"METEOR IMPACT: {buildings} buildings and {vehicles} vehicles destroyed " +
                $"inside {MeteorImpactRadius:F0} m; {secondaryFires} nearby fires started.");
        }

        private bool CreateMeteorImpactMark(float3 position)
        {
            return CreateImpactMark(position, "SmMulch01-reference.png", "Meteor",
                MeteorImpactMarkSize, true);
        }

        private bool CreateMissileImpactMark(float3 position)
        {
            return CreateImpactMark(position, "Scorched Crater Blast Ring.png", "Missile",
                MissileImpactMarkSize, false);
        }

        private bool CreateNuclearImpactMark(float3 position)
        {
            return CreateImpactMark(position, "Nuclear Impact Crater.png", "Nuclear",
                NuclearImpactMarkSize, false);
        }

        private bool CreateImpactMark(float3 position, string resourceSuffix,
            string label, float size, bool removeWhiteBackground)
        {
            UnityEngine.GameObject visual = null;
            UnityEngine.Material material = null;
            UnityEngine.Texture2D texture = null;
            UnityEngine.Mesh impactMesh = null;
            try
            {
                byte[] imageBytes;
                System.Reflection.Assembly assembly = typeof(DisasterTargetToolSystem).Assembly;
                string resourceName = null;
                string[] resourceNames = assembly.GetManifestResourceNames();
                for (int i = 0; i < resourceNames.Length; i++)
                {
                    if (!resourceNames[i].EndsWith(resourceSuffix,
                        StringComparison.OrdinalIgnoreCase)) continue;
                    resourceName = resourceNames[i];
                    break;
                }
                if (resourceName == null)
                {
                    Mod.Log.Error($"{label} impact mark skipped: embedded '{resourceSuffix}' was not found.");
                    return false;
                }

                using (System.IO.Stream stream = assembly.GetManifestResourceStream(resourceName))
                {
                    if (stream == null) return false;
                    imageBytes = new byte[checked((int)stream.Length)];
                    int offset = 0;
                    while (offset < imageBytes.Length)
                    {
                        int read = stream.Read(imageBytes, offset, imageBytes.Length - offset);
                        if (read <= 0) break;
                        offset += read;
                    }
                    if (offset != imageBytes.Length) return false;
                }

                texture = new UnityEngine.Texture2D(2, 2, UnityEngine.TextureFormat.RGBA32, true);
                texture.name = $"GODs {label} Impact Mark Texture";
                if (!UnityEngine.ImageConversion.LoadImage(texture, imageBytes, false))
                    throw new InvalidOperationException("Unity could not decode the embedded impact PNG.");
                texture.wrapMode = UnityEngine.TextureWrapMode.Clamp;
                texture.filterMode = UnityEngine.FilterMode.Trilinear;

                UnityEngine.Color[] pixels = texture.GetPixels();
                for (int i = 0; i < pixels.Length; i++)
                {
                    UnityEngine.Color pixel = pixels[i];
                    if (removeWhiteBackground)
                    {
                        float whiteness = UnityEngine.Mathf.Min(pixel.r,
                            UnityEngine.Mathf.Min(pixel.g, pixel.b));
                        pixel.a *= UnityEngine.Mathf.Clamp01((1f - whiteness) * 5f);
                    }
                    else if (pixel.a <= 0.02f)
                    {
                        // Image-generation can leave a nearly invisible 1/255
                        // alpha haze around an otherwise transparent PNG.
                        pixel.a = 0f;
                    }
                    pixels[i] = pixel;
                }
                texture.SetPixels(pixels);
                texture.Apply(true, false);

                bool nuclearOverlay = string.Equals(label, "Nuclear", StringComparison.Ordinal);
                if (nuclearOverlay)
                {
                    visual = new UnityEngine.GameObject($"GODs {label} Impact Mark");
                    impactMesh = CreateAlphaShapedImpactMesh(texture, 96, 0.04f);
                    visual.AddComponent<UnityEngine.MeshFilter>().sharedMesh = impactMesh;
                    visual.AddComponent<UnityEngine.MeshRenderer>();
                }
                else
                {
                    visual = UnityEngine.GameObject.CreatePrimitive(UnityEngine.PrimitiveType.Quad);
                }
                visual.name = $"GODs {label} Impact Mark";
                visual.hideFlags = UnityEngine.HideFlags.DontSave;
                // A 715 m quad spans terrain with substantial height variation. At the
                // old 0.12 m offset, portions of the terrain/network surface could win
                // the depth test and cut a large wedge out of the crater artwork.
                visual.transform.position = new UnityEngine.Vector3(position.x,
                    position.y + (nuclearOverlay
                        ? (m_NuclearCraterEnabled ? -NuclearCraterDepth + 0.75f : 3.0f)
                        : 0.12f), position.z);
                visual.transform.rotation =
                    UnityEngine.Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f) *
                    UnityEngine.Quaternion.Euler(90f, 0f, 0f);
                visual.transform.localScale = new UnityEngine.Vector3(
                    size, size, 1f);
                UnityEngine.Collider collider = visual.GetComponent<UnityEngine.Collider>();
                if (collider != null) UnityEngine.Object.Destroy(collider);

                // Do not prefer the particle shader here. CS2 accepted its
                // texture but rendered the quad as opaque white in v0.12.6.
                // Nuclear uses HDRP/Unlit because Sprites/Default produced the
                // magenta unsupported-shader material in v0.14.5.
                bool nuclearImpactShader = string.Equals(label, "Nuclear", StringComparison.Ordinal);
                UnityEngine.Shader shader = nuclearImpactShader
                    ? UnityEngine.Shader.Find("HDRP/Unlit")
                    : UnityEngine.Shader.Find("Sprites/Default");
                if (shader == null) shader = UnityEngine.Shader.Find("Unlit/Transparent");
                if (shader == null)
                    shader = UnityEngine.Shader.Find("Legacy Shaders/Transparent/Diffuse");
                if (shader == null) shader = UnityEngine.Shader.Find("UI/Unlit/Transparent");
                if (shader == null) shader = UnityEngine.Shader.Find("Particles/Standard Unlit");
                if (shader == null) shader = UnityEngine.Shader.Find("HDRP/Unlit");
                if (shader == null) throw new InvalidOperationException(
                    "No compatible transparent shader is available for the impact mark.");

                material = new UnityEngine.Material(shader);
                material.name = $"GODs {label} Impact Mark Material";
                if (material.HasProperty("_MainTex")) material.SetTexture("_MainTex", texture);
                if (material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", texture);
                if (material.HasProperty("_BaseColorMap")) material.SetTexture("_BaseColorMap", texture);
                if (material.HasProperty("_UnlitColorMap")) material.SetTexture("_UnlitColorMap", texture);
                if (material.HasProperty("_Color"))
                    material.SetColor("_Color", UnityEngine.Color.white);
                if (material.HasProperty("_BaseColor"))
                    material.SetColor("_BaseColor", UnityEngine.Color.white);
                if (material.HasProperty("_Mode")) material.SetFloat("_Mode", 2f);
                if (material.HasProperty("_Surface")) material.SetFloat("_Surface", 1f);
                if (material.HasProperty("_SurfaceType")) material.SetFloat("_SurfaceType", 1f);
                if (material.HasProperty("_SrcBlend")) material.SetFloat("_SrcBlend", 5f);
                if (material.HasProperty("_DstBlend")) material.SetFloat("_DstBlend", 10f);
                if (material.HasProperty("_ZWrite")) material.SetFloat("_ZWrite", 0f);
                if (material.HasProperty("_Cull")) material.SetFloat("_Cull", 0f);
                if (material.HasProperty("_CullMode")) material.SetFloat("_CullMode", 0f);
                material.SetOverrideTag("RenderType", "Transparent");
                material.EnableKeyword("_ALPHABLEND_ON");
                material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                if (nuclearImpactShader)
                {
                    // HDRP/Unlit displayed fully transparent PNG pixels as an
                    // opaque black quad. Alpha clipping discards those pixels
                    // before HDRP shades them while preserving the crater art.
                    if (material.HasProperty("_AlphaCutoffEnable"))
                        material.SetFloat("_AlphaCutoffEnable", 1f);
                    if (material.HasProperty("_AlphaCutoff"))
                        material.SetFloat("_AlphaCutoff", 0.04f);
                    if (material.HasProperty("_AlphaCutoffShadow"))
                        material.SetFloat("_AlphaCutoffShadow", 0.04f);
                    material.EnableKeyword("_ALPHATEST_ON");
                }
                else
                {
                    material.DisableKeyword("_ALPHATEST_ON");
                }
                material.renderQueue = 3001;
                visual.GetComponent<UnityEngine.MeshRenderer>().sharedMaterial = material;

                DateTime removeUtc = DateTime.UtcNow.AddSeconds(MeteorImpactMarkSeconds);
                m_MeteorImpactMarks.Add(new MeteorImpactMark
                {
                    Visual = visual,
                    Material = material,
                    Texture = texture,
                    Mesh = impactMesh,
                    FadeUtc = removeUtc.AddSeconds(-MeteorImpactMarkFadeSeconds),
                    RemoveUtc = removeUtc
                });
                Enabled = true;
                Mod.Log.Info($"{label} impact mark created from embedded '{resourceName}' at " +
                    $"{FormatPosition(position)}; size={size:F1}m, " +
                    $"lifetime={MeteorImpactMarkSeconds:F0}s, shader='{shader.name}'");
                return true;
            }
            catch (Exception exception)
            {
                Mod.Log.Error($"{label} impact mark creation failed; impact damage continues. {exception}");
                if (visual != null) UnityEngine.Object.Destroy(visual);
                if (material != null) UnityEngine.Object.Destroy(material);
                if (texture != null) UnityEngine.Object.Destroy(texture);
                if (impactMesh != null) UnityEngine.Object.Destroy(impactMesh);
                return false;
            }
        }

        private static UnityEngine.Mesh CreateAlphaShapedImpactMesh(
            UnityEngine.Texture2D texture, int resolution, float alphaThreshold)
        {
            List<UnityEngine.Vector3> vertices = new List<UnityEngine.Vector3>();
            List<UnityEngine.Vector2> uvs = new List<UnityEngine.Vector2>();
            List<int> triangles = new List<int>();
            float step = 1f / resolution;

            for (int y = 0; y < resolution; y++)
            {
                float v0 = y * step;
                float v1 = (y + 1) * step;
                for (int x = 0; x < resolution; x++)
                {
                    float u0 = x * step;
                    float u1 = (x + 1) * step;
                    float alpha = texture.GetPixelBilinear((u0 + u1) * 0.5f,
                        (v0 + v1) * 0.5f).a;
                    if (alpha <= alphaThreshold) continue;

                    int first = vertices.Count;
                    vertices.Add(new UnityEngine.Vector3(u0 - 0.5f, v0 - 0.5f, 0f));
                    vertices.Add(new UnityEngine.Vector3(u1 - 0.5f, v0 - 0.5f, 0f));
                    vertices.Add(new UnityEngine.Vector3(u1 - 0.5f, v1 - 0.5f, 0f));
                    vertices.Add(new UnityEngine.Vector3(u0 - 0.5f, v1 - 0.5f, 0f));
                    uvs.Add(new UnityEngine.Vector2(u0, v0));
                    uvs.Add(new UnityEngine.Vector2(u1, v0));
                    uvs.Add(new UnityEngine.Vector2(u1, v1));
                    uvs.Add(new UnityEngine.Vector2(u0, v1));
                    triangles.Add(first);
                    triangles.Add(first + 2);
                    triangles.Add(first + 1);
                    triangles.Add(first);
                    triangles.Add(first + 3);
                    triangles.Add(first + 2);
                }
            }

            if (vertices.Count == 0)
                throw new InvalidOperationException("The nuclear impact PNG contains no visible alpha cells.");

            UnityEngine.Mesh mesh = new UnityEngine.Mesh
            {
                name = "GODs Nuclear Alpha-Shaped Impact Mesh"
            };
            mesh.SetVertices(vertices);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0, true);
            mesh.RecalculateBounds();
            return mesh;
        }

        private void ProcessMeteorImpactMarks()
        {
            DateTime now = DateTime.UtcNow;
            for (int i = m_MeteorImpactMarks.Count - 1; i >= 0; i--)
            {
                MeteorImpactMark mark = m_MeteorImpactMarks[i];
                if (mark.Visual == null || now >= mark.RemoveUtc)
                {
                    DestroyMeteorImpactMark(mark);
                    m_MeteorImpactMarks.RemoveAt(i);
                    continue;
                }
                if (now < mark.FadeUtc || mark.Material == null) continue;
                float alpha = math.saturate((float)((mark.RemoveUtc - now).TotalSeconds /
                    MeteorImpactMarkFadeSeconds));
                UnityEngine.Color color = new UnityEngine.Color(1f, 1f, 1f, alpha);
                if (mark.Material.HasProperty("_Color")) mark.Material.SetColor("_Color", color);
                if (mark.Material.HasProperty("_BaseColor")) mark.Material.SetColor("_BaseColor", color);
                if (mark.Material.HasProperty("_UnlitColor")) mark.Material.SetColor("_UnlitColor", color);
            }
        }

        private static void DestroyMeteorImpactMark(MeteorImpactMark mark)
        {
            if (mark.Visual != null) UnityEngine.Object.Destroy(mark.Visual);
            if (mark.Material != null) UnityEngine.Object.Destroy(mark.Material);
            if (mark.Texture != null) UnityEngine.Object.Destroy(mark.Texture);
            if (mark.Mesh != null) UnityEngine.Object.Destroy(mark.Mesh);
        }

        private int IgniteMeteorImpactRing(float3 center)
        {
            Entity eventSource = EnsureWildfireEventSource();
            if (eventSource == Entity.Null) return 0;

            NativeArray<Entity> entities = m_LightningFuelQuery.ToEntityArray(Allocator.Temp);
            NativeArray<Game.Objects.Transform> transforms =
                m_LightningFuelQuery.ToComponentDataArray<Game.Objects.Transform>(Allocator.Temp);
            float innerRadiusSquared = MeteorImpactRadius * MeteorImpactRadius;
            float outerRadiusSquared = MeteorFireSpreadRadius * MeteorFireSpreadRadius;
            int count = 0;
            try
            {
                for (int i = 0; i < entities.Length && count < MeteorImpactFireLimit; i++)
                {
                    Entity target = entities[i];
                    float distanceSquared = math.distancesq(transforms[i].m_Position.xz, center.xz);
                    if (distanceSquared <= innerRadiusSquared || distanceSquared > outerRadiusSquared) continue;
                    if (EntityManager.HasComponent<OnFire>(target)) continue;

                    QueueNativeIgnition(target, eventSource, 0.75f);
                    count++;
                }
            }
            finally
            {
                transforms.Dispose();
                entities.Dispose();
            }
            return count;
        }

        private Entity FindMeteorPrefab(string exactName, bool requireEffectData)
        {
            if (m_MeteorPrefabCache.TryGetValue(exactName, out Entity cached) &&
                cached != Entity.Null && EntityManager.Exists(cached)) return cached;

            NativeArray<Entity> prefabs = m_AllPrefabQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < prefabs.Length; i++)
                {
                    Entity prefab = prefabs[i];
                    if (!string.Equals(m_PrefabSystem.GetPrefabName(prefab), exactName,
                        StringComparison.OrdinalIgnoreCase)) continue;
                    if (requireEffectData && !EntityManager.HasComponent<EffectData>(prefab)) continue;

                    m_MeteorPrefabCache[exactName] = prefab;
                    if (exactName == "Boulder07") m_MeteorBoulderPrefab = prefab;
                    else if (exactName == "ExplosionTimedVFX") m_MeteorExplosionPrefab = prefab;
                    else if (exactName == "SmokeFireVFX") m_MeteorSmokePrefab = prefab;
                    return prefab;
                }
            }
            finally { prefabs.Dispose(); }
            return Entity.Null;
        }

        private Entity SpawnMeteorEffect(string prefabName, float3 position, float intensity)
        {
            Entity definition = Entity.Null;
            Entity owner = Entity.Null;
            try
            {
                Entity effectPrefab = FindMeteorPrefab(prefabName, true);
                if (effectPrefab == Entity.Null)
                {
                    Mod.Log.Error($"Meteor VFX prefab '{prefabName}' was not found; " +
                        "meteor flight will continue without this effect.");
                    return Entity.Null;
                }

                // EffectControlSystem does not render a bare EffectInstance. It
                // reads Game.Prefabs.Effect entries from the entity referenced
                // by an owner's PrefabRef, then records the live VFX in the
                // owner's EnabledEffect buffer. Keep this relationship separate
                // from Boulder07 so a rejected VFX can never break meteor flight.
                definition = EntityManager.CreateEntity();
                DynamicBuffer<Game.Prefabs.Effect> effects =
                    EntityManager.AddBuffer<Game.Prefabs.Effect>(definition);
                effects.Add(new Game.Prefabs.Effect
                {
                    m_Effect = effectPrefab,
                    m_Position = float3.zero,
                    m_Rotation = quaternion.identity,
                    m_Scale = new float3(1f),
                    m_Intensity = intensity,
                    m_ParentMesh = -1,
                    m_AnimationIndex = -1,
                    m_Procedural = false
                });

                owner = EntityManager.CreateEntity(
                    ComponentType.ReadWrite<PrefabRef>(),
                    ComponentType.ReadWrite<Game.Objects.Transform>(),
                    ComponentType.ReadWrite<EnabledEffect>(),
                    ComponentType.ReadWrite<Created>(),
                    ComponentType.ReadWrite<Updated>(),
                    ComponentType.ReadWrite<EffectsUpdated>());
                EntityManager.SetComponentData(owner, new PrefabRef { m_Prefab = definition });
                EntityManager.SetComponentData(owner, new Game.Objects.Transform
                {
                    m_Position = position,
                    m_Rotation = quaternion.identity
                });
                m_MeteorEffectDefinitions[owner] = definition;
                Mod.Log.Info($"Meteor native effect owner created: name='{prefabName}', " +
                    $"owner={owner.Index}:{owner.Version}, definition={definition.Index}:{definition.Version}, " +
                    $"intensity={intensity:F2}");
                return owner;
            }
            catch (Exception exception)
            {
                if (owner != Entity.Null && EntityManager.Exists(owner))
                    EntityManager.DestroyEntity(owner);
                if (definition != Entity.Null && EntityManager.Exists(definition))
                    EntityManager.DestroyEntity(definition);
                Mod.Log.Error($"Meteor VFX '{prefabName}' was rejected; continuing with the " +
                    $"stable textured meteor. {exception}");
                return Entity.Null;
            }
        }

        private void QueueMeteorImpactDecal(float3 impactPosition)
        {
            Entity prefab = EnsureLightningDecalPrefab();
            if (prefab == Entity.Null)
            {
                Mod.Log.Error("Meteor impact decal skipped because the native SmMulch01 prefab is unavailable.");
                return;
            }

            float3 decalPosition = impactPosition + new float3(0f, 0.03f, 0f);
            quaternion decalRotation = quaternion.identity;
            Entity definition = EntityManager.CreateEntity(
                typeof(CreationDefinition),
                typeof(ObjectDefinition),
                typeof(Updated));
            EntityManager.SetComponentData(definition, new CreationDefinition
            {
                // This deliberately restores the last configuration proven to
                // render SmMulch01. Permanent/Native flags made the creation
                // request succeed in logs but produced no visible decal.
                m_Prefab = prefab
            });
            EntityManager.SetComponentData(definition, new ObjectDefinition
            {
                m_ParentMesh = -1,
                m_Position = decalPosition,
                m_Rotation = decalRotation,
                m_LocalPosition = decalPosition,
                m_LocalRotation = decalRotation
            });
            Mod.Log.Info($"Meteor visible-mode SmMulch01 impact decal queued at " +
                $"{FormatPosition(decalPosition)} using definition " +
                $"{definition.Index}:{definition.Version}");
        }

        private void MoveMeteorEffect(Entity effect, float3 position, float intensity)
        {
            if (effect == Entity.Null || !EntityManager.Exists(effect) ||
                !EntityManager.HasComponent<Game.Objects.Transform>(effect)) return;

            Game.Objects.Transform transform =
                EntityManager.GetComponentData<Game.Objects.Transform>(effect);
            transform.m_Position = position;
            EntityManager.SetComponentData(effect, transform);
            if (m_MeteorEffectDefinitions.TryGetValue(effect, out Entity definition) &&
                EntityManager.Exists(definition) &&
                EntityManager.HasBuffer<Game.Prefabs.Effect>(definition))
            {
                DynamicBuffer<Game.Prefabs.Effect> effects =
                    EntityManager.GetBuffer<Game.Prefabs.Effect>(definition);
                if (effects.Length > 0)
                {
                    Game.Prefabs.Effect value = effects[0];
                    value.m_Intensity = intensity;
                    effects[0] = value;
                }
            }
            if (!EntityManager.HasComponent<Updated>(effect))
                EntityManager.AddComponent<Updated>(effect);
            if (!EntityManager.HasComponent<EffectsUpdated>(effect))
                EntityManager.AddComponent<EffectsUpdated>(effect);
            if (!EntityManager.HasComponent<BatchesUpdated>(effect))
                EntityManager.AddComponent<BatchesUpdated>(effect);
        }

        private void ScheduleMeteorEffectCleanup(Entity effect, double seconds)
        {
            if (effect == Entity.Null || !EntityManager.Exists(effect)) return;
            m_MeteorEffectCleanup.Add(new TimedEntityCleanup
            {
                Entity = effect,
                RemoveUtc = DateTime.UtcNow.AddSeconds(seconds)
            });
        }

        private void ScheduleNativeMeteorFireCleanup(Entity effect, double seconds)
        {
            if (effect == Entity.Null || !EntityManager.Exists(effect)) return;
            if (!EntityManager.HasComponent<Deleted>(effect))
                EntityManager.AddComponent<Deleted>(effect);
            if (!EntityManager.HasComponent<EffectsUpdated>(effect))
                EntityManager.AddComponent<EffectsUpdated>(effect);
            m_MeteorEffectCleanup.Add(new TimedEntityCleanup
            {
                Entity = effect,
                RemoveUtc = DateTime.UtcNow.AddSeconds(seconds + 0.25),
                DestroyNow = true
            });
        }

        private void ScheduleNativeMeteorFireCleanup(Entity[] effects, double seconds)
        {
            if (effects == null) return;
            for (int i = 0; i < effects.Length; i++)
                ScheduleNativeMeteorFireCleanup(effects[i], seconds);
        }

        private void MoveMeteorNativeFireCluster(Entity[] effects, float3 meteorPosition)
        {
            if (effects == null) return;
            int count = Math.Min(effects.Length, MeteorNativeFireOffsets.Length);
            for (int i = 0; i < count; i++)
                MoveMeteorEffect(effects[i], meteorPosition + MeteorNativeFireOffsets[i], 1f);
        }

        private void ProcessMeteorEffectCleanup()
        {
            for (int i = m_MeteorEffectCleanup.Count - 1; i >= 0; i--)
            {
                TimedEntityCleanup cleanup = m_MeteorEffectCleanup[i];
                if (DateTime.UtcNow < cleanup.RemoveUtc) continue;
                if (!cleanup.DestroyNow &&
                    m_MeteorEffectDefinitions.TryGetValue(cleanup.Entity, out Entity definition))
                {
                    if (EntityManager.Exists(cleanup.Entity))
                    {
                        if (!EntityManager.HasComponent<Deleted>(cleanup.Entity))
                            EntityManager.AddComponent<Deleted>(cleanup.Entity);
                        if (!EntityManager.HasComponent<EffectsUpdated>(cleanup.Entity))
                            EntityManager.AddComponent<EffectsUpdated>(cleanup.Entity);
                    }
                    m_MeteorEffectDefinitions.Remove(cleanup.Entity);
                    // Give EffectControlSystem one update to observe Deleted
                    // and release its native VFX handle before ECS teardown.
                    cleanup.Definition = definition;
                    cleanup.RemoveUtc = DateTime.UtcNow.AddSeconds(0.25);
                    cleanup.DestroyNow = true;
                    m_MeteorEffectCleanup[i] = cleanup;
                    continue;
                }
                if (EntityManager.Exists(cleanup.Entity)) EntityManager.DestroyEntity(cleanup.Entity);
                if (cleanup.Definition != Entity.Null && EntityManager.Exists(cleanup.Definition))
                    EntityManager.DestroyEntity(cleanup.Definition);
                m_MeteorEffectCleanup.RemoveAt(i);
            }
            if (!m_Armed && !m_HailstormActive && m_PendingVehicleCleanup.Count == 0 &&
                m_MeteorFlights.Count == 0 && m_MeteorEffectCleanup.Count == 0 &&
                m_MeteorImpactMarks.Count == 0 && m_MissileFlights.Count == 0 &&
                m_NuclearFlights.Count == 0 && m_NuclearBlastDomes.Count == 0) Enabled = false;
        }

        private int DestroyMeteorBuildings(float3 center, float radius)
        {
            NativeArray<Entity> entities = m_EarthquakeBuildingQuery.ToEntityArray(Allocator.Temp);
            NativeArray<Game.Objects.Transform> transforms =
                m_EarthquakeBuildingQuery.ToComponentDataArray<Game.Objects.Transform>(Allocator.Temp);
            int count = 0;
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    if (math.distancesq(transforms[i].m_Position.xz, center.xz) >
                        radius * radius) continue;
                    QueueNativeDestruction(entities[i]);
                    count++;
                }
            }
            finally { transforms.Dispose(); entities.Dispose(); }
            return count;
        }

        private int DestroyMeteorVehicles(float3 center, float radius)
        {
            NativeArray<Entity> entities = m_HailVehicleQuery.ToEntityArray(Allocator.Temp);
            int count = 0;
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity entity = entities[i];
                    if (!EntityManager.HasComponent<Game.Objects.Transform>(entity)) continue;
                    float3 position = EntityManager.GetComponentData<Game.Objects.Transform>(entity).m_Position;
                    if (math.distancesq(position.xz, center.xz) > radius * radius) continue;
                    QueueVehicleDeletion(entity);
                    count++;
                }
            }
            finally { entities.Dispose(); }
            return count;
        }

        private int KillMeteorCitizens(float3 center, float radius)
        {
            NativeArray<Entity> entities = m_ResidentTargetQuery.ToEntityArray(Allocator.Temp);
            NativeArray<Game.Objects.Transform> transforms =
                m_ResidentTargetQuery.ToComponentDataArray<Game.Objects.Transform>(Allocator.Temp);
            int count = 0;
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    if (math.distancesq(transforms[i].m_Position.xz, center.xz) >
                        radius * radius) continue;
                    if (QueueNativeCitizenDeath(entities[i], center)) count++;
                }
            }
            finally { transforms.Dispose(); entities.Dispose(); }
            return count;
        }

        private int DestroyMeteorTrees(float3 center, float radius)
        {
            NativeArray<Entity> entities = m_LightningFuelQuery.ToEntityArray(Allocator.Temp);
            NativeArray<Game.Objects.Transform> transforms =
                m_LightningFuelQuery.ToComponentDataArray<Game.Objects.Transform>(Allocator.Temp);
            int count = 0;
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity entity = entities[i];
                    if (!EntityManager.HasComponent<Tree>(entity)) continue;
                    if (math.distancesq(transforms[i].m_Position.xz, center.xz) >
                        radius * radius) continue;
                    QueueNativeDestruction(entity);
                    count++;
                }
            }
            finally { transforms.Dispose(); entities.Dispose(); }
            return count;
        }

        private static bool IsStrictRockName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            return name.IndexOf("Boulder", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.StartsWith("Rock", StringComparison.OrdinalIgnoreCase) ||
                name.IndexOf("Meteor", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Asteroid", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Comet", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsInternalMeteorAsset(string name)
        {
            return name.IndexOf(" Mesh", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("LOD", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("RenderPrefab", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Placeholder", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf(" UI", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsStrictMeteorEffectName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            return name.IndexOf("Explosion", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Explode", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Impact", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Fireball", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Shockwave", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Smoke", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void ProcessHailstorm()
        {
            if (!m_HailstormActive) return;
            if (DateTime.UtcNow >= m_HailstormEndsUtc)
            {
                StopHailstorm(true);
                TargetNotice?.Invoke("HAILSTORM ENDED. Previous weather restored.");
                return;
            }

            // Climate simulation continues to calculate ordinary weather while
            // the event runs, so hold the native hail/precipitation overrides.
            ApplyHailstormWeather();

            if (DateTime.UtcNow >= m_NextHailDamagePulseUtc)
            {
                ApplyHailDamagePulse();
                m_NextHailDamagePulseUtc = DateTime.UtcNow.AddSeconds(HailDamagePulseSeconds);
            }
        }

        private void ApplyHailDamagePulse()
        {
            m_HailDamagePulseCount++;
            int damagedVehicles = DamageRandomHailTargets(
                m_HailVehicleQuery, HailVehiclesPerPulse, 0.035f, true);
            int damagedBuildings = DamageRandomHailTargets(
                m_EarthquakeBuildingQuery, HailBuildingsPerPulse, 0.0025f, false);

            int injuredCitizens = 0;
            // One occasional injury every third pulse keeps the human impact
            // visible without turning a hailstorm into a mass-casualty event.
            if ((m_HailDamagePulseCount % 3) == 0)
                injuredCitizens = QueueRandomHailInjury() ? 1 : 0;

            Mod.Log.Info($"Hail damage pulse {m_HailDamagePulseCount}: " +
                $"vehicles={damagedVehicles}, buildings={damagedBuildings}, " +
                $"citizenInjuries={injuredCitizens}");
        }

        private int DamageRandomHailTargets(EntityQuery query, int limit,
            float damageMultiplier, bool allowRareSevereImpact)
        {
            if (query.IsEmptyIgnoreFilter) return 0;

            NativeArray<Entity> targets = query.ToEntityArray(Allocator.Temp);
            try
            {
                int count = math.min(limit, targets.Length);
                int damaged = 0;
                for (int i = 0; i < count; i++)
                {
                    int index = m_HailRandom.NextInt(i, targets.Length);
                    Entity temporary = targets[i];
                    targets[i] = targets[index];
                    targets[index] = temporary;

                    Entity target = targets[i];
                    if (!EntityManager.Exists(target) ||
                        !EntityManager.HasComponent<PrefabRef>(target))
                        continue;

                    float multiplier = damageMultiplier;
                    // Roughly one vehicle per three pulses receives a major
                    // hail impact. It may be wrecked, while ordinary hits remain light.
                    if (allowRareSevereImpact && i == 0 &&
                        (m_HailDamagePulseCount % 3) == 0)
                        multiplier = 0.55f;

                    QueueStructuralDamage(target, multiplier,
                        out float structuralIntegrity, out float damageAmount);
                    damaged++;
                }
                return damaged;
            }
            finally
            {
                targets.Dispose();
            }
        }

        private bool QueueRandomHailInjury()
        {
            if (m_ResidentTargetQuery.IsEmptyIgnoreFilter) return false;

            NativeArray<Entity> residents = m_ResidentTargetQuery.ToEntityArray(Allocator.Temp);
            try
            {
                // Try a few random candidates because some creature entities
                // may point to citizens already receiving healthcare.
                int attempts = math.min(8, residents.Length);
                for (int i = 0; i < attempts; i++)
                {
                    Entity creature = residents[m_HailRandom.NextInt(0, residents.Length)];
                    if (!EntityManager.Exists(creature) ||
                        !EntityManager.HasComponent<CreatureResident>(creature))
                        continue;

                    CreatureResident resident = EntityManager.GetComponentData<CreatureResident>(creature);
                    Entity citizen = resident.m_Citizen;
                    if (citizen == Entity.Null || !EntityManager.Exists(citizen) ||
                        !EntityManager.HasComponent<Citizen>(citizen) ||
                        EntityManager.HasComponent<HealthProblem>(citizen))
                        continue;

                    Entity injuryEvent = EntityManager.CreateEntity(
                        typeof(AddHealthProblem), typeof(Game.Common.Event));
                    EntityManager.SetComponentData(injuryEvent, new AddHealthProblem
                    {
                        m_Event = injuryEvent,
                        m_Target = citizen,
                        m_Flags = HealthProblemFlags.Injured | HealthProblemFlags.RequireTransport
                    });
                    return true;
                }
                return false;
            }
            finally
            {
                residents.Dispose();
            }
        }

        private void ApplyHailstormWeather()
        {
            m_ClimateSystem.hail = 1f;
            m_ClimateSystem.precipitation.overrideValue = 1f;
            m_ClimateSystem.precipitation.overrideState = true;
            m_ClimateSystem.cloudiness.overrideValue = 1f;
            m_ClimateSystem.cloudiness.overrideState = true;
        }

        private void StopHailstorm(bool notify)
        {
            if (!m_HailstormActive) return;

            m_HailstormActive = false;
            if (m_ClimateSystem != null)
            {
                m_ClimateSystem.hail = m_PreviousHail;
                m_ClimateSystem.precipitation.overrideValue = m_PreviousPrecipitationOverrideValue;
                m_ClimateSystem.precipitation.overrideState = m_PreviousPrecipitationOverrideState;
                m_ClimateSystem.cloudiness.overrideValue = m_PreviousCloudinessOverrideValue;
                m_ClimateSystem.cloudiness.overrideState = m_PreviousCloudinessOverrideState;
            }

            if (notify) TargetCancelled?.Invoke();
            if (!m_Armed && m_PendingVehicleCleanup.Count == 0 &&
                m_MeteorFlights.Count == 0 && m_MeteorEffectCleanup.Count == 0 &&
                m_MeteorImpactMarks.Count == 0 && m_MissileFlights.Count == 0 &&
                m_NuclearFlights.Count == 0 && m_NuclearBlastDomes.Count == 0) Enabled = false;
            Mod.Log.Info("Native citywide hailstorm stopped; previous weather restored.");
        }

        private void CancelTargeting(bool notify)
        {
            RestorePreviousTool();
            if (notify) TargetCancelled?.Invoke();
            Mod.Log.Info("Disaster target tool cancelled");
        }

        private void RestorePreviousTool()
        {
            m_Armed = false;
            m_HasTarget = false;
            m_LastEntity = Entity.Null;
            m_ArmedDisaster = string.Empty;
            ToolBaseSystem restore = m_PreviousTool;
            m_PreviousTool = null;
            m_ToolSystem.activeTool = restore != null && restore != this ? restore : m_DefaultToolSystem;
            Enabled = m_HailstormActive || m_PendingVehicleCleanup.Count > 0 ||
                m_MeteorFlights.Count > 0 || m_MeteorEffectCleanup.Count > 0 ||
                m_MeteorImpactMarks.Count > 0 || m_MissileFlights.Count > 0 ||
                m_NuclearFlights.Count > 0 || m_NuclearBlastDomes.Count > 0;
        }

        internal static string FormatPosition(float3 position) =>
            $"X {position.x:F1}  Y {position.y:F1}  Z {position.z:F1}";

        private void DrawTargetMarker(ref JobHandle inputDeps)
        {
            if (!m_HasTarget || m_OverlayRenderSystem == null) return;

            JobHandle overlayDeps;
            var job = new DrawTargetMarkerJob
            {
                Buffer = m_OverlayRenderSystem.GetBuffer(out overlayDeps),
                Position = m_LastPosition,
                PreviewDiameter = GetPreviewDiameter(m_ArmedDisaster),
                UseCrosshair = m_ReticleStyle == "crosshair" ||
                    (m_ReticleStyle == "auto" && UsesPrecisionReticle(m_ArmedDisaster))
            };

            inputDeps = job.Schedule(JobHandle.CombineDependencies(inputDeps, overlayDeps));
            m_OverlayRenderSystem.AddBufferWriter(inputDeps);
        }

        private struct DrawTargetMarkerJob : IJob
        {
            public OverlayRenderSystem.Buffer Buffer;
            public float3 Position;
            public float PreviewDiameter;
            public bool UseCrosshair;

            public void Execute()
            {
                var gold = new UnityEngine.Color(1f, 0.72f, 0.08f, 0.92f);
                var red = new UnityEngine.Color(1f, 0.22f, 0.08f, 0.98f);
                if (UseCrosshair)
                {
                    // Small separated rings form a precise, open cross without
                    // covering the exact terrain point beneath the cursor.
                    Buffer.DrawCircle(red, Position, 0.65f);
                    DrawCrossArm(gold, new float3(1f, 0f, 0f));
                    DrawCrossArm(gold, new float3(0f, 0f, 1f));
                }
                else
                {
                    Buffer.DrawCircle(gold, Position, PreviewDiameter);
                    Buffer.DrawCircle(red, Position, 12f);
                }
            }

            private void DrawCrossArm(UnityEngine.Color color, float3 axis)
            {
                Buffer.DrawCircle(color, Position + axis * 2.5f, 0.45f);
                Buffer.DrawCircle(color, Position - axis * 2.5f, 0.45f);
                Buffer.DrawCircle(color, Position + axis * 5f, 0.45f);
                Buffer.DrawCircle(color, Position - axis * 5f, 0.45f);
            }
        }

        private void TriggerLightningStrike(Entity hitEntity, float3 position,
            float3 surfacePosition)
        {
            if (m_ClimateRenderSystem == null)
            {
                Mod.Log.Error("Lightning strike failed: ClimateRenderSystem is unavailable.");
                return;
            }

            // The full-height vertical strike looks substantially more natural.
            // Keep explicit endpoints while restoring the 500 m cloud-scale origin.
            float3 lightningOrigin = position + new float3(0f, 500f, 0f);
            m_ClimateRenderSystem.LightningStrike(lightningOrigin, position, false);
            QueueLightningStrikeDecal(position, surfacePosition);
            QueueCitizenFatalitiesNear(hitEntity, position);
            TriggerLightningImpact(hitEntity, position);
        }

        private void TriggerLightningImpact(Entity hitEntity, float3 position)
        {
            Entity directTarget = ResolveLightningTarget(hitEntity);
            if (directTarget == Entity.Null)
            {
                Mod.Log.Info("Native lightning struck bare ground at " + FormatPosition(position) +
                    "; isolation build applied no splash damage.");
                return;
            }

            if (EntityManager.HasComponent<Building>(directTarget))
            {
                if (m_LightningBuildingStrikes.Count > 2048)
                    m_LightningBuildingStrikes.Clear();

                m_LightningBuildingStrikes.TryGetValue(directTarget, out int previousStrikes);
                int strikeCount = previousStrikes + 1;
                m_LightningBuildingStrikes[directTarget] = strikeCount;

                float damageMultiplier = strikeCount == 1 ? 0.12f : 0.15f;
                QueueStructuralDamage(directTarget, damageMultiplier,
                    out float structuralIntegrity, out float damageAmount);

                bool ignitionQueued = false;
                if (strikeCount >= 2 && !EntityManager.HasComponent<OnFire>(directTarget))
                {
                    Entity buildingFireSource = EnsureBuildingFireEventSource();
                    if (buildingFireSource != Entity.Null)
                    {
                        QueueNativeIgnition(directTarget, buildingFireSource, 0.15f);
                        ignitionQueued = true;
                    }
                }

                Mod.Log.Info($"Lightning building impact queued for " +
                    $"{directTarget.Index}:{directTarget.Version} at {FormatPosition(position)}: " +
                    $"strike={strikeCount}, integrity={structuralIntegrity:F3}, " +
                    $"multiplier={damageMultiplier:F3}, damage={damageAmount:F3}, " +
                    $"nativeBuildingFireQueued={ignitionQueued}");
                return;
            }

            if (EntityManager.HasComponent<Vehicle>(directTarget))
            {
                QueueStructuralDamage(directTarget, 0.35f,
                    out float structuralIntegrity, out float damageAmount);

                bool ignitionQueued = false;
                if (!EntityManager.HasComponent<OnFire>(directTarget))
                {
                    Entity buildingFireSource = EnsureBuildingFireEventSource();
                    if (buildingFireSource != Entity.Null)
                    {
                        QueueNativeIgnition(directTarget, buildingFireSource, 0.15f);
                        ignitionQueued = true;
                    }
                }

                Mod.Log.Info($"Lightning vehicle impact queued for " +
                    $"{directTarget.Index}:{directTarget.Version} at {FormatPosition(position)}: " +
                    $"integrity={structuralIntegrity:F3}, damage={damageAmount:F3}, " +
                    $"nativeVehicleFireQueued={ignitionQueued}");

                uint currentFrame = m_SimulationSystem != null ? m_SimulationSystem.frameIndex : 0u;
                m_PendingVehicleCleanup[directTarget] = currentFrame + VehicleCleanupDelayFrames;
                Enabled = true;
                Mod.Log.Info($"Lightning vehicle cleanup scheduled for " +
                    $"{directTarget.Index}:{directTarget.Version} at simulation frame " +
                    $"{m_PendingVehicleCleanup[directTarget]}");
                return;
            }

            if (EntityManager.HasComponent<CreatureResident>(directTarget))
            {
                // Citizen fatalities are handled once per strike by the capped
                // 3.5 m splash pass before direct object impact processing.
                return;
            }

            if (EntityManager.HasComponent<Tree>(directTarget))
            {
                Entity eventSource = EnsureWildfireEventSource();
                if (eventSource != Entity.Null && !EntityManager.HasComponent<OnFire>(directTarget))
                {
                    QueueNativeIgnition(directTarget, eventSource, 0.75f);
                    Mod.Log.Info($"Lightning isolation ignition queued for tree " +
                        $"{directTarget.Index}:{directTarget.Version} at {FormatPosition(position)}");
                }
            }
        }

        private void QueueCitizenFatalitiesNear(Entity hitEntity, float3 position)
        {
            Entity directCreature = ResolveLightningTarget(hitEntity);
            if (directCreature != Entity.Null &&
                !EntityManager.HasComponent<CreatureResident>(directCreature))
                directCreature = Entity.Null;

            int queued = 0;
            if (directCreature != Entity.Null &&
                QueueNativeCitizenDeath(directCreature, position))
                queued++;

            if (queued < LightningCitizenFatalityLimit)
            {
                NativeArray<Entity> residents = m_ResidentTargetQuery.ToEntityArray(Allocator.Temp);
                NativeArray<Game.Objects.Transform> transforms =
                    m_ResidentTargetQuery.ToComponentDataArray<Game.Objects.Transform>(Allocator.Temp);
                try
                {
                    float radiusSquared = LightningCitizenSplashRadius * LightningCitizenSplashRadius;
                    for (int i = 0;
                        i < residents.Length && queued < LightningCitizenFatalityLimit;
                        i++)
                    {
                        Entity creature = residents[i];
                        if (creature == directCreature) continue;

                        float3 offset = transforms[i].m_Position - position;
                        offset.y = 0f;
                        if (math.lengthsq(offset) > radiusSquared) continue;

                        if (QueueNativeCitizenDeath(creature, position)) queued++;
                    }
                }
                finally
                {
                    residents.Dispose();
                    transforms.Dispose();
                }
            }

            if (queued > 0)
            {
                Mod.Log.Info($"Lightning citizen splash queued {queued} fatality event(s) " +
                    $"within {LightningCitizenSplashRadius:F1} m at {FormatPosition(position)}");
            }
        }

        private bool QueueNativeCitizenDeath(Entity creature, float3 position)
        {
            if (!EntityManager.Exists(creature) ||
                !EntityManager.HasComponent<CreatureResident>(creature))
                return false;

            CreatureResident resident = EntityManager.GetComponentData<CreatureResident>(creature);
            Entity citizen = resident.m_Citizen;
            if (citizen == Entity.Null || !EntityManager.Exists(citizen) ||
                !EntityManager.HasComponent<Citizen>(citizen))
                return false;

            if (EntityManager.HasComponent<HealthProblem>(citizen))
            {
                HealthProblem existingProblem = EntityManager.GetComponentData<HealthProblem>(citizen);
                if ((((int)existingProblem.m_Flags) & 2) != 0) return false;
            }

            Citizen citizenData = EntityManager.GetComponentData<Citizen>(citizen);
            int previousHealth = citizenData.m_Health;
            citizenData.m_Health = 0;
            EntityManager.SetComponentData(citizen, citizenData);

            Entity deathEvent = EntityManager.CreateEntity(
                typeof(AddHealthProblem),
                typeof(Game.Common.Event));
            EntityManager.SetComponentData(deathEvent, new AddHealthProblem
            {
                m_Event = deathEvent,
                m_Target = citizen,
                m_Flags = (HealthProblemFlags)10
            });

            Mod.Log.Info($"Native lightning citizen death event queued: creature " +
                $"{creature.Index}:{creature.Version}, citizen " +
                $"{citizen.Index}:{citizen.Version} at {FormatPosition(position)}, " +
                $"health={previousHealth}->0, nativeHealthFlags=10, event=" +
                $"{deathEvent.Index}:{deathEvent.Version}");
            return true;
        }

        private Entity EnsureLightningDecalPrefab()
        {
            if (m_LightningDecalPrefab != Entity.Null &&
                EntityManager.Exists(m_LightningDecalPrefab))
                return m_LightningDecalPrefab;

            if (m_LightningDecalLookupFailed) return Entity.Null;

            NativeArray<Entity> prefabs = m_AllPrefabQuery.ToEntityArray(Allocator.Temp);
            int prefabCount = prefabs.Length;
            try
            {
                Entity selectedPrefab = Entity.Null;
                string selectedName = string.Empty;
                int selectedScore = int.MinValue;
                int matchingCandidates = 0;

                for (int i = 0; i < prefabs.Length; i++)
                {
                    Entity prefab = prefabs[i];
                    string prefabName = m_PrefabSystem.GetPrefabName(prefab);
                    string normalizedName = NormalizePrefabName(prefabName);
                    if (normalizedName.IndexOf("smmulch01",
                        StringComparison.OrdinalIgnoreCase) < 0) continue;

                    matchingCandidates++;

                    // A RenderPrefab is an internal graphics dependency, not a
                    // placeable object. Creating one through ObjectDefinition is
                    // accepted but produces no visible decal.
                    if (normalizedName.IndexOf("renderprefab",
                        StringComparison.OrdinalIgnoreCase) >= 0) continue;

                    // Asset packs also register UI/editor placeholders. They
                    // carry StaticObjectData and can outscore the real object,
                    // but do not contain the intended runtime decal material.
                    if (normalizedName.IndexOf("placeholder",
                        StringComparison.OrdinalIgnoreCase) >= 0) continue;

                    int score = 0;
                    if (EntityManager.HasComponent<StaticObjectData>(prefab)) score += 100;
                    if (string.Equals(normalizedName, "smmulch01",
                        StringComparison.OrdinalIgnoreCase)) score += 50;
                    if (normalizedName.IndexOf("decal",
                        StringComparison.OrdinalIgnoreCase) >= 0) score += 25;

                    if (score <= selectedScore) continue;
                    selectedPrefab = prefab;
                    selectedName = prefabName;
                    selectedScore = score;
                }

                if (selectedPrefab != Entity.Null)
                {
                    m_LightningDecalPrefab = selectedPrefab;
                    Mod.Log.Info($"Lightning strike placeable decal selected: '{selectedName}' " +
                        $"({selectedPrefab.Index}:{selectedPrefab.Version}), " +
                        $"score={selectedScore}, matchingCandidates={matchingCandidates}, " +
                        $"hasStaticObjectData=" +
                        $"{EntityManager.HasComponent<StaticObjectData>(selectedPrefab)}");
                    return selectedPrefab;
                }

                Mod.Log.Error($"Found {matchingCandidates} SmMulch01 prefab candidate(s), but " +
                    "none was a placeable non-RenderPrefab decal.");
            }
            finally
            {
                prefabs.Dispose();
            }

            m_LightningDecalLookupFailed = true;
            Mod.Log.Error($"Placeable prefab 'SmMulch01' was not found among {prefabCount} " +
                "registered prefabs; lightning decal disabled for this session.");
            return Entity.Null;
        }

        private static string NormalizePrefabName(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;

            char[] characters = new char[value.Length];
            int length = 0;
            for (int i = 0; i < value.Length; i++)
            {
                char character = value[i];
                if (!char.IsLetterOrDigit(character)) continue;
                characters[length++] = char.ToLowerInvariant(character);
            }
            return new string(characters, 0, length);
        }

        private void QueueLightningStrikeDecal(float3 hitPosition, float3 surfacePosition)
        {
            Entity prefab = EnsureLightningDecalPrefab();
            if (prefab == Entity.Null) return;

            // ControlPoint.m_HitPosition may resolve to terrain beneath roads.
            // m_Position is the tool's resolved placement surface and is the
            // correct anchor for decals on networks and sidewalks.
            float3 placementSurface = math.all(math.isfinite(surfacePosition))
                ? surfacePosition
                : hitPosition;
            float3 decalPosition = placementSurface + new float3(0f, 0.08f, 0f);
            quaternion decalRotation = quaternion.identity;
            Entity definition = EntityManager.CreateEntity(
                typeof(CreationDefinition),
                typeof(ObjectDefinition),
                typeof(Updated));

            EntityManager.SetComponentData(definition, new CreationDefinition
            {
                m_Prefab = prefab,
                // Isolation test: Permanent is the only behavior change from
                // the visible 0.8.3 preview. Do not combine it with Native or
                // object-definition overrides until runtime behavior proves
                // which flag CS2's decal pipeline accepts.
                m_Flags = CreationFlags.Permanent
            });
            EntityManager.SetComponentData(definition, new ObjectDefinition
            {
                m_ParentMesh = -1,
                m_Position = decalPosition,
                m_Rotation = decalRotation,
                m_LocalPosition = decalPosition,
                m_LocalRotation = decalRotation
            });

            Mod.Log.Info($"Permanent-only lightning SmMulch01 decal queued at " +
                $"{FormatPosition(decalPosition)} using definition " +
                $"{definition.Index}:{definition.Version}; rawHit=" +
                $"{FormatPosition(hitPosition)}, resolvedSurface=" +
                $"{FormatPosition(surfacePosition)}, surfaceDeltaY=" +
                $"{surfacePosition.y - hitPosition.y:F3}");
        }

        private Entity EnsureBuildingFireEventSource()
        {
            if (m_BuildingFireEventSource != Entity.Null &&
                EntityManager.Exists(m_BuildingFireEventSource))
                return m_BuildingFireEventSource;

            NativeArray<Entity> prefabs = m_FirePrefabQuery.ToEntityArray(Allocator.Temp);
            try
            {
                Entity selectedPrefab = Entity.Null;
                for (int i = 0; i < prefabs.Length; i++)
                {
                    Entity prefab = prefabs[i];
                    if (string.Equals(m_PrefabSystem.GetPrefabName(prefab),
                        "Building Fire", StringComparison.OrdinalIgnoreCase))
                    {
                        selectedPrefab = prefab;
                        break;
                    }
                }

                if (selectedPrefab == Entity.Null)
                {
                    Mod.Log.Error("FireData prefab named 'Building Fire' was not found; lightning building ignition skipped.");
                    return Entity.Null;
                }

                m_BuildingFireEventSource = EntityManager.CreateEntity(typeof(PrefabRef));
                EntityManager.SetComponentData(m_BuildingFireEventSource, new PrefabRef
                {
                    m_Prefab = selectedPrefab
                });

                FireData selectedData = EntityManager.GetComponentData<FireData>(selectedPrefab);
                Mod.Log.Info($"Native 'Building Fire' FireData selected for lightning ignition: prefab {selectedPrefab.Index}:{selectedPrefab.Version}, " +
                    $"start={selectedData.m_StartIntensity:F3}, escalation={selectedData.m_EscalationRate:F3}, " +
                    $"spreadProbability={selectedData.m_SpreadProbability:F3}, spreadRange={selectedData.m_SpreadRange:F1}");
                return m_BuildingFireEventSource;
            }
            finally
            {
                prefabs.Dispose();
            }
        }

        private string GetTargetDetails(Entity hitEntity)
        {
            Entity target = ResolveLightningTarget(hitEntity);
            if (target == Entity.Null)
                return "Aim at a building or moving vehicle.";

            if (EntityManager.HasComponent<Vehicle>(target))
            {
                string vehicleFireState = EntityManager.HasComponent<OnFire>(target) ? "BURNING" : "not burning";
                return $"VEHICLE TARGET · {vehicleFireState}";
            }

            if (EntityManager.HasComponent<CreatureResident>(target))
                return "CITIZEN TARGET · DIRECT LIGHTNING IS FATAL";

            if (!EntityManager.HasComponent<Building>(target))
                return "Aim at a building or moving vehicle.";

            m_LightningBuildingStrikes.TryGetValue(target, out int strikes);
            int trackedDamagePercent = strikes <= 0 ? 0 : math.min(99, 12 + (strikes - 1) * 15);
            string fireState = EntityManager.HasComponent<OnFire>(target) ? "BURNING" : "not burning";
            return $"BUILDING DAMAGE: {trackedDamagePercent}% · LIGHTNING HITS: {strikes} · {fireState}";
        }

        private Entity ResolveLightningTarget(Entity entity)
        {
            // Moving-object hits and static sub-objects can both resolve through
            // owner chains. Stop at the first supported physical target.
            for (int i = 0; i < 8 && entity != Entity.Null && EntityManager.Exists(entity); i++)
            {
                bool isTarget = EntityManager.HasComponent<Vehicle>(entity) ||
                    EntityManager.HasComponent<CreatureResident>(entity) ||
                    EntityManager.HasComponent<Tree>(entity) ||
                    EntityManager.HasComponent<Building>(entity);
                if (isTarget && EntityManager.HasComponent<PrefabRef>(entity)) return entity;

                if (!EntityManager.HasComponent<Owner>(entity)) break;
                entity = EntityManager.GetComponentData<Owner>(entity).m_Owner;
            }
            return Entity.Null;
        }

        private void ProcessPendingVehicleCleanup()
        {
            if (m_PendingVehicleCleanup.Count == 0) return;

            uint currentFrame = m_SimulationSystem != null ? m_SimulationSystem.frameIndex : 0u;
            m_CompletedVehicleCleanup.Clear();
            foreach (KeyValuePair<Entity, uint> pending in m_PendingVehicleCleanup)
            {
                if (currentFrame < pending.Value) continue;

                Entity vehicle = pending.Key;
                if (EntityManager.Exists(vehicle) &&
                    EntityManager.HasComponent<Vehicle>(vehicle) &&
                    !EntityManager.HasComponent<Deleted>(vehicle))
                {
                    QueueVehicleDeletion(vehicle);
                    Mod.Log.Info($"Lightning vehicle deletion queued for " +
                        $"{vehicle.Index}:{vehicle.Version} at simulation frame {currentFrame}");
                }
                m_CompletedVehicleCleanup.Add(vehicle);
            }

            for (int i = 0; i < m_CompletedVehicleCleanup.Count; i++)
                m_PendingVehicleCleanup.Remove(m_CompletedVehicleCleanup[i]);

            if (!m_Armed && !m_HailstormActive && m_PendingVehicleCleanup.Count == 0 &&
                m_MeteorFlights.Count == 0 && m_MeteorEffectCleanup.Count == 0 &&
                m_MeteorImpactMarks.Count == 0 && m_MissileFlights.Count == 0 &&
                m_NuclearFlights.Count == 0 && m_NuclearBlastDomes.Count == 0) Enabled = false;
        }

        private void QueueStructuralDamage(Entity building, float integrityMultiplier,
            out float structuralIntegrity, out float damageAmount)
        {
            PrefabRef prefabRef = EntityManager.GetComponentData<PrefabRef>(building);
            structuralIntegrity = 100f;
            if (prefabRef.m_Prefab != Entity.Null &&
                EntityManager.HasComponent<DestructibleObjectData>(prefabRef.m_Prefab))
            {
                structuralIntegrity = math.max(1f,
                    EntityManager.GetComponentData<DestructibleObjectData>(prefabRef.m_Prefab)
                        .m_StructuralIntegrity);
            }

            damageAmount = structuralIntegrity * integrityMultiplier;

            Entity damageEvent = EntityManager.CreateEntity(
                typeof(Damage),
                typeof(Game.Common.Event));
            EntityManager.SetComponentData(damageEvent,
                new Damage(building, new float3(damageAmount, 0f, 0f)));
        }

        private void QueueVehicleDeletion(Entity vehicle)
        {
            // Destroy only converts vehicles into wrecks. Deleted is the native
            // lifecycle marker consumed by vehicle cleanup/despawn systems.
            if (!EntityManager.HasComponent<Deleted>(vehicle))
                EntityManager.AddComponent<Deleted>(vehicle);
        }

        private bool TriggerWildfire(Entity hitEntity, float3 position)
        {
            Entity target = ResolveIgnitionTarget(hitEntity);
            if (target == Entity.Null)
            {
                const string message = "WILDFIRE NEEDS FUEL: aim directly at a tree or building.";
                TargetNotice?.Invoke(message);
                Mod.Log.Info(message + " Click was at " + FormatPosition(position));
                return false;
            }

            if (EntityManager.HasComponent<OnFire>(target))
            {
                const string message = "That target is already burning. Choose another tree or building.";
                TargetNotice?.Invoke(message);
                Mod.Log.Info(message);
                return false;
            }

            Entity eventSource = EnsureWildfireEventSource();
            if (eventSource == Entity.Null)
            {
                const string message = "WILDFIRE FAILED: no native FireData prefab is available.";
                TargetNotice?.Invoke(message);
                Mod.Log.Error(message);
                return false;
            }

            QueueNativeIgnition(target, eventSource, 0.75f);

            Mod.Log.Info($"Native wildfire ignition queued for entity {target.Index}:{target.Version} at {FormatPosition(position)}");
            return true;
        }

        private void QueueNativeIgnition(Entity target, Entity eventSource, float intensity)
        {
            // Native IgniteSystem explicitly queries for both Ignite and
            // Game.Common.Event. Without the marker, the request is never read.
            Entity igniteEvent = EntityManager.CreateEntity(
                typeof(Ignite),
                typeof(Game.Common.Event));
            EntityManager.SetComponentData(igniteEvent, new Ignite
            {
                m_Event = eventSource,
                m_Target = target,
                m_Intensity = intensity,
                m_RequestFrame = m_SimulationSystem != null ? m_SimulationSystem.frameIndex : 0u
            });
        }

        private Entity EnsureWildfireEventSource()
        {
            if (m_WildfireEventSource != Entity.Null && EntityManager.Exists(m_WildfireEventSource))
                return m_WildfireEventSource;

            NativeArray<Entity> prefabs = m_FirePrefabQuery.ToEntityArray(Allocator.Temp);
            try
            {
                Entity selectedPrefab = Entity.Null;
                float selectedScore = float.MinValue;

                for (int i = 0; i < prefabs.Length; i++)
                {
                    Entity prefab = prefabs[i];
                    FireData data = EntityManager.GetComponentData<FireData>(prefab);
                    if (data.m_StartIntensity <= 0f) continue;

                    // Prefer the native fire definition with the strongest
                    // spreading behavior; this avoids accidentally choosing a
                    // weak one-shot vehicle-fire definition when several exist.
                    float score = data.m_SpreadProbability * math.max(1f, data.m_SpreadRange);
                    if (score <= selectedScore) continue;
                    selectedScore = score;
                    selectedPrefab = prefab;
                }

                if (selectedPrefab == Entity.Null) return Entity.Null;

                m_WildfireEventSource = EntityManager.CreateEntity(typeof(PrefabRef));
                EntityManager.SetComponentData(m_WildfireEventSource, new PrefabRef
                {
                    m_Prefab = selectedPrefab
                });

                FireData selectedData = EntityManager.GetComponentData<FireData>(selectedPrefab);
                Mod.Log.Info($"Wildfire FireData selected: prefab {selectedPrefab.Index}:{selectedPrefab.Version}, " +
                    $"start={selectedData.m_StartIntensity:F3}, escalation={selectedData.m_EscalationRate:F3}, " +
                    $"spreadProbability={selectedData.m_SpreadProbability:F3}, spreadRange={selectedData.m_SpreadRange:F1}");
                return m_WildfireEventSource;
            }
            finally
            {
                prefabs.Dispose();
            }
        }

        private Entity ResolveIgnitionTarget(Entity entity)
        {
            // Static-object raycasts may return an attached sub-object. Walk a
            // short owner chain and accept only native fire-capable object kinds.
            for (int i = 0; i < 8 && entity != Entity.Null && EntityManager.Exists(entity); i++)
            {
                bool isFuel = EntityManager.HasComponent<Tree>(entity) ||
                    EntityManager.HasComponent<Building>(entity);
                if (isFuel && EntityManager.HasComponent<PrefabRef>(entity)) return entity;

                if (!EntityManager.HasComponent<Owner>(entity)) break;
                entity = EntityManager.GetComponentData<Owner>(entity).m_Owner;
            }
            return Entity.Null;
        }

        private bool TriggerEarthquake(float3 epicenter)
        {
            const float radius = 500f;
            const float severeRadius = 125f;
            const float moderateRadius = 300f;
            const int maximumDamageTargets = 4096;
            const int maximumCollapseTargets = 1024;

            NativeArray<Entity> buildings = m_EarthquakeBuildingQuery.ToEntityArray(Allocator.Temp);
            NativeArray<Game.Objects.Transform> transforms =
                m_EarthquakeBuildingQuery.ToComponentDataArray<Game.Objects.Transform>(Allocator.Temp);

            int damagedCount = 0;
            int collapsedCount = 0;
            Entity closestBuilding = Entity.Null;
            float closestDistance = float.MaxValue;
            try
            {
                for (int i = 0; i < buildings.Length; i++)
                {
                    Entity building = buildings[i];
                    float2 offset = transforms[i].m_Position.xz - epicenter.xz;
                    float distance = math.length(offset);
                    if (distance > radius) continue;

                    if (distance < closestDistance)
                    {
                        closestDistance = distance;
                        closestBuilding = building;
                    }

                    if (damagedCount < maximumDamageTargets)
                    {
                        PrefabRef prefabRef = EntityManager.GetComponentData<PrefabRef>(building);
                        float structuralIntegrity = 100f;
                        if (prefabRef.m_Prefab != Entity.Null &&
                            EntityManager.HasComponent<DestructibleObjectData>(prefabRef.m_Prefab))
                        {
                            structuralIntegrity = math.max(1f,
                                EntityManager.GetComponentData<DestructibleObjectData>(prefabRef.m_Prefab)
                                    .m_StructuralIntegrity);
                        }

                        // X is CS2's native structural/weather damage channel. The
                        // 500 m falloff leaves repairable damage in the outer zone
                        // while the inner zones also receive explicit collapse events.
                        float falloff = math.saturate(1f - distance / radius);
                        float variation = 0.85f + (building.Index % 31) * 0.01f;
                        float damageAmount = structuralIntegrity *
                            math.lerp(0.15f, 1.50f, falloff) * variation;

                        Entity damageEvent = EntityManager.CreateEntity(
                            typeof(Damage),
                            typeof(Game.Common.Event));
                        EntityManager.SetComponentData(damageEvent,
                            new Damage(building, new float3(damageAmount, 0f, 0f)));
                        damagedCount++;
                    }

                    // Use a stable entity-derived roll so repeated runs are
                    // predictable: about 80% collapse in the severe core and 30%
                    // in the middle zone. The outer zone receives damage only.
                    uint collapseRoll = ((uint)building.Index * 2654435761u) % 100u;
                    bool shouldCollapse = distance <= severeRadius
                        ? collapseRoll < 80u
                        : distance <= moderateRadius && collapseRoll < 30u;
                    if (shouldCollapse && collapsedCount < maximumCollapseTargets)
                    {
                        QueueNativeDestruction(building);
                        collapsedCount++;
                    }
                }

                // Sparse areas must still give visible feedback when at least one
                // building lies inside the earthquake radius.
                if (damagedCount > 0 && collapsedCount == 0 && closestBuilding != Entity.Null)
                {
                    QueueNativeDestruction(closestBuilding);
                    collapsedCount = 1;
                }
            }
            finally
            {
                transforms.Dispose();
                buildings.Dispose();
            }

            if (damagedCount == 0)
            {
                const string message = "EARTHQUAKE FOUND NO BUILDINGS inside the target radius.";
                TargetNotice?.Invoke(message);
                Mod.Log.Info(message + " Epicenter was " + FormatPosition(epicenter));
                return false;
            }

            Mod.Log.Info($"Wide-scale earthquake queued damage for {damagedCount} buildings and visible collapse for {collapsedCount} buildings within {radius:F0}m of {FormatPosition(epicenter)}");
            return true;
        }

        private void QueueNativeDestruction(Entity target)
        {
            Entity destroyEvent = EntityManager.CreateEntity(
                typeof(Game.Objects.Destroy),
                typeof(Game.Common.Event));
            EntityManager.SetComponentData(destroyEvent, new Game.Objects.Destroy
            {
                m_Event = Entity.Null,
                m_Object = target
            });
        }

        private static float GetPreviewDiameter(string disasterId)
        {
            switch (disasterId)
            {
                case "lightning": return 28f;
                case "wildfire": return 90f;
                case "meteor": return MeteorImpactRadius * 2f;
                case "earthquake": return 1000f;
                case "missile": return MissileImpactRadius * 2f;
                case "nuclear": return NuclearInnerDestructionRadius * 2f;
                default: return 60f;
            }
        }

        private static bool UsesPrecisionReticle(string disasterId) =>
            disasterId == "lightning" || disasterId == "missile";
    }
}
