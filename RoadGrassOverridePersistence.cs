using System.Collections.Generic;
using Colossal.Entities;
using Colossal.Serialization.Entities;
using Game;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Rendering;
using Unity.Collections;
using Unity.Entities;
using UnityEngine.Scripting;

namespace RoadGrass
{
    internal struct RoadGrassPendingOverride : IComponentData { }
    /// <summary>
    /// Durable descriptor for the ordinary Median and Road Edge restore modes.
    /// The generated composition mesh is runtime-only; this record is the part
    /// that is allowed to enter the city save.
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct RoadGrassSavedOverride : IBufferElementData, ISerializable
    {
        internal Entity Edge;
        internal int Areas;

        internal RoadGrassSavedOverride(Entity edge, RoadGrassArea areas)
        {
            Edge = edge;
            Areas = (int)areas;
        }

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(1);
            writer.Write(Edge);
            writer.Write(Areas);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out int _);
            reader.Read(out Edge);
            reader.Read(out Areas);
        }
    }

    /// <summary>
    /// Owns the city-level save records and replays them after loading. Work is
    /// dormant outside load hydration, a tool action, or save serialization.
    /// </summary>
    [Preserve]
    internal partial class RoadGrassOverridePersistenceSystem : GameSystemBase
    {
        private const RoadGrassArea PersistedAreaMask =
            RoadGrassArea.Median | RoadGrassArea.RoadEdge;
        private const int RetryInterval = 4;
        private const int LoadSettleQuietFrames = 3;
        private const int LoadSettleMaximumFrames = 240;

        private EntityQuery m_CityQuery;
        private EntityQuery m_ChangedPendingQuery;
        private readonly Dictionary<Entity, RoadGrassArea> m_Pending = new();
        private readonly List<Entity> m_Completed = new();
        private readonly List<KeyValuePair<Entity, RoadGrassArea>> m_Work = new();
        private bool m_GameReady;
        private bool m_NeedsHydration;
        private bool m_SuspendedForSave;
        private bool m_WaitingForLoadSettle;
        private int m_RetryCountdown;
        private int m_LoadSettleQuietFrames;
        private int m_LoadSettleStartFrame;
        private int m_LoadSettleLastFrame;
        private int m_RetryPasses;

        internal static RoadGrassOverridePersistenceSystem Instance { get; private set; }

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            Instance = this;
            m_CityQuery = GetEntityQuery(
                ComponentType.ReadOnly<Game.City.City>());
            m_ChangedPendingQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<RoadGrassPendingOverride>() },
                Any = new[] { ComponentType.ReadOnly<Updated>(), ComponentType.ReadOnly<Deleted>() }
            });
        }

        [Preserve]
        protected override void OnGamePreload(Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            RoadGrassRequest.RestoreAllRuntime(EntityManager);
            ClearPendingTags();
            m_Pending.Clear();
            m_Completed.Clear();
            m_GameReady = false;
            m_NeedsHydration = false;
            m_SuspendedForSave = false;
            m_WaitingForLoadSettle = false;
            m_RetryCountdown = 0;
            m_LoadSettleQuietFrames = 0;
            m_LoadSettleStartFrame = 0;
            m_LoadSettleLastFrame = -1;
            m_RetryPasses = 0;
        }

        [Preserve]
        protected override void OnGameLoadingComplete(Purpose purpose, GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);
            m_GameReady = true;
            m_NeedsHydration = true;
            m_WaitingForLoadSettle = true;
            m_RetryCountdown = 0;
            m_LoadSettleQuietFrames = 0;
            m_LoadSettleStartFrame = UnityEngine.Time.frameCount;
            m_LoadSettleLastFrame = -1;
        }

        [Preserve]
        protected override void OnUpdate()
        {
            if (!m_GameReady || m_SuspendedForSave)
            {
                return;
            }

            if (m_NeedsHydration)
            {
                LoadPersistentOverrides();
                m_NeedsHydration = false;
            }

            // On a true process restart, RoadBuilder recreates its generated
            // prefabs and marks their placed edges Updated after loading
            // completes. Replaying the override before that pass targets the
            // temporary composition and the grass disappears as soon as the
            // final composition is installed. This bounded gate performs no
            // geometry work; it only waits until the saved roads have remained
            // stable for a few Modification4 updates.
            if (m_WaitingForLoadSettle && !AdvanceLoadSettleGate())
            {
                return;
            }

            if (m_Pending.Count == 0)
            {
                return;
            }

            bool startup = m_RetryPasses > 0;
            if (!startup && m_ChangedPendingQuery.IsEmptyIgnoreFilter) return;
            if (startup && m_RetryCountdown > 0)
            {
                m_RetryCountdown--;
                return;
            }
            m_RetryCountdown = RetryInterval;
            if (startup) m_RetryPasses--;

            // After startup, unresolved records wake only when their own road changes.
            m_Completed.Clear();
            m_Work.Clear();
            if (startup) m_Work.AddRange(m_Pending);
            else
            {
                using var changed = m_ChangedPendingQuery.ToEntityArray(Allocator.Temp);
                foreach (var edge in changed)
                    if (m_Pending.TryGetValue(edge, out var areas)) m_Work.Add(new KeyValuePair<Entity, RoadGrassArea>(edge, areas));
            }
            foreach (KeyValuePair<Entity, RoadGrassArea> entry in m_Work)
            {
                Entity edge = entry.Key;
                RoadGrassArea areas = entry.Value & PersistedAreaMask;
                if (edge == Entity.Null || areas == RoadGrassArea.None)
                {
                    m_Completed.Add(edge);
                    RemovePersistentOverride(edge);
                    continue;
                }

                if (!EntityManager.Exists(edge))
                {
                    // Entity references can be temporarily unresolved while a
                    // save and RoadBuilder-generated prefabs are hydrating.
                    continue;
                }

                if (EntityManager.HasComponent<Deleted>(edge))
                {
                    m_Completed.Add(edge);
                    RemovePersistentOverride(edge);
                    continue;
                }

                if (startup && !EntityManager.HasComponent<RoadGrassPendingOverride>(edge))
                    EntityManager.AddComponent<RoadGrassPendingOverride>(edge);

                if (!TryGetComposition(edge, out Entity composition))
                {
                    continue;
                }

                if (RoadGrassRequest.CompositionStates.TryGetValue(
                        composition,
                        out RoadGrassRequest.CompositionState state) &&
                    (state.Areas & areas) == areas)
                {
                    m_Completed.Add(edge);
                    continue;
                }

                if (CanApply(composition))
                {
                    RoadGrassRequest.Enqueue(edge, areas);
                }
            }

            for (int i = 0; i < m_Completed.Count; i++)
            {
                RemovePendingTag(m_Completed[i]);
                m_Pending.Remove(m_Completed[i]);
            }
        }

        internal void RecordOverride(Entity edge, RoadGrassArea areas)
        {
            RemovePendingTag(edge);
            areas &= PersistedAreaMask;
            if (edge == Entity.Null || areas == RoadGrassArea.None ||
                !TryGetPersistentOverrides(create: true, out var overrides))
            {
                return;
            }

            int firstIndex = -1;
            RoadGrassArea combined = areas;
            for (int i = 0; i < overrides.Length; i++)
            {
                RoadGrassSavedOverride saved = overrides[i];
                if (saved.Edge != edge)
                {
                    continue;
                }

                combined |= (RoadGrassArea)saved.Areas & PersistedAreaMask;
                if (firstIndex < 0)
                {
                    firstIndex = i;
                }
            }

            if (firstIndex < 0)
            {
                overrides.Add(new RoadGrassSavedOverride(edge, combined));
            }
            else
            {
                overrides[firstIndex] = new RoadGrassSavedOverride(edge, combined);
                for (int i = overrides.Length - 1; i > firstIndex; i--)
                {
                    if (overrides[i].Edge == edge)
                    {
                        overrides.RemoveAt(i);
                    }
                }
            }

            m_Pending.Remove(edge);
            Mod.TryInfo($"Stored persistent grass override {combined} for road edge {edge}");
        }

        internal void RemoveOverride(Entity edge)
        {
            RemovePendingTag(edge);
            RemovePersistentOverride(edge);
            m_Pending.Remove(edge);
        }

        internal void ClearAllData()
        {
            ClearPendingTags();
            if (TryGetPersistentOverrides(create: false, out var overrides))
            {
                overrides.Clear();
            }
            m_Pending.Clear();
            m_Completed.Clear();
            m_NeedsHydration = false;
            m_WaitingForLoadSettle = false;
            m_RetryCountdown = 0;
        }

        internal void SuspendForSerialization()
        {
            if (m_SuspendedForSave)
            {
                return;
            }

            PruneDeletedOverrides();
            m_SuspendedForSave = true;
            ClearPendingTags();
            m_Pending.Clear();
            RoadGrassRequest.RestoreAllRuntime(EntityManager);

            int count = TryGetPersistentOverrides(create: false, out var overrides)
                ? overrides.Length
                : 0;
            Mod.TryInfo($"Saving {count} persistent Median Controller override(s)");
        }

        internal void ResumeAfterSerialization()
        {
            m_SuspendedForSave = false;
            m_NeedsHydration = true;
            // Saving does not recreate RoadBuilder prefabs. Reapply the
            // runtime mesh immediately after serialization instead of taking
            // the reboot-only stabilization path.
            m_WaitingForLoadSettle = false;
            m_RetryCountdown = 0;
        }

        private bool AdvanceLoadSettleGate()
        {
            if (m_Pending.Count == 0)
            {
                m_WaitingForLoadSettle = false;
                return true;
            }

            int frame = UnityEngine.Time.frameCount;
            if (frame == m_LoadSettleLastFrame)
            {
                // GameSimulation can update several times per rendered frame.
                // Count actual frames so a high simulation speed cannot bypass
                // the load stabilization window.
                return false;
            }
            m_LoadSettleLastFrame = frame;
            bool changing = false;
            foreach (KeyValuePair<Entity, RoadGrassArea> entry in m_Pending)
            {
                Entity edge = entry.Key;
                if (edge == Entity.Null || !EntityManager.Exists(edge))
                {
                    continue;
                }

                if (EntityManager.HasComponent<Updated>(edge) ||
                    !EntityManager.HasComponent<Edge>(edge) ||
                    !EntityManager.HasComponent<Composition>(edge))
                {
                    changing = true;
                    break;
                }
            }

            if (changing)
            {
                m_LoadSettleQuietFrames = 0;
            }
            else
            {
                m_LoadSettleQuietFrames++;
            }

            if (m_LoadSettleQuietFrames < LoadSettleQuietFrames &&
                frame - m_LoadSettleStartFrame < LoadSettleMaximumFrames)
            {
                return false;
            }

            m_WaitingForLoadSettle = false;
            m_RetryCountdown = 0;
            return true;
        }

        private void LoadPersistentOverrides()
        {
            ClearPendingTags();
            m_Pending.Clear();
            m_RetryPasses = 48;
            if (!TryGetPersistentOverrides(create: true, out var overrides))
            {
                return;
            }

            for (int i = overrides.Length - 1; i >= 0; i--)
            {
                RoadGrassSavedOverride saved = overrides[i];
                RoadGrassArea areas = (RoadGrassArea)saved.Areas & PersistedAreaMask;
                if (saved.Edge == Entity.Null || areas == RoadGrassArea.None)
                {
                    overrides.RemoveAt(i);
                    continue;
                }

                if (m_Pending.TryGetValue(saved.Edge, out RoadGrassArea existing))
                {
                    m_Pending[saved.Edge] = existing | areas;
                }
                else
                {
                    m_Pending.Add(saved.Edge, areas);
                }
            }

            // Canonicalize old/duplicate entries so subsequent saves contain
            // exactly one descriptor per selected road edge.
            overrides.Clear();
            foreach (KeyValuePair<Entity, RoadGrassArea> entry in m_Pending)
            {
                overrides.Add(new RoadGrassSavedOverride(entry.Key, entry.Value));
            }
            // Adding tags is structural: release the live city buffer before doing it.
            foreach (var edge in m_Pending.Keys)
                if (EntityManager.Exists(edge) && !EntityManager.HasComponent<Deleted>(edge) && !EntityManager.HasComponent<RoadGrassPendingOverride>(edge))
                    EntityManager.AddComponent<RoadGrassPendingOverride>(edge);

            Mod.TryInfo(
                $"Loaded {m_Pending.Count} persistent Median Controller override(s) from the city save buffer");
        }

        private void RemovePendingTag(Entity edge)
        {
            if (EntityManager.Exists(edge) && EntityManager.HasComponent<RoadGrassPendingOverride>(edge))
                EntityManager.RemoveComponent<RoadGrassPendingOverride>(edge);
        }
        private void ClearPendingTags()
        {
            foreach (var edge in m_Pending.Keys) RemovePendingTag(edge);
        }

        private bool TryGetPersistentOverrides(
            bool create,
            out DynamicBuffer<RoadGrassSavedOverride> overrides)
        {
            overrides = default;
            if (m_CityQuery.IsEmptyIgnoreFilter)
            {
                return false;
            }

            Entity city = m_CityQuery.GetSingletonEntity();
            if (!EntityManager.HasBuffer<RoadGrassSavedOverride>(city))
            {
                if (!create)
                {
                    return false;
                }
                EntityManager.AddBuffer<RoadGrassSavedOverride>(city);
            }

            overrides = EntityManager.GetBuffer<RoadGrassSavedOverride>(city);
            return true;
        }

        private void RemovePersistentOverride(Entity edge)
        {
            if (edge == Entity.Null ||
                !TryGetPersistentOverrides(create: false, out var overrides))
            {
                return;
            }

            for (int i = overrides.Length - 1; i >= 0; i--)
            {
                if (overrides[i].Edge == edge)
                {
                    overrides.RemoveAt(i);
                }
            }
        }

        private void PruneDeletedOverrides()
        {
            if (!TryGetPersistentOverrides(create: false, out var overrides))
            {
                return;
            }

            for (int i = overrides.Length - 1; i >= 0; i--)
            {
                Entity edge = overrides[i].Edge;
                if (edge == Entity.Null || !EntityManager.Exists(edge) ||
                    EntityManager.HasComponent<Deleted>(edge) ||
                    !EntityManager.HasComponent<Edge>(edge))
                {
                    overrides.RemoveAt(i);
                }
            }
        }

        private bool TryGetComposition(Entity edge, out Entity composition)
        {
            composition = Entity.Null;
            if (!EntityManager.HasComponent<Edge>(edge) ||
                !EntityManager.TryGetComponent(edge, out Composition edgeComposition))
            {
                return false;
            }

            composition = edgeComposition.m_Edge;
            return composition != Entity.Null && EntityManager.Exists(composition);
        }

        private bool CanApply(Entity composition)
        {
            return EntityManager.TryGetComponent(
                    composition,
                    out NetCompositionData compositionData) &&
                EntityManager.HasBuffer<NetCompositionPiece>(composition) &&
                EntityManager.HasComponent<NetCompositionMeshRef>(composition) &&
                RoadGrassMeshInjectSystem.HasStop(compositionData.m_Flags) &&
                RoadGrassMeshInjectSystem.HasGrass(compositionData.m_Flags);
        }

        [Preserve]
        protected override void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
            base.OnDestroy();
        }
    }
}
