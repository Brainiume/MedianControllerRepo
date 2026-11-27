using System;
using System.Collections.Generic;
using Colossal.Mathematics;
using Colossal.Rendering;
using Colossal.Serialization.Entities;
using Game;
using Game.Prefabs;
using Game.Rendering;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Scripting;

namespace RoadGrass
{
    [Preserve]
    internal partial class RoadGrassPolygonPrototypeBatchSystem : GameSystemBase
    {
        private sealed class Entry
        {
            internal object Plan;
            internal MedianSavedSurface Surface;
            internal PolygonTerrainLayer TerrainLayer;
            internal float TextureTiling;
            internal List<RoadGrassTaperGeometry.Lane> Crosswalks;
            internal int TerrainDiffuseId, TerrainNormalId;
            internal Mesh Pavement, Grass;
            internal Material PavementMaterial, GrassMaterial;
            internal List<RenderTexture> FillTextures;
            internal List<Colossal.IO.AssetDatabase.TextureAsset> MaterialTextures;
            internal Entity MeshEntity, Carrier;
            internal int Group = -1;
            internal float3 Origin;
            internal Bounds3 Bounds;
            internal bool UseGrass;
            internal bool ReportPending;
            internal string Error;
            internal Vector3[] PickVertices;
            internal int[] PickTriangles;
            internal List<Line3.Segment> Outline;
            internal RoadGrassMedianPickIndex PickIndex;
            internal Vector2[] InteriorUVs;
            internal float OriginalTiling;
        }

        internal struct Write
        {
            internal int Group;
            internal Entity MeshEntity, Carrier;
            internal float3 Origin;
            internal Bounds3 Bounds;
        }

        internal static RoadGrassPolygonPrototypeBatchSystem Instance { get; private set; }
        private BatchManagerSystem m_BatchManager;
        private BatchMeshSystem m_BatchMeshes;
        private RoadGrassPolygonPrototypeResources m_Resources;
        private string m_ResourceError;
        private bool m_LoadAttempted, m_DisposeResources;
        private bool m_ReportPending;
        private readonly Dictionary<int, Entry> m_Entries = new();
        private readonly Dictionary<int, Entry> m_Pending = new();
        private readonly HashSet<int> m_Remove = new();
        private NativeList<Write> m_Writes;
        private RoadGrassMedianPickIndex m_PickIndex;
        private int[] m_PickIds = Array.Empty<int>();
        internal int WriteRevision { get; private set; }
        internal NativeArray<Write> Writes => m_Writes.AsArray();

        protected override void OnCreate()
        {
            base.OnCreate();
            Instance = this;
            m_BatchManager = World.GetOrCreateSystemManaged<BatchManagerSystem>();
            m_BatchMeshes = World.GetOrCreateSystemManaged<BatchMeshSystem>();
            m_Writes = new NativeList<Write>(Allocator.Persistent);
        }

        internal bool TryInitialize(out string reason)
        {
            if (!m_LoadAttempted)
            {
                m_LoadAttempted = true;
                RoadGrassPolygonPrototypeResources.TryLoad(out m_Resources, out m_ResourceError);
                if (!string.IsNullOrEmpty(m_ResourceError)) Mod.TryWarn("Polygon custom shader unavailable: " + m_ResourceError);
            }
            reason = m_ResourceError;
            m_DisposeResources = false;
            return m_Resources != null;
        }

        internal bool Matches(int id, object plan, MedianSavedSurface surface, out bool accepted, out string reason,
            PolygonTerrainLayer terrainLayer = PolygonTerrainLayer.None, float textureTiling = 1, List<RoadGrassTaperGeometry.Lane> crosswalks = null)
        {
            accepted = false; reason = "";
            if (!m_Pending.TryGetValue(id, out var entry)) m_Entries.TryGetValue(id, out entry);
            if (entry == null || !ReferenceEquals(entry.Plan, plan) || entry.Surface != surface || entry.TerrainLayer != terrainLayer || entry.TextureTiling != textureTiling) return false;
            if ((entry.Crosswalks == null) != (crosswalks == null)) return false;
            if (crosswalks != null)
            {
                if (entry.Crosswalks.Count != crosswalks.Count) return false;
                for (int i = 0; i < crosswalks.Count; i++)
                    if (!entry.Crosswalks[i].Curve.Equals(crosswalks[i].Curve) || entry.Crosswalks[i].Width != crosswalks[i].Width) return false;
            }
            if (terrainLayer != PolygonTerrainLayer.None)
            {
                RoadGrassTerrainTextures.TryGet(terrainLayer, out var diffuse, out var normal);
                if (entry.TerrainDiffuseId != (diffuse != null ? diffuse.GetInstanceID() : 0) ||
                    entry.TerrainNormalId != (normal != null ? normal.GetInstanceID() : 0)) return false;
            }
            accepted = entry.Error == null;
            reason = entry.Error;
            return true;
        }

        internal bool IsReady(int id) => !m_Remove.Contains(id) && !m_Pending.ContainsKey(id) &&
            m_Entries.TryGetValue(id, out var entry) && entry.Group >= 0 && entry.Error == null;
        internal void SurfaceMaterialsReady()
        {
            foreach (var entry in m_Entries.Values) if (entry.TerrainLayer != PolygonTerrainLayer.None) entry.TerrainDiffuseId = -1;
            foreach (var entry in m_Pending.Values) if (entry.TerrainLayer != PolygonTerrainLayer.None) entry.TerrainDiffuseId = -1;
            RoadGrassJoinCompositionSystem.Instance?.SurfaceMaterialsReady();
        }

        internal bool TryTerrainMaterial(Material template, PolygonTerrainLayer layer, bool legacy, out Material material) =>
            m_Resources.TryTerrainMaterial(template, layer, legacy, out material);
        internal void CopyMedianMaterials(EntityManager manager, PrefabSystem prefabs, RoadGrassResolvedMedianAnchor source,
            RoadGrassResolvedMedianAnchor curbSource, RoadGrassPolygonPrototypeMesh.Profile profile, bool useGrass,
            out Material pavement, out Material grass, out List<Colossal.IO.AssetDatabase.TextureAsset> textures, out List<RenderTexture> fillTextures) =>
            m_Resources.CopyMedianMaterials(manager, prefabs, source, curbSource, profile, useGrass, out pavement, out grass, out textures, out fillTextures);

        private Entry GetPickEntry(int id)
        {
            if (m_Remove.Contains(id)) return null;
            // The displayed mesh remains selectable while a replacement is queued.
            if (m_Entries.TryGetValue(id, out var active) && active.Error == null && active.PickVertices != null) return active;
            return m_Pending.TryGetValue(id, out var pending) && pending.Error == null ? pending : null;
        }

        internal bool HasPickGeometry(int id) => GetPickEntry(id) != null;
        internal IEnumerable<Line3.Segment> GetPickOutline(int id) => GetPickEntry(id)?.Outline ?? (IEnumerable<Line3.Segment>)Array.Empty<Line3.Segment>();

        internal bool TryPick(int id, Line3.Segment ray, out float hit)
        {
            hit = float.MaxValue;
            var entry = GetPickEntry(id);
            if (entry == null) return false;
            if (!RoadGrassMedianPickIndex.Hit(entry.Bounds, ray, 1, out _)) return false;
            var local = new Line3.Segment(ray.a - entry.Origin, ray.b - entry.Origin);
            bool found = false;
            hit = entry.PickIndex.Query(local, (triangle, best) =>
            {
                int i = triangle * 3;
                if (!RoadGrassPolygonSections.RayTriangle(local, entry.PickVertices[entry.PickTriangles[i]],
                    entry.PickVertices[entry.PickTriangles[i + 1]], entry.PickVertices[entry.PickTriangles[i + 2]], out float score) || score > best) return best;
                found = true; return score;
            });
            if (!found) hit = float.MaxValue;
            return hit < float.MaxValue;
        }

        internal int PickClosest(Line3.Segment ray, out float distance)
        {
            int id = 0;
            distance = m_PickIndex?.Query(ray, (item, best) =>
            {
                int candidate = m_PickIds[item];
                if (TryPick(candidate, ray, out float hit) && hit <= best) { id = candidate; return hit; }
                return best;
            }) ?? float.MaxValue;
            if (id == 0) distance = float.MaxValue;
            return id;
        }

        internal bool TryRetile(int id, float tiling)
        {
            if (m_Pending.ContainsKey(id) || !m_Entries.TryGetValue(id, out var entry) || entry.Error != null ||
                entry.Grass == null || entry.InteriorUVs == null || entry.OriginalTiling <= 0) return false;
            float ratio = tiling / entry.OriginalTiling;
            bool native = (entry.GrassMaterial ?? m_Resources.Grass).shader.name == "BH/NetCompositionMeshLitShader";
            float minimum = 0;
            if (native) foreach (int index in entry.Grass.triangles) minimum = math.min(minimum, entry.InteriorUVs[index].y * ratio);
            float shift = native ? math.ceil(-minimum) : 0;
            var uv = new List<Vector4>(entry.InteriorUVs.Length);
            foreach (var point in entry.InteriorUVs) uv.Add(new Vector4(point.x * ratio, point.y * ratio + shift, native ? .5f : 0, 0));
            entry.Grass.SetUVs(0, uv); entry.TextureTiling = tiling;
            return true;
        }

        internal void Submit(int id, object plan, MedianSavedSurface surface, Mesh mesh, float3 origin, bool grass, List<Line3.Segment> outline,
            Material pavementMaterial, Material grassMaterial, List<Colossal.IO.AssetDatabase.TextureAsset> materialTextures,
            List<RenderTexture> fillTextures, PolygonTerrainLayer terrainLayer = PolygonTerrainLayer.None, float textureTiling = 1, List<RoadGrassTaperGeometry.Lane> crosswalks = null,
            (Vector3[] Vertices, int[] Triangles, Bounds Bounds)? selection = null)
        {
            if (m_Pending.TryGetValue(id, out var old)) DestroyMeshes(old);
            var bounds = selection?.Bounds ?? mesh.bounds;
            RoadGrassTerrainTextures.TryGet(terrainLayer, out var terrainDiffuse, out var terrainNormal);
            var entry = new Entry { Plan = plan, Surface = surface, TerrainLayer = terrainLayer, TextureTiling = textureTiling, Crosswalks = crosswalks, Pavement = mesh, Origin = origin,
                TerrainDiffuseId = terrainDiffuse != null ? terrainDiffuse.GetInstanceID() : 0,
                TerrainNormalId = terrainNormal != null ? terrainNormal.GetInstanceID() : 0,
                Bounds = new Bounds3((float3)bounds.min + origin, (float3)bounds.max + origin), UseGrass = grass,
                PickVertices = id > 0 ? selection?.Vertices ?? mesh.vertices : Array.Empty<Vector3>(),
                PickTriangles = id > 0 ? selection?.Triangles ?? mesh.triangles : Array.Empty<int>(), Outline = outline,
                PavementMaterial = pavementMaterial, GrassMaterial = grassMaterial, MaterialTextures = materialTextures, FillTextures = fillTextures };
            var triangleBounds = new Bounds3[entry.PickTriangles.Length / 3];
            for (int i = 0; i < triangleBounds.Length; i++)
            {
                float3 a = entry.PickVertices[entry.PickTriangles[i * 3]], b = entry.PickVertices[entry.PickTriangles[i * 3 + 1]], c = entry.PickVertices[entry.PickTriangles[i * 3 + 2]];
                // Match RayTriangle's edge tolerance at shared triangle boundaries.
                float3 padding = (math.abs(b - a) + math.abs(c - a)) * .0002f + .00001f;
                triangleBounds[i] = new Bounds3(math.min(a, math.min(b, c)) - padding, math.max(a, math.max(b, c)) + padding);
            }
            entry.PickIndex = new RoadGrassMedianPickIndex(triangleBounds);
            m_Pending[id] = entry;
            m_Remove.Remove(id);
        }

        internal void Remove(int id)
        {
            if (m_Pending.TryGetValue(id, out var entry)) { DestroyMeshes(entry); m_Pending.Remove(id); }
            if (m_Entries.ContainsKey(id)) m_Remove.Add(id);
        }

        internal void Clear()
        {
            foreach (var entry in m_Pending.Values) DestroyMeshes(entry);
            m_Pending.Clear();
            foreach (int id in m_Entries.Keys) m_Remove.Add(id);
            m_DisposeResources = true;
        }

        internal void Shutdown()
        {
            Clear();
            if (m_Entries.Count != 0) OnUpdate();
            FinishCleanup();
        }

        protected override void OnGamePreload(Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            Clear();
        }

        protected override void OnUpdate()
        {
            RoadGrassTerrainTextures.PumpCompletions();
            if (m_BatchManager.CheckPropertyUpdates()) WriteRevision++;
            if (!RoadGrassSettings.VerboseLoggingEnabled) m_ReportPending = false;
            if (m_Pending.Count == 0 && m_Remove.Count == 0 && !m_ReportPending) return;
            // Structural changes are bounded to commit/edit/remove/load events, never hover or ordinary simulation.
            var groups = m_BatchManager.GetNativeBatchGroups(false, out var groupDeps);
            var instances = m_BatchManager.GetNativeBatchInstances(false, out var instanceDeps);
            var subBatches = m_BatchManager.GetNativeSubBatches(false, out var subDeps);
            JobHandle.CombineDependencies(Dependency, JobHandle.CombineDependencies(groupDeps, instanceDeps, subDeps)).Complete();
            // One audit after registration, once vanilla has allocated buffers and calculated visibility.
            if (m_ReportPending)
            {
                m_ReportPending = false;
                foreach (var pair in m_Entries)
                {
                    var entry = pair.Value;
                    if (!entry.ReportPending || entry.Error != null || m_Remove.Contains(pair.Key)) continue;
                    entry.ReportPending = false;
                    if (entry.Group < 0 || !groups.IsValidGroup(entry.Group) || instances.GetInstanceCount(entry.Group) == 0)
                    {
                        Mod.TryWarn($"[PolygonRender] id={pair.Key} no active batch instance");
                        continue;
                    }
                    var data = groups.GetGroupData(entry.Group);
                    var culling = instances.GetCullingData(entry.Group, 0);
                    instances.GetBounds(entry.Group, out var center, out var extents);
                    bool hasTransform = instances.AsParallelInstanceWriter().GetTransformValue(entry.Group, 0, out var transform, out _);
                    var material = entry.PavementMaterial ?? m_Resources.Pavement;
                    Mod.TryWarn($"[PolygonRender] id={pair.Key} group={entry.Group} lod={data.m_LodCount} range={culling.lodRange} hidden={culling.isHidden} bounds={center}/{extents} transform={hasTransform}/{transform.c3} shader={material.shader.name} queue={material.renderQueue} gbuffer={material.GetShaderPassEnabled("GBuffer")} batches={groups.GetBatchCount(entry.Group)}");
                }
            }
            foreach (int id in m_Remove)
                if (m_Entries.TryGetValue(id, out var old)) { Release(old, groups, instances, subBatches); m_Entries.Remove(id); }
            m_Remove.Clear();
            foreach (var request in m_Pending)
            {
                if (m_Entries.TryGetValue(request.Key, out var old)) Release(old, groups, instances, subBatches);
                var entry = request.Value;
                try { Register(entry, groups, instances, subBatches); entry.ReportPending = m_ReportPending = RoadGrassSettings.VerboseLoggingEnabled; }
                catch (Exception exception)
                {
                    Release(entry, groups, instances, subBatches);
                    entry.Error = "Polygon mesh prototype: " + exception.Message;
                }
                m_Entries[request.Key] = entry;
                RoadGrassJoinCompositionSystem.Instance?.PolygonPrototypeChanged(request.Key, entry.Error);
            }
            m_Pending.Clear();
            var ids = new List<int>(); var bounds = new List<Bounds3>();
            foreach (var pair in m_Entries)
                if (pair.Key > 0 && pair.Value.Error == null) { ids.Add(pair.Key); bounds.Add(pair.Value.Bounds); }
            m_PickIds = ids.ToArray(); m_PickIndex = new RoadGrassMedianPickIndex(bounds.ToArray());
            WriteRevision++;
            m_Writes.Clear();
            foreach (var entry in m_Entries.Values)
                if (entry.Group >= 0 && entry.Error == null)
                    m_Writes.Add(new Write { Group = entry.Group, MeshEntity = entry.MeshEntity, Carrier = entry.Carrier,
                        Origin = entry.Origin, Bounds = entry.Bounds });
            m_BatchManager.AddNativeBatchGroupsWriter(default);
            m_BatchManager.AddNativeBatchInstancesWriter(default);
            m_BatchManager.AddNativeSubBatchesWriter(default);
        }

        private void Register(Entry entry, NativeBatchGroups<CullingData, GroupData, BatchData, InstanceData> groups,
            NativeBatchInstances<CullingData, GroupData, BatchData, InstanceData> instances,
            NativeSubBatches<CullingData, GroupData, BatchData, InstanceData> subBatches)
        {
            if (m_Resources == null) throw new InvalidOperationException("Polygon shader is unavailable");
            // A crosswalk can cover the whole polygon; retain its saved shape and picking without an empty draw batch.
            if (entry.Pavement.GetIndexCount(0) == 0 && entry.Pavement.GetIndexCount(1) == 0) return;
            // CustomBatch draws submesh zero. Split resources instead of touching internal setters.
            if (entry.Pavement.GetIndexCount(1) > 0)
            {
                entry.Grass = UnityEngine.Object.Instantiate(entry.Pavement);
                entry.Grass.name = "Median Controller polygon interior mesh";
                var triangles = entry.Grass.GetTriangles(1);
                entry.Grass.subMeshCount = 1;
                entry.Grass.SetTriangles(triangles, 0);
            }
            entry.Pavement.subMeshCount = 1;
            // Vanilla net shaders consume normalized composition coordinates and matrix selectors,
            // not ordinary local-space vertices. Picking keeps the original CPU coordinates.
            var localBounds = new Bounds3(entry.Bounds.min - entry.Origin, entry.Bounds.max - entry.Origin);
            EncodeComposition(entry.Pavement, entry.PavementMaterial ?? m_Resources.Pavement, localBounds);
            if (entry.Grass != null)
            {
                entry.InteriorUVs = entry.Grass.uv; entry.OriginalTiling = entry.TextureTiling;
                EncodeComposition(entry.Grass, entry.GrassMaterial ?? (entry.UseGrass ? m_Resources.Grass : m_Resources.Pavement), localBounds);
            }
            // No PrefabRef or serializable mod component: these entities are excluded from saved city state.
            entry.MeshEntity = EntityManager.CreateEntity(ComponentType.ReadWrite<NetCompositionMeshData>());
            EntityManager.SetComponentData(entry.MeshEntity, new NetCompositionMeshData { m_State = MeshFlags.Default });
            entry.Carrier = EntityManager.CreateEntity();
            EntityManager.AddBuffer<MeshBatch>(entry.Carrier);
            var data = new GroupData { m_Mesh = entry.MeshEntity, m_Layer = MeshLayer.Default,
                m_MeshType = MeshType.Net, m_LodCount = 0,
                m_RenderFlags = BatchRenderFlags.IsEnabled | BatchRenderFlags.ReceiveShadows | BatchRenderFlags.CastShadows };
            for (int i = 0; i < GroupData.MAX_PROPERTY_COUNT; i++) data.SetPropertyIndex(i, -1);
            entry.Group = groups.CreateGroup(data, 2, instances, subBatches);
            Add(entry.Pavement, entry.PavementMaterial ?? m_Resources.Pavement);
            if (entry.Grass != null) Add(entry.Grass, entry.GrassMaterial ?? (entry.UseGrass ? m_Resources.Grass : m_Resources.Pavement));
            // Every batch and its property layout must exist before creating the instance.
            int instance = instances.AddInstance(new InstanceData { m_Entity = entry.Carrier, m_MeshGroup = byte.MaxValue },
                // m_LodCount is the highest LOD index, so one LOD uses index/range zero.
                new CullingData { m_Bounds = entry.Bounds, lodFade = 255, lodRange = new int4(0, 1, 0, 1) }, entry.Group, subBatches);
            EntityManager.GetBuffer<MeshBatch>(entry.Carrier).Add(new MeshBatch { m_GroupIndex = entry.Group,
                m_InstanceIndex = instance, m_MeshGroup = byte.MaxValue });
            World.GetOrCreateSystemManaged<RenderingSystem>().SetShaderEnabled(m_Resources.Pavement.shader, true);

            void Add(Mesh mesh, Material material)
            {
                if (mesh.GetIndexCount(0) == 0) return;
                World.GetOrCreateSystemManaged<RenderingSystem>().SetShaderEnabled(material.shader, true);
                int batchIndex = groups.CreateBatch(new BatchData { m_LodMesh = entry.MeshEntity,
                    m_VTIndex0 = -1, m_VTIndex1 = -1, m_RenderFlags = data.m_RenderFlags,
                    m_ShadowCastingMode = (byte)ShadowCastingMode.On }, entry.Group, 2, instances, subBatches);
                var batch = new CustomBatch(entry.Group, batchIndex, null, material, material, material, mesh,
                    entry.MeshEntity, entry.MeshEntity, 0, GeneratedType.None, MeshType.Net, 0, new MaterialPropertyBlock());
                var managed = m_BatchManager.GetManagedBatches();
                int managedIndex = managed.AddBatch(batch, batchIndex, groups);
                // Default mesh state initializes native loading/priority tables without requesting prefab streaming.
                m_BatchMeshes.AddBatch(batch, managedIndex);
                var properties = managed.GetBatchProperties(material.shader, new OptionalProperties(0, MeshType.Net));
                groups.SetBatchProperties(entry.Group, batchIndex, properties);
                managed.SetDefaults(material, batch.customProps, properties, groups.GetBatchDefaultsAccessor(entry.Group, batchIndex));
            }
        }

        private void Release(Entry entry, NativeBatchGroups<CullingData, GroupData, BatchData, InstanceData> groups,
            NativeBatchInstances<CullingData, GroupData, BatchData, InstanceData> instances,
            NativeSubBatches<CullingData, GroupData, BatchData, InstanceData> subBatches)
        {
            if ((uint)entry.Group < (uint)groups.GetGroupCount() && groups.IsValidGroup(entry.Group) &&
                groups.GetGroupData(entry.Group).m_Mesh == entry.MeshEntity)
                groups.DestroyGroup(entry.Group, instances, subBatches);
            // Vanilla BatchRendererSystem consumes obsolete managed batches and unregisters IDs exactly once.
            entry.Group = -1;
            if (EntityManager.Exists(entry.Carrier)) EntityManager.DestroyEntity(entry.Carrier);
            if (EntityManager.Exists(entry.MeshEntity)) EntityManager.DestroyEntity(entry.MeshEntity);
            entry.Carrier = entry.MeshEntity = Entity.Null;
            DestroyMeshes(entry);
        }

        private static void DestroyMeshes(Entry entry)
        {
            RoadGrassPolygonPrototypeResources.ReleaseMaterial(entry.PavementMaterial);
            RoadGrassPolygonPrototypeResources.ReleaseMaterial(entry.GrassMaterial);
            entry.PavementMaterial = entry.GrassMaterial = null;
            if (entry.FillTextures != null) foreach (var tile in entry.FillTextures) UnityEngine.Object.Destroy(tile);
            entry.FillTextures = null;
            if (entry.MaterialTextures != null)
                foreach (var texture in entry.MaterialTextures) texture.Unload();
            entry.MaterialTextures = null;
            if (entry.Pavement != null) UnityEngine.Object.Destroy(entry.Pavement);
            if (entry.Grass != null) UnityEngine.Object.Destroy(entry.Grass);
            entry.Pavement = entry.Grass = null;
        }

        private static void EncodeComposition(Mesh mesh, Material material, Bounds3 bounds)
        {
            if (material.shader.name != "BH/NetCompositionMeshLitShader") return;
            var vertices = mesh.vertices;
            var colors = new Color32[vertices.Length];
            var uv = new List<Vector4>(vertices.Length);
            var oldUV = mesh.uv;
            float minimumV = 0;
            // Split meshes still contain the other submesh's unused vertices. Only drawn UVs
            // determine this shift, otherwise world-space grass UVs inflate curb coordinates.
            foreach (int index in mesh.triangles) minimumV = math.min(minimumV, oldUV[index].y);
            // Negative V selects the shader's fractional UV branch. Nonnegative V uses curve.w.
            // Shift by whole texture periods so the direct polygon mapping keeps its phase.
            float vShift = math.ceil(-minimumV);
            float2 size = math.max(bounds.max.xz - bounds.min.xz, new float2(.0001f));
            for (int i = 0; i < vertices.Length; i++)
            {
                float3 vertex = vertices[i];
                vertex.xz = (vertex.xz - bounds.min.xz) / size;
                vertices[i] = vertex;
                colors[i] = new Color32(0, 2, 0, 0);
                uv.Add(new Vector4(oldUV[i].x, oldUV[i].y + vShift, .5f, 0));
            }
            mesh.vertices = vertices;
            mesh.colors32 = colors;
            mesh.SetUVs(0, uv);
            mesh.RecalculateBounds();
        }

        internal void FinishCleanup()
        {
            if (!m_DisposeResources || m_Entries.Count != 0 || m_Pending.Count != 0) return;
            m_Resources?.Dispose(); m_Resources = null;
            m_LoadAttempted = m_DisposeResources = false; m_ResourceError = null;
        }

        protected override void OnDestroy()
        {
            Shutdown();
            m_Resources?.Dispose();
            if (m_Writes.IsCreated) m_Writes.Dispose();
            if (Instance == this) Instance = null;
            base.OnDestroy();
        }
    }

    [Preserve]
    internal partial class RoadGrassPolygonPrototypeDataSystem : GameSystemBase
    {
        private BatchManagerSystem m_BatchManager;
        private int m_LastWriteRevision = -1, m_WritePasses;
        protected override void OnCreate() { base.OnCreate(); m_BatchManager = World.GetOrCreateSystemManaged<BatchManagerSystem>(); }

        [BurstCompile]
        private struct WriteJob : IJobParallelFor
        {
            [ReadOnly] internal NativeArray<RoadGrassPolygonPrototypeBatchSystem.Write> Writes;
            [ReadOnly] internal NativeBatchGroups<CullingData, GroupData, BatchData, InstanceData> Groups;
            internal NativeBatchInstances<CullingData, GroupData, BatchData, InstanceData> Instances;
            public void Execute(int index)
            {
                var write = Writes[index];
                if ((uint)write.Group >= (uint)Groups.GetGroupCount() || !Groups.IsValidGroup(write.Group) ||
                    Groups.GetGroupData(write.Group).m_Mesh != write.MeshEntity || Instances.GetInstanceCount(write.Group) != 1 ||
                    Instances.GetInstanceData(write.Group, 0).m_Entity != write.Carrier) return;
                var writer = Instances.AsParallelInstanceWriter();
                var transform = new float3x4(new float3(1, 0, 0), new float3(0, 1, 0), new float3(0, 0, 1), write.Origin);
                // Native rendering settings may reallocate buffers. Cheap comparisons repair that without rebuilding geometry.
                // Keep both model allocations valid if native loading state selects either one.
                bool same = writer.GetTransformValue(write.Group, 0, out var current, out var secondary) &&
                    current.Equals(transform) && secondary.Equals(transform);
                if (!same) writer.SetTransformValue(transform, transform, write.Group, 0);
                var group = Groups.GetGroupData(write.Group);
                float3 low = write.Bounds.min - write.Origin, high = write.Bounds.max - write.Origin;
                var left = CompositionCurve(low.x, low.z, high.z);
                var right = CompositionCurve(high.x, low.z, high.z);
                Set(NetProperty.CompositionMatrix0, left);
                Set(NetProperty.CompositionMatrix1, left);
                Set(NetProperty.CompositionMatrix2, right);
                Set(NetProperty.CompositionMatrix3, right);
                Set(NetProperty.CompositionMatrix4, left);
                Set(NetProperty.CompositionMatrix5, left);
                Set(NetProperty.CompositionMatrix6, right);
                Set(NetProperty.CompositionMatrix7, right);
                for (int i = 0; i < 4; i++)
                    if (group.GetPropertyIndex((int)NetProperty.CompositionSync0 + i, out int property))
                        writer.SetPropertyValue(new float4(.2f, .4f, .6f, .8f), write.Group, property, 0);
                ref var culling = ref writer.AccessCullingData(write.Group, 0);
                culling.m_Bounds = write.Bounds;
                culling.isHidden = false;

                void Set(NetProperty property, float4x4 value)
                {
                    if (group.GetPropertyIndex((int)property, out int index))
                        writer.SetPropertyValue(value, write.Group, index, 0);
                }
            }

            private static float4x4 CompositionCurve(float x, float start, float end)
            {
                // Same control-point/mapping layout as native BatchDataHelpers.BuildEdgeMatrix.
                float length = end - start;
                // Polygon UVs already contain their mapped distance; keep the tiling channel at one.
                return new float4x4(new float4(x, 0, start, 1),
                    new float4(x, 0, start + length / 3, 1),
                    new float4(x, 0, start + length * (2f / 3), 1),
                    new float4(x, 0, end, 1));
            }
        }

        protected override void OnUpdate()
        {
            var system = RoadGrassPolygonPrototypeBatchSystem.Instance;
            if (system == null) return;
            system.FinishCleanup();
            if (m_LastWriteRevision != system.WriteRevision)
            { m_LastWriteRevision = system.WriteRevision; m_WritePasses = 2; }
            if (m_WritePasses == 0) return;
            if (system.Writes.Length == 0) return;
            var groups = m_BatchManager.GetNativeBatchGroups(true, out var groupDeps);
            var instances = m_BatchManager.GetNativeBatchInstances(false, out var instanceDeps);
            Dependency = new WriteJob { Writes = system.Writes, Groups = groups, Instances = instances }.Schedule(
                system.Writes.Length, 32, JobHandle.CombineDependencies(Dependency, groupDeps, instanceDeps));
            m_BatchManager.AddNativeBatchGroupsReader(Dependency);
            m_BatchManager.AddNativeBatchInstancesWriter(Dependency);
            m_WritePasses--;
        }

        protected override void OnDestroy() { Dependency.Complete(); base.OnDestroy(); }
    }
}
