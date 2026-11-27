using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Colossal.Entities;
using Colossal.Json;
using Colossal.Mathematics;
using Game.Common;
using Game.Input;
using Game.Net;
using Game.Prefabs;
using Game.Rendering;
using Game.Tools;
using Game.UI.Localization;
using Game.UI.Tooltip;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Scripting;

namespace RoadGrass
{
    // Temporary, read-only developer tool. No median systems or save components are involved.
    [Preserve]
    internal partial class MedianIntersectionCaptureTool : ToolBaseSystem
    {
        internal static MedianIntersectionCaptureTool Instance { get; private set; }
        internal Entity Hovered { get; private set; }
        internal static string Status = "Select Capture intersection, then click a highlighted road junction.";
        internal static string Folder => Path.Combine(Application.persistentDataPath, "ModsData", "MedianController", "IntersectionCaptures");
        private OverlayRenderSystem m_Overlay;
        private Task m_Write;
        public override string toolID => "MedianControllerIntersectionCapture";
        public override PrefabBase GetPrefab() => null;
        public override bool TrySetPrefab(PrefabBase prefab) => false;

        protected override void OnCreate()
        {
            base.OnCreate();
            Instance = this;
            m_Overlay = World.GetOrCreateSystemManaged<OverlayRenderSystem>();
            Enabled = false;
        }

        internal void Activate() => m_ToolSystem.activeTool = this;
        internal void Deactivate()
        {
            if (m_ToolSystem.activeTool == this) m_ToolSystem.activeTool = m_DefaultToolSystem;
        }

        internal static void OpenFolder()
        {
            try { Directory.CreateDirectory(Folder); Colossal.RemoteProcess.OpenFolder(Path.GetFullPath(Folder)); }
            catch (Exception e) { Status = "Cannot open captures folder: " + e.Message; }
        }

        public override void InitializeRaycast()
        {
            base.InitializeRaycast();
            m_ToolRaycastSystem.typeMask = TypeMask.Net;
            m_ToolRaycastSystem.netLayerMask = Layer.Road;
            m_ToolRaycastSystem.collisionMask = CollisionMask.OnGround | CollisionMask.Overground | CollisionMask.Underground;
        }

        protected override void OnStartRunning()
        {
            base.OnStartRunning();
            applyAction.shouldBeEnabled = true;
            cancelAction.shouldBeEnabled = true;
            Hovered = Entity.Null;
        }

        protected override void OnStopRunning()
        {
            Hovered = Entity.Null;
            base.OnStopRunning();
        }

        protected override void OnDestroy()
        {
            if (Instance == this) Instance = null;
            base.OnDestroy();
        }

        protected override JobHandle OnUpdate(JobHandle inputDeps)
        {
            if (m_ToolSystem.activeTool != this) return inputDeps;
            if (m_FocusChanged) return inputDeps;
            if (cancelAction.WasPressedThisFrame()) { Deactivate(); return inputDeps; }
            Hovered = Entity.Null;
            if (GetRaycastResult(out Entity hitEntity, out RaycastHit hit))
            {
                // Traffic selects compatible nodes. Also accept the adjacent road surface at wide junctions.
                for (int i = 0; i < 4 && hitEntity != Entity.Null; i++)
                {
                    if (Compatible(hitEntity)) { Hovered = hitEntity; break; }
                    if (EntityManager.TryGetComponent(hitEntity, out Edge edge))
                    {
                        float nearest = 40 * 40;
                        TryNode(edge.m_Start, hit.m_HitPosition, ref nearest);
                        TryNode(edge.m_End, hit.m_HitPosition, ref nearest);
                        break;
                    }
                    if (!EntityManager.TryGetComponent(hitEntity, out Owner owner)) break;
                    hitEntity = owner.m_Owner;
                }
            }
            if (Hovered == Entity.Null) return inputDeps;
            if (applyAction.WasReleasedThisFrame() && (m_Write == null || m_Write.IsCompleted))
            {
                try
                {
                    // Snapshot ECS on its owning thread; the worker only receives JSON and a destination.
                    inputDeps.Complete();
                    Dependency.Complete();
                    var snapshot = IntersectionCapture.Read(EntityManager, World.GetExistingSystemManaged<PrefabSystem>(), Hovered);
                    foreach (var connection in EntityManager.GetBuffer<ConnectedEdge>(Hovered, true))
                    {
                        Entity road = connection.m_Edge;
                        if (!EntityManager.TryGetComponent(road, out Edge edge) || EntityManager.HasComponent<Deleted>(road) || EntityManager.HasComponent<Temp>(road)) continue;
                        if (!RoadGrassMedianAnchorResolver.TryResolveAll(EntityManager, road, edge.m_End == Hovered,
                            GetBufferLookup<NetSubSection>(true), GetBufferLookup<NetSectionPiece>(true),
                            GetComponentLookup<NetPieceData>(true), GetComponentLookup<NetVertexMatchData>(true),
                            out var anchors, out _, prefabs: World.GetExistingSystemManaged<PrefabSystem>())) continue;
                        foreach (var anchor in anchors)
                            if (RoadGrassMedianGeometry.TryGetOrientedApproach(EntityManager, anchor, out var approach))
                                snapshot.medianEnds.Add(new IntersectionCapture.MedianEnd { road = road.ToString(), ordinal = anchor.Key.Ordinal,
                                    leftBoundary = new IntersectionCapture.NamedCurve("median.left", approach.m_Left),
                                    rightBoundary = new IntersectionCapture.NamedCurve("median.right", approach.m_Right),
                                    left = new IntersectionCapture.Point(approach.m_Left.d), right = new IntersectionCapture.Point(approach.m_Right.d),
                                    centre = new IntersectionCapture.Point((approach.m_Left.d + approach.m_Right.d) * .5f) });
                    }
                    string json = JSON.Dump(snapshot, EncodeOptions.PreserveUnicode);
                    string folder = Folder;
                    string name = $"intersection-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.json";
                    Status = "Writing intersection capture...";
                    m_Write = Task.Run(() =>
                    {
                        string temporary = Path.Combine(folder, name + ".tmp");
                        try
                        {
                            Directory.CreateDirectory(folder);
                            File.WriteAllText(temporary, json, new UTF8Encoding(false));
                            File.Move(temporary, Path.Combine(folder, name));
                            Status = "Capture saved: " + name;
                        }
                        catch (Exception e)
                        {
                            Status = "Capture failed: " + e.Message;
                            try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                        }
                    });
                }
                catch (Exception e) { Status = "Capture failed: " + e.Message; }
            }
            var buffer = m_Overlay.GetBuffer(out JobHandle writers);
            var job = new Highlight { Buffer = buffer, Position = EntityManager.GetComponentData<Node>(Hovered).m_Position };
            JobHandle result = job.Schedule(JobHandle.CombineDependencies(inputDeps, writers));
            m_Overlay.AddBufferWriter(result);
            return result;
        }

        private void TryNode(Entity node, float3 hit, ref float nearest)
        {
            if (!Compatible(node)) return;
            float distance = math.distancesq(EntityManager.GetComponentData<Node>(node).m_Position, hit);
            if (distance >= nearest) return;
            nearest = distance;
            Hovered = node;
        }

        private bool Compatible(Entity node)
        {
            if (!EntityManager.HasComponent<Node>(node) || EntityManager.HasComponent<Deleted>(node) ||
                EntityManager.HasComponent<Temp>(node) || !EntityManager.HasBuffer<ConnectedEdge>(node)) return false;
            int roads = 0;
            foreach (var entry in EntityManager.GetBuffer<ConnectedEdge>(node, true))
                if (EntityManager.HasComponent<Edge>(entry.m_Edge) && EntityManager.HasComponent<Game.Net.Road>(entry.m_Edge) &&
                    !EntityManager.HasComponent<Deleted>(entry.m_Edge) && !EntityManager.HasComponent<Temp>(entry.m_Edge)) roads++;
            return roads >= 2;
        }

        private struct Highlight : IJob
        {
            internal OverlayRenderSystem.Buffer Buffer;
            internal float3 Position;
            public void Execute()
            {
                var color = new Color(.1f, .85f, 1f, 1f);
                for (int i = 0; i < 48; i++)
                {
                    float a = i * math.PI * 2 / 48, b = (i + 1) * math.PI * 2 / 48;
                    Buffer.DrawLine(color, new Line3.Segment(Position + new float3(math.cos(a) * 8, .2f, math.sin(a) * 8),
                        Position + new float3(math.cos(b) * 8, .2f, math.sin(b) * 8)), .25f);
                }
            }
        }
    }

    [Preserve]
    internal partial class MedianIntersectionCaptureTooltip : TooltipSystemBase
    {
        protected override void OnUpdate()
        {
            var tool = MedianIntersectionCaptureTool.Instance;
            if (tool == null || World.GetExistingSystemManaged<ToolSystem>()?.activeTool != tool) return;
            AddMouseTooltip(new StringTooltip
            {
                path = "MedianController.IntersectionCapture",
                value = LocalizedString.Value((tool.Hovered == Entity.Null ? "Hover over a road intersection. " : "Click to capture highlighted intersection. ") +
                    "Cancel exits. " + MedianIntersectionCaptureTool.Status)
            });
        }
    }

    internal static class IntersectionCapture
    {
        [Serializable] internal struct Point
        {
            public float x, y, z;
            internal Point(float3 p) { x = p.x; y = p.y; z = p.z; }
        }
        [Serializable] internal sealed class NamedCurve
        {
            public string name;
            public Point[] points;
            internal NamedCurve(string name, Bezier4x3 curve)
            {
                if (!math.all(math.isfinite(curve.a)) || !math.all(math.isfinite(curve.b)) ||
                    !math.all(math.isfinite(curve.c)) || !math.all(math.isfinite(curve.d))) throw new InvalidDataException("Non-finite " + name + " curve");
                this.name = name;
                points = new[] { new Point(curve.a), new Point(curve.b), new Point(curve.c), new Point(curve.d) };
            }
        }
        [Serializable] internal sealed class Lane
        {
            public string entity = "", owner = "", prefab = "", prefabName = "", laneFlags = "";
            public string sourceLane = "", targetLane = "", secondaryFlags = "";
            public int ordinal;
            public int sharedStartCount, sharedEndCount;
            public float width, startWidth, endWidth, curveLength;
            public bool car, pedestrian, track, marking, secondary, hidden, hasPlacementData, hasMeshBatches;
            public NamedCurve curve;
            public NamedCurve leftBoundary, rightBoundary;
            public string[] markingMeshes = Array.Empty<string>();
            public List<Range> cutRanges = new List<Range>();
            public List<MarkingMesh> markingGeometry = new List<MarkingMesh>();
            public Point secondaryPositionOffset;
        }
        [Serializable] internal struct Range
        {
            public float min, max;
        }
        [Serializable] internal sealed class MarkingMesh
        {
            public string name = "", flags = "", subMeshFlags = "";
            public int prefabMeshIndex, tilingCount;
            public bool active, inverted;
            public float smoothingDistance;
            public Point size, centre;
        }
        [Serializable] internal sealed class Road
        {
            public string entity = "", prefabName = "", start = "", end = "", compositionFlags = "", nodeCompositionFlags = "";
            public float width, middleOffset;
            public NamedCurve[] curves = Array.Empty<NamedCurve>();
        }
        [Serializable] internal sealed class Capture
        {
            public int version = 1;
            public string format = "MedianController.IntersectionCapture", capturedUtc = "", unityVersion = "", node = "";
            public string coordinateSystem = "CS2 world metres: x/z horizontal, y elevation. Raw cubic control points; no guide simplification.";
            public Point position;
            public List<Road> roads = new List<Road>();
            public List<Lane> lanes = new List<Lane>();
            public List<MedianEnd> medianEnds = new List<MedianEnd>();
            public List<string> warnings = new List<string>();
        }
        [Serializable] internal sealed class MedianEnd
        {
            public string road;
            public int ordinal;
            public Point left, centre, right;
            public NamedCurve leftBoundary, rightBoundary;
        }

        internal static Capture Read(EntityManager em, PrefabSystem prefabs, Entity node)
        {
            if (!math.all(math.isfinite(em.GetComponentData<Node>(node).m_Position))) throw new InvalidDataException("Non-finite junction position");
            var capture = new Capture { capturedUtc = DateTime.UtcNow.ToString("O"), unityVersion = Application.unityVersion,
                node = node.ToString(), position = new Point(em.GetComponentData<Node>(node).m_Position) };
            var seen = new HashSet<Entity>();
            ReadLanes(node);
            foreach (var connection in em.GetBuffer<ConnectedEdge>(node, true))
            {
                Entity entity = connection.m_Edge;
                if (!Live(entity) || !em.TryGetComponent(entity, out Edge edge)) continue;
                var road = new Road { entity = entity.ToString(), prefabName = Name(entity), start = edge.m_Start.ToString(), end = edge.m_End.ToString() };
                if (em.TryGetComponent(entity, out Composition composition))
                {
                    if (em.TryGetComponent(composition.m_Edge, out NetCompositionData data))
                    {
                        if (!math.isfinite(data.m_Width) || !math.isfinite(data.m_MiddleOffset)) throw new InvalidDataException("Non-finite road dimensions");
                        road.width = data.m_Width; road.middleOffset = data.m_MiddleOffset; road.compositionFlags = Flags(data);
                    }
                    if (em.TryGetComponent(edge.m_Start == node ? composition.m_StartNode : composition.m_EndNode, out NetCompositionData nodeData))
                        road.nodeCompositionFlags = Flags(nodeData);
                }
                var curves = new List<NamedCurve>();
                if (em.TryGetComponent(entity, out EdgeGeometry geometry))
                {
                    curves.AddRange(new[] { new NamedCurve("edge.start.left", geometry.m_Start.m_Left), new NamedCurve("edge.start.right", geometry.m_Start.m_Right),
                        new NamedCurve("edge.end.left", geometry.m_End.m_Left), new NamedCurve("edge.end.right", geometry.m_End.m_Right) });
                }
                else capture.warnings.Add("No EdgeGeometry: " + entity);
                if (edge.m_Start == node && em.TryGetComponent(entity, out StartNodeGeometry startGeometry)) AddNodeCurves(startGeometry.m_Geometry);
                else if (edge.m_End == node && em.TryGetComponent(entity, out EndNodeGeometry endGeometry)) AddNodeCurves(endGeometry.m_Geometry);
                else capture.warnings.Add("No junction-side geometry: " + entity);
                road.curves = curves.ToArray();
                capture.roads.Add(road);
                ReadLanes(entity);

                void AddNodeCurves(EdgeNodeGeometry nodeGeometry)
                {
                    curves.AddRange(new[] { new NamedCurve("node.left.left", nodeGeometry.m_Left.m_Left), new NamedCurve("node.left.right", nodeGeometry.m_Left.m_Right),
                        new NamedCurve("node.right.left", nodeGeometry.m_Right.m_Left), new NamedCurve("node.right.right", nodeGeometry.m_Right.m_Right),
                        new NamedCurve("node.middle", nodeGeometry.m_Middle) });
                }
            }
            if (capture.lanes.Count == 0) capture.warnings.Add("No live lane curves found; road geometry may still be updating.");
            return capture;

            bool Live(Entity entity) => em.Exists(entity) && !em.HasComponent<Deleted>(entity) && !em.HasComponent<Temp>(entity);
            string Flags(NetCompositionData data) => $"general={data.m_Flags.m_General}; left={data.m_Flags.m_Left}; right={data.m_Flags.m_Right}";
            string Name(Entity entity) => em.TryGetComponent(entity, out PrefabRef reference) && prefabs != null &&
                prefabs.TryGetPrefab<PrefabBase>(reference, out var prefab) && prefab != null ? prefab.name : "";
            void ReadLanes(Entity owner)
            {
                if (!em.HasBuffer<Game.Net.SubLane>(owner)) return;
                var buffer = em.GetBuffer<Game.Net.SubLane>(owner, true);
                for (int i = 0; i < buffer.Length; i++)
                {
                    Entity entity = buffer[i].m_SubLane;
                    if (!seen.Add(entity) || !Live(entity) || !em.TryGetComponent(entity, out Curve curve)) continue;
                    var lane = new Lane { entity = entity.ToString(), owner = owner.ToString(), ordinal = i, prefabName = Name(entity),
                        car = em.HasComponent<Game.Net.CarLane>(entity), pedestrian = em.HasComponent<Game.Net.PedestrianLane>(entity), track = em.HasComponent<Game.Net.TrackLane>(entity),
                        secondary = em.HasComponent<Game.Net.SecondaryLane>(entity), hidden = em.HasComponent<Hidden>(entity),
                        hasPlacementData = true, curveLength = curve.m_Length,
                        curve = new NamedCurve("lane", curve.m_Bezier) };
                    if (em.TryGetComponent(entity, out Game.Net.Lane path))
                    {
                        lane.sourceLane = PathIdentity(path.m_StartNode);
                        lane.targetLane = PathIdentity(path.m_EndNode);
                    }
                    if (em.HasBuffer<CutRange>(entity))
                        foreach (var cut in em.GetBuffer<CutRange>(entity, true))
                        {
                            if (!math.isfinite(cut.m_CurveDelta.min) || !math.isfinite(cut.m_CurveDelta.max) ||
                                cut.m_CurveDelta.min < 0 || cut.m_CurveDelta.max > 1 || cut.m_CurveDelta.min > cut.m_CurveDelta.max)
                                throw new InvalidDataException("Invalid lane cut range: " + entity);
                            lane.cutRanges.Add(new Range { min = cut.m_CurveDelta.min, max = cut.m_CurveDelta.max });
                        }
                    if (em.TryGetComponent(entity, out PrefabRef reference))
                    {
                        lane.prefab = reference.m_Prefab.ToString();
                        if (em.TryGetComponent(reference.m_Prefab, out NetLaneData data))
                        {
                            if (!math.isfinite(data.m_Width) || data.m_Width < 0) throw new InvalidDataException("Invalid lane width: " + entity);
                            lane.width = data.m_Width; lane.laneFlags = data.m_Flags.ToString();
                        }
                        lane.startWidth = lane.endWidth = lane.width;
                        if (em.TryGetComponent(entity, out NodeLane nodeLane))
                        {
                            lane.startWidth += nodeLane.m_WidthOffset.x;
                            lane.endWidth += nodeLane.m_WidthOffset.y;
                            lane.sharedStartCount = nodeLane.m_SharedStartCount;
                            lane.sharedEndCount = nodeLane.m_SharedEndCount;
                        }
                        if (!math.isfinite(lane.startWidth) || !math.isfinite(lane.endWidth) || lane.startWidth < 0 || lane.endWidth < 0)
                            throw new InvalidDataException("Invalid junction lane width: " + entity);
                        if (!lane.secondary && lane.width > 0 && math.distance(curve.m_Bezier.a.xz, curve.m_Bezier.d.xz) > .001f)
                        {
                            try
                            {
                                var halfWidth = new float2(lane.startWidth, lane.endWidth) * .5f;
                                lane.leftBoundary = new NamedCurve("native-offset.left", NetUtils.OffsetCurveLeftSmooth(curve.m_Bezier, halfWidth));
                                lane.rightBoundary = new NamedCurve("native-offset.right", NetUtils.OffsetCurveLeftSmooth(curve.m_Bezier, -halfWidth));
                            }
                            catch (InvalidDataException) { lane.leftBoundary = lane.rightBoundary = null; capture.warnings.Add("Cannot fit lane boundaries: " + entity); }
                        }
                        if (em.TryGetComponent(reference.m_Prefab, out SecondaryLaneData secondary))
                        { lane.secondaryPositionOffset = new Point(secondary.m_PositionOffset); lane.secondaryFlags = secondary.m_Flags.ToString(); }
                        if (prefabs != null && prefabs.TryGetPrefab<NetLaneGeometryPrefab>(reference, out var geometry) && geometry?.m_Meshes != null)
                        {
                            var markings = new List<string>();
                            foreach (var mesh in geometry.m_Meshes)
                                if (mesh?.m_Mesh != null && !mesh.m_RequireEditor && mesh.m_Mesh.TryGet<DecalProperties>(out var decal) &&
                                    (decal.m_LayerMask & DecalLayers.Roads) != 0) markings.Add(mesh.m_Mesh.name);
                            lane.marking = markings.Count > 0;
                            lane.markingMeshes = markings.ToArray();
                        }
                        // Renderer uses the selected SubMesh/MeshData, not every road-decal prefab entry.
                        if (em.HasBuffer<SubMesh>(reference.m_Prefab))
                        {
                            var activeMeshes = new HashSet<int>();
                            lane.hasMeshBatches = em.HasBuffer<MeshBatch>(entity);
                            if (lane.hasMeshBatches)
                                foreach (var batch in em.GetBuffer<MeshBatch>(entity, true)) activeMeshes.Add(batch.m_MeshIndex);
                            var meshes = em.GetBuffer<SubMesh>(reference.m_Prefab, true);
                            for (int meshIndex = 0; meshIndex < meshes.Length; meshIndex++)
                            {
                                var mesh = meshes[meshIndex];
                                if (!em.TryGetComponent(mesh.m_SubMesh, out MeshData meshData)) continue;
                                string meshName = mesh.m_SubMesh.ToString();
                                bool roadDecal = (meshData.m_DecalLayer & DecalLayers.Roads) != 0;
                                if (prefabs != null && prefabs.TryGetPrefab<RenderPrefab>(new PrefabRef(mesh.m_SubMesh), out var renderPrefab) && renderPrefab != null)
                                {
                                    meshName = renderPrefab.name;
                                    // The managed prefab retains decal receiver layers when MeshData does not.
                                    roadDecal |= renderPrefab.TryGet<DecalProperties>(out var properties) && (properties.m_LayerMask & DecalLayers.Roads) != 0;
                                }
                                if (!roadDecal) continue;
                                lane.markingGeometry.Add(new MarkingMesh { name = meshName, prefabMeshIndex = meshIndex,
                                    active = activeMeshes.Contains(meshIndex), flags = meshData.m_State.ToString(), subMeshFlags = mesh.m_Flags.ToString(),
                                    inverted = (meshData.m_State & MeshFlags.Invert) != 0, tilingCount = meshData.m_TilingCount, smoothingDistance = meshData.m_SmoothingDistance,
                                    size = new Point(MathUtils.Size(meshData.m_Bounds)), centre = new Point(MathUtils.Center(meshData.m_Bounds)) });
                            }
                            lane.marking |= lane.markingGeometry.Count > 0;
                        }
                    }
                    capture.lanes.Add(lane);
                }
            }
            string PathIdentity(Game.Pathfind.PathNode pathNode) => pathNode.GetOwnerIndex() == 0 ? "" :
                $"{pathNode.GetOwnerIndex()}:{pathNode.GetLaneIndex()}:{pathNode.IsSecondary()}";
        }
    }
}
