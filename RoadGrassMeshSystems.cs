using System.Collections.Generic;
using Colossal.Entities;
using Game;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Rendering;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using UnityEngine.Scripting;

namespace RoadGrass
{
    [BurstCompile]
    internal struct RoadGrassMarkMatchingEdgesUpdatedJob : IJobChunk
    {
        [ReadOnly]
        internal EntityTypeHandle EntityType;
        [ReadOnly]
        internal ComponentTypeHandle<Composition> CompositionType;
        [ReadOnly]
        internal ComponentLookup<BatchesUpdated> BatchesUpdatedLookup;
        internal Entity Composition;
        internal EntityCommandBuffer.ParallelWriter CommandBuffer;

        public void Execute(
            in ArchetypeChunk chunk,
            int unfilteredChunkIndex,
            bool useEnabledMask,
            in v128 chunkEnabledMask)
        {
            NativeArray<Entity> entities = chunk.GetNativeArray(EntityType);
            NativeArray<Composition> compositions =
                chunk.GetNativeArray(ref CompositionType);
            for (int i = 0; i < chunk.Count; i++)
            {
                Entity edge = entities[i];
                if (compositions[i].m_Edge == Composition &&
                    !BatchesUpdatedLookup.HasComponent(edge))
                {
                    CommandBuffer.AddComponent<BatchesUpdated>(
                        unfilteredChunkIndex,
                        edge);
                }
            }
        }
    }

    internal static class RoadGrassRequest
    {
        internal readonly struct Selection
        {
            internal readonly Entity Edge;
            internal readonly RoadGrassArea Area;

            internal Selection(Entity edge, RoadGrassArea area)
            {
                Edge = edge;
                Area = area;
            }
        }

        internal struct CompositionState
        {
            internal NetCompositionMeshRef OriginalMeshRef;
            internal RoadGrassArea Areas;
        }

        private static readonly List<Selection> PendingSelections = new();
        private static int PendingHead;
        private static readonly HashSet<(Entity, RoadGrassArea)> PendingKeys = new();
        internal static Entity InjectedComposition;
        internal static readonly List<NetCompositionPiece> OriginalPieces = new();
        internal static readonly Dictionary<Entity, CompositionState> CompositionStates = new();

        internal static void Reset()
        {
            PendingSelections.Clear();
            PendingHead = 0;
            PendingKeys.Clear();
            InjectedComposition = Entity.Null;
            OriginalPieces.Clear();
            CompositionStates.Clear();
        }

        internal static bool Enqueue(Entity edge, RoadGrassArea area)
        {
            if (!PendingKeys.Add((edge, area))) return false;
            PendingSelections.Add(new Selection(edge, area));
            return true;
        }

        internal static bool TryDequeue(out Selection selection)
        {
            if (PendingSelections.Count == 0)
            {
                selection = default;
                return false;
            }

            selection = PendingSelections[PendingHead++];
            PendingKeys.Remove((selection.Edge, selection.Area));
            if (PendingHead == PendingSelections.Count) { PendingSelections.Clear(); PendingHead = 0; }
            else if (PendingHead > 1024 && PendingHead > PendingSelections.Count / 2)
            { PendingSelections.RemoveRange(0, PendingHead); PendingHead = 0; }
            return true;
        }

        internal static void RestoreAllRuntime(EntityManager entityManager)
        {
            if (InjectedComposition != Entity.Null &&
                entityManager.Exists(InjectedComposition) &&
                entityManager.HasBuffer<NetCompositionPiece>(InjectedComposition) &&
                OriginalPieces.Count != 0)
            {
                DynamicBuffer<NetCompositionPiece> pieces =
                    entityManager.GetBuffer<NetCompositionPiece>(InjectedComposition);
                pieces.Clear();
                for (int i = 0; i < OriginalPieces.Count; i++)
                {
                    pieces.Add(OriginalPieces[i]);
                }
            }

            foreach (KeyValuePair<Entity, CompositionState> entry in CompositionStates)
            {
                if (entityManager.Exists(entry.Key) &&
                    entityManager.HasComponent<NetCompositionMeshRef>(entry.Key))
                {
                    entityManager.SetComponentData(entry.Key, entry.Value.OriginalMeshRef);
                }
                if (entityManager.Exists(entry.Key) &&
                    entityManager.HasComponent<Created>(entry.Key))
                {
                    entityManager.RemoveComponent<Created>(entry.Key);
                }
            }

            Reset();
        }
    }

    [Preserve]
    internal partial class RoadGrassMeshInjectSystem : GameSystemBase
    {
        private EntityQuery m_EdgeQuery;
        private ModificationBarrier4 m_ModificationBarrier;

        internal static RoadGrassMeshInjectSystem Instance { get; private set; }

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            Instance = this;
            RoadGrassRequest.Reset();
            // This system runs in Modification4. EndFrameBarrier has already
            // closed by then, while vanilla NetCompositionMeshRefSystem uses
            // ModificationBarrier4 for work produced in this same phase.
            m_ModificationBarrier =
                World.GetOrCreateSystemManaged<ModificationBarrier4>();
            m_EdgeQuery = GetEntityQuery(ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<Composition>());
        }

        [Preserve]
        protected override void OnDestroy()
        {
            RoadGrassRequest.RestoreAllRuntime(EntityManager);
            if (Instance == this)
            {
                Instance = null;
            }
            base.OnDestroy();
        }

        internal void RestoreAllToVanilla()
        {
            var compositions = new List<Entity>(RoadGrassRequest.CompositionStates.Keys);
            Entity injected = RoadGrassRequest.InjectedComposition;
            if (injected != Entity.Null && !compositions.Contains(injected))
            {
                compositions.Add(injected);
            }

            RoadGrassRequest.RestoreAllRuntime(EntityManager);
            for (int i = 0; i < compositions.Count; i++)
            {
                MarkMatchingEdgesUpdated(compositions[i]);
            }
        }

        [Preserve]
        protected override void OnUpdate()
        {
            if (!RoadGrassRequest.TryDequeue(out RoadGrassRequest.Selection selection))
            {
                return;
            }

            Entity edge = selection.Edge;

            if (edge == Entity.Null || !EntityManager.Exists(edge) ||
                !EntityManager.TryGetComponent(edge, out Composition edgeComposition))
            {
                return;
            }

            if (selection.Area == RoadGrassArea.RestoreVanilla)
            {
                RestoreVanilla(edge, edgeComposition);
                return;
            }

            Entity composition = edgeComposition.m_Edge;
            if (composition == Entity.Null || !EntityManager.Exists(composition) ||
                !EntityManager.TryGetComponent(composition, out NetCompositionData compositionData) ||
                !EntityManager.HasBuffer<NetCompositionPiece>(composition) ||
                !EntityManager.TryGetComponent(composition, out NetCompositionMeshRef meshRef) ||
                !EntityManager.TryGetComponent(composition, out PrefabRef prefabRef) ||
                !EntityManager.HasBuffer<NetGeometrySection>(prefabRef.m_Prefab))
            {
                return;
            }

            RoadGrassRequest.CompositionStates.TryGetValue(
                composition, out RoadGrassRequest.CompositionState existingState);
            RoadGrassArea targetAreas = existingState.Areas | selection.Area;
            if ((existingState.Areas & selection.Area) == selection.Area)
            {
                RoadGrassOverridePersistenceSystem.Instance?.RecordOverride(
                    edge,
                    targetAreas);
                return;
            }

            CompositionFlags currentFlags = compositionData.m_Flags;
            if (!HasStop(currentFlags) || !HasGrass(currentFlags))
            {
                return;
            }

            CompositionFlags withoutStops = RemoveStopFlags(currentFlags);
            CompositionFlags withoutStopsOrGrass = RemoveGrassFlags(withoutStops);
            var subSections = GetBufferLookup<NetSubSection>(true);
            var sectionPieces = GetBufferLookup<NetSectionPiece>(true);
            var grassPieces = new NativeList<NetCompositionPiece>(Allocator.Temp);
            var plainPieces = new NativeList<NetCompositionPiece>(Allocator.Temp);
            DynamicBuffer<NetGeometrySection> geometrySections =
                EntityManager.GetBuffer<NetGeometrySection>(prefabRef.m_Prefab, true);

            NetCompositionHelpers.GetCompositionPieces(
                grassPieces, geometrySections.AsNativeArray(), withoutStops, subSections, sectionPieces);
            NetCompositionHelpers.GetCompositionPieces(
                plainPieces, geometrySections.AsNativeArray(), withoutStopsOrGrass, subSections, sectionPieces);

            // GetCompositionPieces only selects raw section pieces. Vanilla always performs this
            // second pass before using them; it calculates piece sizes and converts their offsets
            // into the composition-relative coordinates consumed by the curve deformation mesh.
            var netPieceData = GetComponentLookup<NetPieceData>(true);
            var vertexMatchData = GetComponentLookup<NetVertexMatchData>(true);
            NetCompositionData grassCompositionData = compositionData;
            grassCompositionData.m_Flags = withoutStops;
            NetCompositionHelpers.CalculateCompositionData(
                ref grassCompositionData, grassPieces.AsArray(), netPieceData, vertexMatchData);
            NetCompositionData plainCompositionData = compositionData;
            plainCompositionData.m_Flags = withoutStopsOrGrass;
            NetCompositionHelpers.CalculateCompositionData(
                ref plainCompositionData, plainPieces.AsArray(), netPieceData, vertexMatchData);

            DynamicBuffer<NetCompositionPiece> currentPieces = EntityManager.GetBuffer<NetCompositionPiece>(composition);
            RoadGrassRequest.OriginalPieces.Clear();
            for (int i = 0; i < currentPieces.Length; i++)
            {
                RoadGrassRequest.OriginalPieces.Add(currentPieces[i]);
            }

            int removedCount = 0;
            bool replaceWholeMedian = (targetAreas & RoadGrassArea.Median) != 0;
            for (int i = currentPieces.Length - 1; i >= 0; i--)
            {
                NetCompositionPiece current = currentPieces[i];
                bool isMedianMesh = replaceWholeMedian &&
                    (current.m_PieceFlags & NetPieceFlags.HasMesh) != 0 &&
                    (current.m_SectionFlags & NetSectionFlags.Median) != 0;
                bool isPlainEdgeMesh =
                    (targetAreas & RoadGrassArea.RoadEdge) != 0 &&
                    (current.m_PieceFlags & NetPieceFlags.HasMesh) != 0 &&
                    (current.m_SectionFlags & NetSectionFlags.Median) == 0 &&
                    ContainsEquivalent(plainPieces, current) &&
                    !ContainsEquivalent(grassPieces, current);

                if (isMedianMesh || isPlainEdgeMesh)
                {
                    currentPieces.RemoveAt(i);
                    removedCount++;
                }
            }

            int addedCount = 0;
            for (int i = 0; i < grassPieces.Length; i++)
            {
                NetCompositionPiece candidate = grassPieces[i];
                if ((candidate.m_PieceFlags & NetPieceFlags.HasMesh) == 0 ||
                    (candidate.m_SectionFlags & NetSectionFlags.Hidden) != 0 ||
                    !MatchesArea(candidate.m_SectionFlags, targetAreas) ||
                    ((candidate.m_SectionFlags & NetSectionFlags.Median) == 0 &&
                     ContainsEquivalent(plainPieces, candidate)) ||
                    ContainsEquivalent(currentPieces, candidate))
                {
                    continue;
                }

                currentPieces.Add(candidate);
                addedCount++;
            }

            if (removedCount == 0 && addedCount == 0)
            {
                RoadGrassRequest.OriginalPieces.Clear();
                grassPieces.Dispose();
                plainPieces.Dispose();
                return;
            }

            if (existingState.Areas == RoadGrassArea.None)
            {
                existingState.OriginalMeshRef = meshRef;
            }
            existingState.Areas = targetAreas;
            RoadGrassRequest.CompositionStates[composition] = existingState;
            RoadGrassOverridePersistenceSystem.Instance?.RecordOverride(
                edge,
                targetAreas);
            RoadGrassRequest.InjectedComposition = composition;
            if (!EntityManager.HasComponent<Created>(composition))
            {
                EntityManager.AddComponent<Created>(composition);
            }
            Mod.TryInfo(
                $"Replaced {removedCount} plain pieces with {addedCount} grass pieces for composition {composition}");
            grassPieces.Dispose();
            plainPieces.Dispose();
        }

        private void RestoreVanilla(Entity edge, Composition edgeComposition)
        {
            RoadGrassOverridePersistenceSystem.Instance?.RemoveOverride(edge);
            RoadGrassJoinCompositionSystem.Instance?.RestoreEdgeToVanilla(edge);

            Entity composition = edgeComposition.m_Edge;
            bool restoredMesh = false;
            if (composition != Entity.Null &&
                EntityManager.Exists(composition) &&
                RoadGrassRequest.CompositionStates.TryGetValue(
                    composition,
                    out RoadGrassRequest.CompositionState state))
            {
                if (EntityManager.HasComponent<NetCompositionMeshRef>(composition))
                {
                    EntityManager.SetComponentData(composition, state.OriginalMeshRef);
                    restoredMesh = true;
                }

                if (EntityManager.HasComponent<Created>(composition))
                {
                    EntityManager.RemoveComponent<Created>(composition);
                }

                RoadGrassRequest.CompositionStates.Remove(composition);
                MarkMatchingEdgesUpdated(composition);
            }

            Mod.TryInfo(
                $"Restored road edge {edge} to vanilla appearance" +
                (restoredMesh ? $" using original composition mesh {composition}" : string.Empty));
        }

        private static bool MatchesArea(NetSectionFlags sectionFlags, RoadGrassArea areas)
        {
            bool isMedian = (sectionFlags & NetSectionFlags.Median) != 0;
            return isMedian
                ? (areas & RoadGrassArea.Median) != 0
                : (areas & RoadGrassArea.RoadEdge) != 0;
        }

        internal static bool HasStop(CompositionFlags flags)
        {
            const CompositionFlags.Side sideMask = CompositionFlags.Side.PrimaryStop |
                CompositionFlags.Side.SecondaryStop | CompositionFlags.Side.TertiaryStop;
            return (flags.m_Left & sideMask) != 0 ||
                   (flags.m_Right & sideMask) != 0;
        }

        internal static bool HasGrass(CompositionFlags flags)
        {
            return (flags.m_General & CompositionFlags.General.PrimaryMiddleBeautification) != 0 ||
                   (flags.m_Left & CompositionFlags.Side.PrimaryBeautification) != 0 ||
                   (flags.m_Right & CompositionFlags.Side.PrimaryBeautification) != 0;
        }

        internal static CompositionFlags RemoveStopFlags(CompositionFlags flags)
        {
            const CompositionFlags.Side sideMask = CompositionFlags.Side.PrimaryStop |
                CompositionFlags.Side.SecondaryStop | CompositionFlags.Side.TertiaryStop;
            flags.m_Left &= ~sideMask;
            flags.m_Right &= ~sideMask;
            return flags;
        }

        private static CompositionFlags RemoveGrassFlags(CompositionFlags flags)
        {
            flags.m_General &= ~CompositionFlags.General.PrimaryMiddleBeautification;
            flags.m_Left &= ~CompositionFlags.Side.PrimaryBeautification;
            flags.m_Right &= ~CompositionFlags.Side.PrimaryBeautification;
            return flags;
        }

        private static bool ContainsEquivalent(NativeList<NetCompositionPiece> pieces, NetCompositionPiece candidate)
        {
            for (int i = 0; i < pieces.Length; i++)
            {
                if (Equivalent(pieces[i], candidate)) return true;
            }
            return false;
        }

        private static bool ContainsEquivalent(DynamicBuffer<NetCompositionPiece> pieces, NetCompositionPiece candidate)
        {
            for (int i = 0; i < pieces.Length; i++)
            {
                if (Equivalent(pieces[i], candidate)) return true;
            }
            return false;
        }

        private static bool Equivalent(NetCompositionPiece a, NetCompositionPiece b)
        {
            return a.m_Piece == b.m_Piece && a.m_Offset.Equals(b.m_Offset) &&
                   a.m_SectionFlags == b.m_SectionFlags && a.m_PieceFlags == b.m_PieceFlags;
        }

        private void MarkMatchingEdgesUpdated(Entity composition)
        {
            var job = new RoadGrassMarkMatchingEdgesUpdatedJob
            {
                EntityType = GetEntityTypeHandle(),
                CompositionType = GetComponentTypeHandle<Composition>(true),
                BatchesUpdatedLookup = GetComponentLookup<BatchesUpdated>(true),
                Composition = composition,
                CommandBuffer =
                    m_ModificationBarrier.CreateCommandBuffer().AsParallelWriter()
            };
            JobHandle result = job.ScheduleParallel(m_EdgeQuery, Dependency);
            m_ModificationBarrier.AddJobHandleForProducer(result);
            Dependency = result;
        }
    }

    [Preserve]
    internal partial class RoadGrassMeshRestoreSystem : GameSystemBase
    {
        private EntityQuery m_EdgeQuery;
        private ModificationBarrier4 m_ModificationBarrier;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            m_ModificationBarrier =
                World.GetOrCreateSystemManaged<ModificationBarrier4>();
            m_EdgeQuery = GetEntityQuery(ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<Composition>());
        }

        [Preserve]
        protected override void OnUpdate()
        {
            Entity composition = RoadGrassRequest.InjectedComposition;
            RoadGrassRequest.InjectedComposition = Entity.Null;

            if (composition == Entity.Null || !EntityManager.Exists(composition) ||
                !EntityManager.HasBuffer<NetCompositionPiece>(composition))
            {
                RoadGrassRequest.OriginalPieces.Clear();
                return;
            }

            DynamicBuffer<NetCompositionPiece> pieces = EntityManager.GetBuffer<NetCompositionPiece>(composition);
            pieces.Clear();
            for (int i = 0; i < RoadGrassRequest.OriginalPieces.Count; i++)
            {
                pieces.Add(RoadGrassRequest.OriginalPieces[i]);
            }
            RoadGrassRequest.OriginalPieces.Clear();

            if (EntityManager.HasComponent<Created>(composition))
            {
                EntityManager.RemoveComponent<Created>(composition);
            }

            var job = new RoadGrassMarkMatchingEdgesUpdatedJob
            {
                EntityType = GetEntityTypeHandle(),
                CompositionType = GetComponentTypeHandle<Composition>(true),
                BatchesUpdatedLookup = GetComponentLookup<BatchesUpdated>(true),
                Composition = composition,
                CommandBuffer =
                    m_ModificationBarrier.CreateCommandBuffer().AsParallelWriter()
            };
            JobHandle result = job.ScheduleParallel(m_EdgeQuery, Dependency);
            m_ModificationBarrier.AddJobHandleForProducer(result);
            Dependency = result;
        }
    }
}
