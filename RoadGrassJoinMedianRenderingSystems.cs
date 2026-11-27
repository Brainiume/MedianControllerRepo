using System.Collections.Generic;
using Colossal.Entities;
using Colossal.Mathematics;
using Colossal.Rendering;
using Game;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Rendering;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using UnityEngine.Scripting;

namespace RoadGrass
{
    /// <summary>
    /// Adds one render-only, capless edge instance of the selected road's
    /// generated vanilla median mesh. The normal road remains owned by vanilla.
    /// </summary>
    [Preserve]
    internal partial class RoadGrassJoinMedianBatchSystem : GameSystemBase
    {
        internal const byte MedianMeshGroup = byte.MaxValue;

        internal readonly struct PropertyLayout
        {
            internal readonly int Matrix0;
            internal readonly int Matrix1;
            internal readonly int Matrix2;
            internal readonly int Matrix3;
            internal readonly int Matrix4;
            internal readonly int Matrix5;
            internal readonly int Matrix6;
            internal readonly int Matrix7;
            internal readonly int Sync0;
            internal readonly int Sync1;
            internal readonly int Sync2;
            internal readonly int Sync3;

            internal PropertyLayout(GroupData group)
            {
                Matrix0 = GetPropertyIndex(group, NetProperty.CompositionMatrix0);
                Matrix1 = GetPropertyIndex(group, NetProperty.CompositionMatrix1);
                Matrix2 = GetPropertyIndex(group, NetProperty.CompositionMatrix2);
                Matrix3 = GetPropertyIndex(group, NetProperty.CompositionMatrix3);
                Matrix4 = GetPropertyIndex(group, NetProperty.CompositionMatrix4);
                Matrix5 = GetPropertyIndex(group, NetProperty.CompositionMatrix5);
                Matrix6 = GetPropertyIndex(group, NetProperty.CompositionMatrix6);
                Matrix7 = GetPropertyIndex(group, NetProperty.CompositionMatrix7);
                Sync0 = GetPropertyIndex(group, NetProperty.CompositionSync0);
                Sync1 = GetPropertyIndex(group, NetProperty.CompositionSync1);
                Sync2 = GetPropertyIndex(group, NetProperty.CompositionSync2);
                Sync3 = GetPropertyIndex(group, NetProperty.CompositionSync3);
            }

            private static int GetPropertyIndex(GroupData group, NetProperty property)
            {
                return group.GetPropertyIndex((int)property, out int index) ? index : -1;
            }
        }

        internal readonly struct BatchHandle
        {
            internal readonly Entity Carrier;
            internal readonly Entity Mesh;
            internal readonly int GroupIndex;
            internal readonly int InstanceIndex;
            internal readonly byte SubMesh;
            internal readonly PropertyLayout Properties;
            internal readonly int Span;

            internal BatchHandle(
                Entity carrier,
                Entity mesh,
                int groupIndex,
                int instanceIndex,
                byte subMesh,
                PropertyLayout properties, int span = 0)
            {
                Carrier = carrier;
                Mesh = mesh;
                GroupIndex = groupIndex;
                InstanceIndex = instanceIndex;
                SubMesh = subMesh;
                Properties = properties;
                Span = span;
            }
        }

        private BatchManagerSystem m_BatchManagerSystem;
        private readonly Dictionary<RoadGrassMedianAnchorKey, List<BatchHandle>>
            m_Batches = new();
        private readonly HashSet<RoadGrassMedianAnchorKey> m_Desired = new();
        private readonly List<RoadGrassMedianAnchorKey> m_Remove = new();
        private readonly List<BatchGroup> m_ApplicableGroups = new();
        private const int HydrationDelayFrames = 12;
        private int m_LastRevision = -1;
        private int m_BatchRevision = 1;
        private int m_HydrationDelayFrames;
        private int m_LastHydrationFrame = -1;
        private bool m_WaitingForMeshes;

        internal static RoadGrassJoinMedianBatchSystem Instance { get; private set; }
        internal int BatchRevision => m_BatchRevision;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            Instance = this;
            m_BatchManagerSystem = World.GetOrCreateSystemManaged<BatchManagerSystem>();
        }

        [Preserve]
        protected override void OnUpdate()
        {
            RoadGrassJoinCompositionSystem.Instance?.SyncMedianBatches(this);
        }

        internal void ResetForGameLoad()
        {
            RemoveAll();
            m_LastRevision = -1;
            m_WaitingForMeshes = true;
            m_HydrationDelayFrames = 0;
            m_LastHydrationFrame = -1;
            AdvanceBatchRevision();
        }

        internal void BeginHydrationDelay()
        {
            // Mesh refs and native batch groups are both rebuilt during city
            // loading. Do not allocate a visible native instance until those
            // inputs have stayed available for a short bounded window. This
            // prevents the standalone median mesh from appearing with its
            // undeformed/default matrices during load.
            m_LastRevision = -1;
            m_WaitingForMeshes = true;
            m_HydrationDelayFrames = HydrationDelayFrames;
            m_LastHydrationFrame = -1;
            AdvanceBatchRevision();
        }

        internal void Sync(
            IReadOnlyDictionary<
                RoadGrassMedianAnchorKey,
                RoadGrassJoinCompositionSystem.VariantState> variants,
            IReadOnlyDictionary<
                RoadGrassMedianAnchorKey,
                RoadGrassMedianAnchorKey> pairs,
            int revision)
        {
            if (revision == m_LastRevision && !m_WaitingForMeshes &&
                m_HydrationDelayFrames == 0)
            {
                return;
            }

            m_WaitingForMeshes = false;
            m_Desired.Clear();
            foreach (KeyValuePair<
                         RoadGrassMedianAnchorKey,
                         RoadGrassMedianAnchorKey> pair in pairs)
            {
                if (variants.TryGetValue(
                        pair.Key,
                        out RoadGrassJoinCompositionSystem.VariantState state) &&
                    variants.ContainsKey(pair.Value))
                {
                    if (TryGetMedianMesh(state, out _))
                    {
                        if (RoadGrassJoinCompositionSystem.Instance.GetMedianRenderSpanCount(pair.Key) > 0)
                            m_Desired.Add(pair.Key);
                    }
                    else
                    {
                        m_WaitingForMeshes = true;
                    }
                }
            }

            if (m_HydrationDelayFrames > 0)
            {
                if (pairs.Count == 0)
                {
                    m_HydrationDelayFrames = 0;
                    m_LastHydrationFrame = -1;
                    m_LastRevision = revision;
                    return;
                }

                if (m_WaitingForMeshes || m_Desired.Count == 0)
                {
                    return;
                }

                int frame = UnityEngine.Time.frameCount;
                if (frame == m_LastHydrationFrame)
                {
                    return;
                }

                m_LastHydrationFrame = frame;
                m_HydrationDelayFrames--;
                if (m_HydrationDelayFrames > 0)
                {
                    return;
                }

                // Allocation happens only after the delay. The data system,
                // which runs later in this Rendering phase, observes the new
                // batch revision and uploads the complete matrices immediately.
                m_LastHydrationFrame = -1;
            }

            bool requiresSync = m_Batches.Count != m_Desired.Count;
            if (!requiresSync)
            {
                foreach (RoadGrassMedianAnchorKey endpoint in m_Desired)
                {
                    if (!m_Batches.TryGetValue(endpoint, out List<BatchHandle> handles) ||
                        handles.Count == 0 ||
                        !variants.TryGetValue(
                             endpoint,
                             out RoadGrassJoinCompositionSystem.VariantState state) ||
                        !TryGetMedianMesh(state, out Entity medianMesh) ||
                        !HandlesMatchVariant(handles, state.Carrier, medianMesh) ||
                        handles[handles.Count - 1].Span + 1 != RoadGrassJoinCompositionSystem.Instance.GetMedianRenderSpanCount(endpoint))
                    {
                        requiresSync = true;
                        break;
                    }
                }
            }
            if (!requiresSync)
            {
                m_LastRevision = revision;
                return;
            }

            // Native instance allocation/removal has no command-buffer API and
            // returns indices immediately.  Synchronize only when the desired
            // set actually changes; steady-state rendering never blocks here.
            GetNativeRenderingData(
                out NativeBatchGroups<CullingData, GroupData, BatchData, InstanceData> groups,
                out NativeBatchInstances<CullingData, GroupData, BatchData, InstanceData> instances,
                out NativeSubBatches<CullingData, GroupData, BatchData, InstanceData> subBatches);

            m_Remove.Clear();
            foreach (KeyValuePair<
                         RoadGrassMedianAnchorKey,
                         List<BatchHandle>> entry in m_Batches)
            {
                bool valid = m_Desired.Contains(entry.Key) &&
                    variants.TryGetValue(
                        entry.Key,
                        out RoadGrassJoinCompositionSystem.VariantState state) &&
                    TryGetMedianMesh(state, out Entity medianMesh) &&
                    AreHandlesValid(
                        entry.Key,
                        entry.Value,
                        state.Carrier,
                        medianMesh,
                        groups,
                        instances);
                if (!valid)
                {
                    RemoveEndpointHandles(
                        entry.Key,
                        entry.Value,
                        groups,
                        instances,
                        subBatches);
                    m_Remove.Add(entry.Key);
                }
            }

            for (int i = 0; i < m_Remove.Count; i++)
            {
                m_Batches.Remove(m_Remove[i]);
            }

            foreach (RoadGrassMedianAnchorKey endpoint in m_Desired)
            {
                if (m_Batches.ContainsKey(endpoint))
                {
                    continue;
                }

                if (!variants.TryGetValue(
                        endpoint,
                        out RoadGrassJoinCompositionSystem.VariantState state) ||
                    !TryGetMedianMesh(state, out Entity medianMesh) ||
                    !TryGetApplicableBatchGroups(
                        medianMesh,
                        groups,
                        m_ApplicableGroups))
                {
                    m_WaitingForMeshes = true;
                    continue;
                }

                int spanCount = RoadGrassJoinCompositionSystem.Instance.GetMedianRenderSpanCount(endpoint);
                var handles = new List<BatchHandle>(m_ApplicableGroups.Count * spanCount);
                m_Batches[endpoint] = handles;
                bool addedAll = true;
                for (int i = 0; i < m_ApplicableGroups.Count && addedAll; i++)
                for (int span = 0; span < spanCount; span++)
                {
                    if (!TryAddHandle(
                            endpoint,
                            state.Carrier,
                            medianMesh,
                            m_ApplicableGroups[i],
                            groups,
                            instances,
                            subBatches,
                            out BatchHandle handle, span))
                    {
                        addedAll = false;
                        break;
                    }
                    handles.Add(handle);
                }
                if (!addedAll)
                {
                    RemoveEndpointHandles(
                        endpoint,
                        handles,
                        groups,
                        instances,
                        subBatches);
                    m_Batches.Remove(endpoint);
                    m_WaitingForMeshes = true;
                    continue;
                }

                m_Batches[endpoint] = handles;
            }

            RegisterRenderingAccess();
            AdvanceBatchRevision();
            m_LastRevision = revision;
        }

        internal bool TryGetBatches(
            RoadGrassMedianAnchorKey endpoint,
            out IReadOnlyList<BatchHandle> handles)
        {
            if (m_Batches.TryGetValue(endpoint, out List<BatchHandle> result) &&
                result.Count > 0)
            {
                handles = result;
                return true;
            }
            handles = null;
            return false;
        }

        internal void RemoveAnchor(RoadGrassMedianAnchorKey endpoint)
        {
            RemoveAnchors(new[] { endpoint });
        }

        internal void RemoveAnchors(IEnumerable<RoadGrassMedianAnchorKey> endpoints)
        {
            var pending = new List<RoadGrassMedianAnchorKey>();
            var unique = new HashSet<RoadGrassMedianAnchorKey>();
            foreach (RoadGrassMedianAnchorKey endpoint in endpoints)
            {
                if (unique.Add(endpoint) && m_Batches.ContainsKey(endpoint))
                {
                    pending.Add(endpoint);
                }
            }
            if (pending.Count == 0)
            {
                return;
            }

            GetNativeRenderingData(
                out NativeBatchGroups<CullingData, GroupData, BatchData, InstanceData> groups,
                out NativeBatchInstances<CullingData, GroupData, BatchData, InstanceData> instances,
                out NativeSubBatches<CullingData, GroupData, BatchData, InstanceData> subBatches);
            for (int i = 0; i < pending.Count; i++)
            {
                RoadGrassMedianAnchorKey endpoint = pending[i];
                if (!m_Batches.TryGetValue(endpoint, out List<BatchHandle> current))
                {
                    continue;
                }
                RemoveEndpointHandles(
                    endpoint,
                    current,
                    groups,
                    instances,
                    subBatches);
                m_Batches.Remove(endpoint);
            }
            RegisterRenderingAccess();
            AdvanceBatchRevision();
        }

        internal void RemoveAll()
        {
            bool hadState = m_Batches.Count != 0 ||
                m_LastRevision != -1 ||
                m_WaitingForMeshes ||
                m_HydrationDelayFrames != 0;
            if (!hadState)
            {
                return;
            }

            if (m_BatchManagerSystem != null && m_Batches.Count != 0)
            {
                GetNativeRenderingData(
                    out NativeBatchGroups<CullingData, GroupData, BatchData, InstanceData> groups,
                    out NativeBatchInstances<CullingData, GroupData, BatchData, InstanceData> instances,
                    out NativeSubBatches<CullingData, GroupData, BatchData, InstanceData> subBatches);
                m_Remove.Clear();
                m_Remove.AddRange(m_Batches.Keys);
                for (int i = 0; i < m_Remove.Count; i++)
                {
                    RoadGrassMedianAnchorKey endpoint = m_Remove[i];
                    RemoveEndpointHandles(
                        endpoint,
                        m_Batches[endpoint],
                        groups,
                        instances,
                        subBatches);
                }
                RegisterRenderingAccess();
            }

            m_Batches.Clear();
            m_LastRevision = -1;
            m_WaitingForMeshes = false;
            m_HydrationDelayFrames = 0;
            m_LastHydrationFrame = -1;
            AdvanceBatchRevision();
        }

        private bool TryGetMedianMesh(
            RoadGrassJoinCompositionSystem.VariantState state,
            out Entity mesh)
        {
            mesh = Entity.Null;
            if (!EntityManager.Exists(state.MedianComposition) ||
                !EntityManager.TryGetComponent(
                    state.MedianComposition,
                    out NetCompositionMeshRef meshRef) ||
                meshRef.m_Mesh == Entity.Null ||
                !EntityManager.Exists(meshRef.m_Mesh) ||
                !EntityManager.HasComponent<NetCompositionMeshData>(meshRef.m_Mesh) ||
                !EntityManager.HasBuffer<BatchGroup>(meshRef.m_Mesh))
            {
                return false;
            }

            mesh = meshRef.m_Mesh;
            return EntityManager.GetBuffer<BatchGroup>(mesh, true).Length > 0;
        }

        private bool TryGetApplicableBatchGroups(
            Entity mesh,
            NativeBatchGroups<CullingData, GroupData, BatchData, InstanceData> groups,
            List<BatchGroup> result)
        {
            result.Clear();
            if (!EntityManager.TryGetComponent(
                    mesh,
                    out NetCompositionMeshData meshData))
            {
                return false;
            }

            // Match BatchInstanceSystem.UpdateNetInstances/AddInstance: use
            // the composition mesh's default layer override and instantiate
            // every matching net batch group. A median commonly has separate
            // grass/top, curb, and side material groups; selecting only one
            // group drops the grass on width transitions.
            MeshLayer layers = MeshLayer.Default;
            if (meshData.m_DefaultLayers != 0)
            {
                layers = meshData.m_DefaultLayers;
            }
            DynamicBuffer<BatchGroup> batchGroups =
                EntityManager.GetBuffer<BatchGroup>(mesh, true);
            for (int i = 0; i < batchGroups.Length; i++)
            {
                BatchGroup candidate = batchGroups[i];
                if ((candidate.m_Layer & layers) == 0 ||
                    candidate.m_Type != MeshType.Net ||
                    candidate.m_Partition != 0 ||
                    !groups.IsValidGroup(candidate.m_GroupIndex))
                {
                    continue;
                }

                GroupData group = groups.GetGroupData(candidate.m_GroupIndex);
                if (group.m_Mesh != mesh || group.m_MeshType != MeshType.Net)
                {
                    continue;
                }
                result.Add(candidate);
            }

            return result.Count > 0;
        }

        private bool TryAddHandle(
            RoadGrassMedianAnchorKey endpoint,
            Entity carrier,
            Entity mesh,
            BatchGroup batchGroup,
            NativeBatchGroups<CullingData, GroupData, BatchData, InstanceData> groups,
            NativeBatchInstances<CullingData, GroupData, BatchData, InstanceData> instances,
            NativeSubBatches<CullingData, GroupData, BatchData, InstanceData> subBatches,
            out BatchHandle handle, int span = 0)
        {
            handle = default;
            if (!EntityManager.Exists(endpoint.Edge) ||
                !EntityManager.Exists(carrier) ||
                !EntityManager.HasBuffer<MeshBatch>(carrier))
            {
                return false;
            }

            // StartNode/EndNode meshes contain median caps.  Using them for both
            // selected roads creates multiple visible strips and a seam gap.
            // The Edge mesh is capless and is deformed to one half of the shared
            // connection by RoadGrassJoinMedianBatchDataSystem.
            NetSubMesh subMesh = NetSubMesh.Edge;
            InstanceData instanceData = new InstanceData
            {
                // The real road edge's MeshBatch buffer is owned by vanilla and
                // is cleared whenever that edge leaves the near-camera set.
                // Keep this synthetic instance on the isolated carrier so it
                // remains registered in native culling and can become visible
                // again when the camera returns.
                m_Entity = carrier,
                m_MeshGroup = MedianMeshGroup,
                m_MeshIndex = (byte)subMesh,
                // Stable per-span identity survives native instance-array compaction.
                m_TileIndex = (byte)span
            };
            int instanceIndex = instances.AddInstance(
                instanceData,
                new CullingData { lodFade = 255 },
                batchGroup.m_GroupIndex,
                batchGroup.m_MergeIndex,
                subBatches);

            EntityManager.GetBuffer<MeshBatch>(carrier).Add(new MeshBatch
            {
                m_GroupIndex = batchGroup.m_GroupIndex,
                m_InstanceIndex = instanceIndex,
                m_MeshGroup = MedianMeshGroup,
                m_MeshIndex = (byte)subMesh,
                m_TileIndex = (byte)span
            });
            handle = new BatchHandle(
                carrier,
                mesh,
                batchGroup.m_GroupIndex,
                instanceIndex,
                (byte)subMesh,
                new PropertyLayout(groups.GetGroupData(batchGroup.m_GroupIndex)), span);
            return true;
        }

        private static bool HandlesMatchVariant(
            List<BatchHandle> handles,
            Entity carrier,
            Entity mesh)
        {
            for (int i = 0; i < handles.Count; i++)
            {
                if (handles[i].Carrier != carrier || handles[i].Mesh != mesh)
                {
                    return false;
                }
            }
            return handles.Count > 0;
        }

        private bool AreHandlesValid(
            RoadGrassMedianAnchorKey endpoint,
            List<BatchHandle> handles,
            Entity carrier,
            Entity mesh,
            NativeBatchGroups<CullingData, GroupData, BatchData, InstanceData> groups,
            NativeBatchInstances<CullingData, GroupData, BatchData, InstanceData> instances)
        {
            if (!HandlesMatchVariant(handles, carrier, mesh) ||
                !TryGetApplicableBatchGroups(mesh, groups, m_ApplicableGroups) ||
                handles.Count != m_ApplicableGroups.Count * RoadGrassJoinCompositionSystem.Instance.GetMedianRenderSpanCount(endpoint))
            {
                return false;
            }

            for (int i = 0; i < handles.Count; i++)
            {
                if (!TryResolveHandle(handles[i], out BatchHandle handle))
                {
                    return false;
                }
                handles[i] = handle;
                bool groupExpected = false;
                for (int j = 0; j < m_ApplicableGroups.Count; j++)
                {
                    if (m_ApplicableGroups[j].m_GroupIndex == handle.GroupIndex)
                    {
                        groupExpected = true;
                        break;
                    }
                }
                if (!groupExpected ||
                    !IsHandleValid(endpoint, handle, groups, instances))
                {
                    return false;
                }
            }
            return true;
        }

        private bool TryResolveHandle(BatchHandle handle, out BatchHandle resolved)
        {
            resolved = handle;
            if (!EntityManager.Exists(handle.Carrier) ||
                !EntityManager.HasBuffer<MeshBatch>(handle.Carrier))
            {
                return false;
            }
            DynamicBuffer<MeshBatch> batches =
                EntityManager.GetBuffer<MeshBatch>(handle.Carrier, true);
            for (int i = 0; i < batches.Length; i++)
            {
                MeshBatch batch = batches[i];
                if (batch.m_GroupIndex == handle.GroupIndex &&
                    batch.m_MeshGroup == MedianMeshGroup &&
                    batch.m_MeshIndex == handle.SubMesh && batch.m_TileIndex == handle.Span)
                {
                    resolved = new BatchHandle(
                        handle.Carrier,
                        handle.Mesh,
                        handle.GroupIndex,
                        batch.m_InstanceIndex,
                        handle.SubMesh,
                        handle.Properties, handle.Span);
                    return true;
                }
            }
            return false;
        }

        private bool IsHandleValid(
            RoadGrassMedianAnchorKey endpoint,
            BatchHandle handle,
            NativeBatchGroups<CullingData, GroupData, BatchData, InstanceData> groups,
            NativeBatchInstances<CullingData, GroupData, BatchData, InstanceData> instances)
        {
            if (!EntityManager.Exists(endpoint.Edge) ||
                !EntityManager.Exists(handle.Carrier) ||
                !EntityManager.HasBuffer<MeshBatch>(handle.Carrier) ||
                !groups.IsValidGroup(handle.GroupIndex) ||
                groups.GetGroupData(handle.GroupIndex).m_Mesh != handle.Mesh ||
                handle.InstanceIndex < 0 ||
                handle.InstanceIndex >= instances.GetInstanceCount(handle.GroupIndex))
            {
                return false;
            }

            InstanceData data =
                instances.GetInstanceData(handle.GroupIndex, handle.InstanceIndex);
            if (data.m_Entity != handle.Carrier ||
                data.m_MeshGroup != MedianMeshGroup ||
                data.m_MeshIndex != handle.SubMesh || data.m_TileIndex != handle.Span)
            {
                return false;
            }

            DynamicBuffer<MeshBatch> batches =
                EntityManager.GetBuffer<MeshBatch>(handle.Carrier, true);
            for (int i = 0; i < batches.Length; i++)
            {
                MeshBatch batch = batches[i];
                if (batch.m_GroupIndex == handle.GroupIndex &&
                    batch.m_InstanceIndex == handle.InstanceIndex &&
                    batch.m_MeshGroup == MedianMeshGroup &&
                    batch.m_MeshIndex == handle.SubMesh && batch.m_TileIndex == handle.Span)
                {
                    return true;
                }
            }

            return false;
        }

        private void RemoveEndpointHandles(
            RoadGrassMedianAnchorKey endpoint,
            List<BatchHandle> handles,
            NativeBatchGroups<CullingData, GroupData, BatchData, InstanceData> groups,
            NativeBatchInstances<CullingData, GroupData, BatchData, InstanceData> instances,
            NativeSubBatches<CullingData, GroupData, BatchData, InstanceData> subBatches)
        {
            // Remove the tracked entry before touching the packed native
            // instance array. If removal swaps another tracked instance into
            // this slot, RepairMovedHandle can then update that remaining
            // handle without confusing it with the one being removed.
            while (handles.Count > 0)
            {
                int index = handles.Count - 1;
                BatchHandle handle = handles[index];
                handles.RemoveAt(index);
                RemoveHandle(
                    endpoint,
                    handle,
                    groups,
                    instances,
                    subBatches);
            }
        }

        private void RemoveHandle(
            RoadGrassMedianAnchorKey endpoint,
            BatchHandle handle,
            NativeBatchGroups<CullingData, GroupData, BatchData, InstanceData> groups,
            NativeBatchInstances<CullingData, GroupData, BatchData, InstanceData> instances,
            NativeSubBatches<CullingData, GroupData, BatchData, InstanceData> subBatches)
        {
            bool foundBatch = false;
            if (EntityManager.Exists(handle.Carrier) &&
                EntityManager.HasBuffer<MeshBatch>(handle.Carrier))
            {
                DynamicBuffer<MeshBatch> batches =
                    EntityManager.GetBuffer<MeshBatch>(handle.Carrier);
                for (int i = batches.Length - 1; i >= 0; i--)
                {
                    MeshBatch batch = batches[i];
                    if (batch.m_GroupIndex == handle.GroupIndex &&
                        batch.m_MeshGroup == MedianMeshGroup &&
                        batch.m_MeshIndex == handle.SubMesh && batch.m_TileIndex == handle.Span)
                    {
                        handle = new BatchHandle(
                            handle.Carrier,
                            handle.Mesh,
                            handle.GroupIndex,
                            batch.m_InstanceIndex,
                            handle.SubMesh,
                            handle.Properties, handle.Span);
                        batches.RemoveAt(i);
                        foundBatch = true;
                        break;
                    }
                }
            }

            if (!foundBatch)
            {
                return;
            }

            if (!groups.IsValidGroup(handle.GroupIndex) ||
                handle.InstanceIndex < 0 ||
                handle.InstanceIndex >= instances.GetInstanceCount(handle.GroupIndex))
            {
                return;
            }

            InstanceData current =
                instances.GetInstanceData(handle.GroupIndex, handle.InstanceIndex);
            if (current.m_Entity != handle.Carrier ||
                current.m_MeshGroup != MedianMeshGroup ||
                current.m_MeshIndex != handle.SubMesh || current.m_TileIndex != handle.Span)
            {
                // Vanilla may already have removed this instance while
                // rebuilding the edge's normal batches. Never remove whatever
                // instance was swapped into its old index.
                return;
            }

            InstanceData moved = instances.RemoveInstance(
                handle.GroupIndex,
                handle.InstanceIndex,
                subBatches);
            if (moved.m_Entity != Entity.Null)
            {
                int movedFromIndex = instances.GetInstanceCount(handle.GroupIndex);
                RepairMovedBatch(
                    moved,
                    handle.GroupIndex,
                    movedFromIndex,
                    handle.InstanceIndex);
                RepairMovedHandle(
                    moved,
                    handle.GroupIndex,
                    movedFromIndex,
                    handle.InstanceIndex);
            }
        }

        private void RepairMovedBatch(
            InstanceData moved,
            int groupIndex,
            int movedFromIndex,
            int replacementIndex)
        {
            if (!EntityManager.Exists(moved.m_Entity) ||
                !EntityManager.HasBuffer<MeshBatch>(moved.m_Entity))
            {
                return;
            }

            DynamicBuffer<MeshBatch> movedBatches =
                EntityManager.GetBuffer<MeshBatch>(moved.m_Entity);
            for (int i = 0; i < movedBatches.Length; i++)
            {
                MeshBatch batch = movedBatches[i];
                if (batch.m_GroupIndex == groupIndex &&
                    batch.m_InstanceIndex == movedFromIndex)
                {
                    batch.m_InstanceIndex = replacementIndex;
                    movedBatches[i] = batch;
                    return;
                }
            }
        }

        private void RepairMovedHandle(
            InstanceData moved,
            int groupIndex,
            int movedFromIndex,
            int replacementIndex)
        {
            foreach (KeyValuePair<
                         RoadGrassMedianAnchorKey,
                         List<BatchHandle>> entry in m_Batches)
            {
                List<BatchHandle> handles = entry.Value;
                for (int i = 0; i < handles.Count; i++)
                {
                    BatchHandle handle = handles[i];
                    if (handle.Carrier == moved.m_Entity &&
                        handle.GroupIndex == groupIndex &&
                        handle.InstanceIndex == movedFromIndex &&
                        moved.m_MeshGroup == MedianMeshGroup &&
                        moved.m_TileIndex == handle.Span &&
                        moved.m_MeshIndex == handle.SubMesh)
                    {
                        handles[i] = new BatchHandle(
                            handle.Carrier,
                            handle.Mesh,
                            handle.GroupIndex,
                            replacementIndex,
                            handle.SubMesh,
                            handle.Properties, handle.Span);
                        AdvanceBatchRevision();
                        return;
                    }
                }
            }
        }

        private void GetNativeRenderingData(
            out NativeBatchGroups<CullingData, GroupData, BatchData, InstanceData> groups,
            out NativeBatchInstances<CullingData, GroupData, BatchData, InstanceData> instances,
            out NativeSubBatches<CullingData, GroupData, BatchData, InstanceData> subBatches)
        {
            groups = m_BatchManagerSystem.GetNativeBatchGroups(
                readOnly: true,
                out JobHandle groupDependencies);
            instances = m_BatchManagerSystem.GetNativeBatchInstances(
                readOnly: false,
                out JobHandle instanceDependencies);
            subBatches = m_BatchManagerSystem.GetNativeSubBatches(
                readOnly: false,
                out JobHandle subBatchDependencies);
            JobHandle.CombineDependencies(
                groupDependencies,
                instanceDependencies,
                subBatchDependencies).Complete();
        }

        private void RegisterRenderingAccess()
        {
            m_BatchManagerSystem.AddNativeBatchGroupsReader(default);
            m_BatchManagerSystem.AddNativeBatchInstancesWriter(default);
            m_BatchManagerSystem.AddNativeSubBatchesWriter(default);
        }

        private void AdvanceBatchRevision()
        {
            unchecked
            {
                m_BatchRevision++;
            }
            if (m_BatchRevision == 0)
            {
                m_BatchRevision = 1;
            }
        }

        [Preserve]
        protected override void OnDestroy()
        {
            RemoveAll();
            if (Instance == this)
            {
                Instance = null;
            }

            base.OnDestroy();
        }
    }

    /// <summary>
    /// Runs after vanilla BatchDataSystem and replaces only the isolated median
    /// instance's edge matrices with its half of the shared connection strip.
    /// </summary>
    [Preserve]
    internal partial class RoadGrassJoinMedianBatchDataSystem : GameSystemBase
    {
        private BatchManagerSystem m_BatchManagerSystem;
        private NativeList<MedianBatchWrite> m_CachedWrites;
        private int m_LastCompositionRevision = -1;
        private int m_LastBatchRevision = -1;
        private int m_PendingWritePasses;
        private const int InitialWritePasses = 2;

        private struct MedianBatchWrite
        {
            internal Entity Edge;
            internal RoadGrassJoinMedianBatchSystem.BatchHandle Handle;
            internal BatchDataHelpers.CompositionParameters Parameters;
            internal Bounds3 Bounds;
        }

        [BurstCompile]
        private struct WriteMedianBatchDataJob : IJobParallelFor
        {
            [ReadOnly]
            internal NativeArray<MedianBatchWrite> Writes;
            [ReadOnly]
            internal NativeBatchGroups<CullingData, GroupData, BatchData, InstanceData> Groups;
            internal NativeBatchInstances<CullingData, GroupData, BatchData, InstanceData> Instances;
            [ReadOnly]
            internal BufferLookup<MeshBatch> MeshBatches;
            [ReadOnly]
            internal ComponentLookup<Game.Tools.Hidden> Hidden;
            [ReadOnly]
            internal ComponentLookup<Deleted> Deleted;

            public void Execute(int i)
            {
                NativeBatchInstances<CullingData, GroupData, BatchData, InstanceData>
                    .ParallelInstanceWriter writer = Instances.AsParallelInstanceWriter();
                MedianBatchWrite command = Writes[i];
                RoadGrassJoinMedianBatchSystem.BatchHandle handle = command.Handle;
                if (!Groups.IsValidGroup(handle.GroupIndex) ||
                    !MeshBatches.HasBuffer(handle.Carrier))
                {
                    return;
                }

                DynamicBuffer<MeshBatch> carrierBatches = MeshBatches[handle.Carrier];
                int instanceIndex = -1;
                for (int batchIndex = 0; batchIndex < carrierBatches.Length; batchIndex++)
                {
                    MeshBatch batch = carrierBatches[batchIndex];
                    if (batch.m_TileIndex == handle.Span && batch.m_GroupIndex == handle.GroupIndex &&
                        batch.m_MeshGroup == RoadGrassJoinMedianBatchSystem.MedianMeshGroup &&
                        batch.m_MeshIndex == handle.SubMesh)
                    {
                        instanceIndex = batch.m_InstanceIndex;
                        break;
                    }
                }
                if (instanceIndex < 0 ||
                    instanceIndex >= Instances.GetInstanceCount(handle.GroupIndex))
                {
                    return;
                }

                InstanceData instance =
                    Instances.GetInstanceData(handle.GroupIndex, instanceIndex);
                if (instance.m_Entity != handle.Carrier ||
                    instance.m_MeshGroup != RoadGrassJoinMedianBatchSystem.MedianMeshGroup ||
                    instance.m_MeshIndex != handle.SubMesh || instance.m_TileIndex != handle.Span)
                {
                    return;
                }
                handle = new RoadGrassJoinMedianBatchSystem.BatchHandle(
                    handle.Carrier,
                    handle.Mesh,
                    handle.GroupIndex,
                    instanceIndex,
                    handle.SubMesh,
                    handle.Properties, handle.Span);

                BatchDataHelpers.CompositionParameters parameters = command.Parameters;
                ref CullingData culling = ref writer.SetTransformValue(
                    parameters.m_TransformMatrix,
                    parameters.m_TransformMatrix,
                    handle.GroupIndex,
                    handle.InstanceIndex);
                culling.m_Bounds = command.Bounds;
                culling.isHidden = Hidden.HasComponent(command.Edge) ||
                    Deleted.HasComponent(command.Edge);

                RoadGrassJoinMedianBatchSystem.PropertyLayout properties = handle.Properties;
                SetProperty(writer, handle, properties.Matrix0,
                    parameters.m_CompositionMatrix0);
                SetProperty(writer, handle, properties.Matrix1,
                    parameters.m_CompositionMatrix1);
                SetProperty(writer, handle, properties.Matrix2,
                    parameters.m_CompositionMatrix2);
                SetProperty(writer, handle, properties.Matrix3,
                    parameters.m_CompositionMatrix3);
                SetProperty(writer, handle, properties.Matrix4,
                    parameters.m_CompositionMatrix4);
                SetProperty(writer, handle, properties.Matrix5,
                    parameters.m_CompositionMatrix5);
                SetProperty(writer, handle, properties.Matrix6,
                    parameters.m_CompositionMatrix6);
                SetProperty(writer, handle, properties.Matrix7,
                    parameters.m_CompositionMatrix7);
                SetProperty(writer, handle, properties.Sync0,
                    parameters.m_CompositionSync0);
                SetProperty(writer, handle, properties.Sync1,
                    parameters.m_CompositionSync1);
                SetProperty(writer, handle, properties.Sync2,
                    parameters.m_CompositionSync2);
                SetProperty(writer, handle, properties.Sync3,
                    parameters.m_CompositionSync3);
            }
        }

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            m_BatchManagerSystem = World.GetOrCreateSystemManaged<BatchManagerSystem>();
            m_CachedWrites = new NativeList<MedianBatchWrite>(Allocator.Persistent);
        }

        [Preserve]
        protected override void OnUpdate()
        {
            RoadGrassJoinCompositionSystem.Instance?.UpdateMedianBatchData(this);
        }

        internal void UpdateData(
            IReadOnlyDictionary<
                RoadGrassMedianAnchorKey,
                RoadGrassJoinCompositionSystem.VariantState> variants,
            IReadOnlyDictionary<
                RoadGrassMedianAnchorKey,
                RoadGrassMedianAnchorKey> pairs)
        {
            RoadGrassJoinCompositionSystem manager =
                RoadGrassJoinCompositionSystem.Instance;
            RoadGrassJoinMedianBatchSystem batchSystem =
                RoadGrassJoinMedianBatchSystem.Instance;
            if (manager == null || batchSystem == null)
            {
                return;
            }

            int compositionRevision = manager.RenderRevision;
            int batchRevision = batchSystem.BatchRevision;
            if (compositionRevision != m_LastCompositionRevision ||
                batchRevision != m_LastBatchRevision)
            {
                RebuildCachedWrites(
                    variants,
                    pairs,
                    manager,
                    batchSystem,
                    compositionRevision,
                    batchRevision);
            }
            if (m_PendingWritePasses == 0 ||
                !m_CachedWrites.IsCreated ||
                m_CachedWrites.Length == 0)
            {
                return;
            }

            NativeBatchGroups<CullingData, GroupData, BatchData, InstanceData> groups =
                m_BatchManagerSystem.GetNativeBatchGroups(
                    readOnly: true,
                    out JobHandle groupDependencies);
            NativeBatchInstances<CullingData, GroupData, BatchData, InstanceData> instances =
                m_BatchManagerSystem.GetNativeBatchInstances(
                    readOnly: false,
                    out JobHandle instanceDependencies);

            JobHandle result = new WriteMedianBatchDataJob
            {
                Writes = m_CachedWrites.AsArray(),
                Groups = groups,
                Instances = instances,
                MeshBatches = GetBufferLookup<MeshBatch>(true),
                Hidden = GetComponentLookup<Game.Tools.Hidden>(true),
                Deleted = GetComponentLookup<Deleted>(true)
            }.Schedule(
                m_CachedWrites.Length,
                32,
                JobHandle.CombineDependencies(
                    Dependency,
                    groupDependencies,
                    instanceDependencies));
            m_BatchManagerSystem.AddNativeBatchGroupsReader(result);
            m_BatchManagerSystem.AddNativeBatchInstancesWriter(result);
            Dependency = result;
            m_PendingWritePasses--;
        }

        private void RebuildCachedWrites(
            IReadOnlyDictionary<
                RoadGrassMedianAnchorKey,
                RoadGrassJoinCompositionSystem.VariantState> variants,
            IReadOnlyDictionary<
                RoadGrassMedianAnchorKey,
                RoadGrassMedianAnchorKey> pairs,
            RoadGrassJoinCompositionSystem manager,
            RoadGrassJoinMedianBatchSystem batchSystem,
            int compositionRevision,
            int batchRevision)
        {
            var replacement = new NativeList<MedianBatchWrite>(
                pairs.Count > 0 ? pairs.Count * 2 : 1,
                Allocator.Persistent);
            foreach (KeyValuePair<
                         RoadGrassMedianAnchorKey,
                         RoadGrassMedianAnchorKey> pair in pairs)
            {
                RoadGrassMedianAnchorKey endpoint = pair.Key;
                if (!variants.ContainsKey(endpoint) ||
                    !batchSystem.TryGetBatches(endpoint, out var handles))
                {
                    continue;
                }
                for (int i = 0; i < handles.Count; i++)
                {
                    if (!manager.TryGetMedianRenderState(endpoint, pair.Value,
                        out BatchDataHelpers.CompositionParameters parameters, out Bounds3 geometryBounds, handles[i].Span)) continue;
                    replacement.Add(new MedianBatchWrite
                    {
                        Edge = endpoint.Edge,
                        Handle = handles[i],
                        Parameters = parameters,
                        Bounds = geometryBounds
                    });
                }
            }

            if (m_CachedWrites.IsCreated)
            {
                Dependency = m_CachedWrites.Dispose(Dependency);
            }
            m_CachedWrites = replacement;
            m_LastCompositionRevision = compositionRevision;
            m_LastBatchRevision = batchRevision;
            // One pass initializes the freshly allocated instance; the second
            // catches the following-frame MeshBatch index after vanilla has
            // completed its group updates. Static medians perform no further
            // transform/property writes until a render or batch revision.
            m_PendingWritePasses = replacement.Length == 0
                ? 0
                : InitialWritePasses;
        }

        [Preserve]
        protected override void OnDestroy()
        {
            Dependency.Complete();
            if (m_CachedWrites.IsCreated)
            {
                m_CachedWrites.Dispose();
            }
            base.OnDestroy();
        }

        private static void SetProperty<T>(
            NativeBatchInstances<CullingData, GroupData, BatchData, InstanceData>
                .ParallelInstanceWriter writer,
            RoadGrassJoinMedianBatchSystem.BatchHandle handle,
            int propertyIndex,
            T value)
            where T : unmanaged
        {
            if (propertyIndex >= 0)
            {
                writer.SetPropertyValue(
                    value,
                    handle.GroupIndex,
                    propertyIndex,
                    handle.InstanceIndex);
            }
        }
    }
}
