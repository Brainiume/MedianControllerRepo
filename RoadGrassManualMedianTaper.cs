using System.Collections.Generic;
using Colossal.Entities;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Unity.Entities;
using Unity.Mathematics;

namespace RoadGrass
{
    internal partial class RoadGrassJoinCompositionSystem
    {
        private readonly Dictionary<Entity, RoadGrassContourGraph> m_ContourGraphs = new();
        private readonly Dictionary<Entity, List<float3>> m_TaperSnapPoints = new();
        private readonly Dictionary<Entity, IEnumerator<bool>> m_TaperSnapBuilders = new();

        private void AdvanceSnapPoints(Entity node)
        {
            if (!m_TaperSnapBuilders.TryGetValue(node, out var builder)) return;
            int previousCount = m_TaperSnapPoints[node].Count;
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            do
            {
                if (builder.MoveNext()) continue;
                builder.Dispose(); m_TaperSnapBuilders.Remove(node); break;
            }
            while ((System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000d / System.Diagnostics.Stopwatch.Frequency < 1);
            if (m_TaperSnapPoints[node].Count != previousCount) m_PolygonGuideEdges.Clear();
        }
        private readonly Dictionary<Entity, List<RoadGrassTaperGeometry.Lane>> m_MarkingGuides = new();

        private List<RoadGrassTaperGeometry.Lane> GetMarkingGuides(Entity node)
        {
            if (m_MarkingGuides.TryGetValue(node, out var guides)) return guides;
            guides = new List<RoadGrassTaperGeometry.Lane>();
            m_MarkingGuides[node] = guides;
            var prefabs = World.GetExistingSystemManaged<PrefabSystem>();
            if (prefabs == null) return guides;
            var seen = new HashSet<Entity>();
            Read(node);
            if (EntityManager.HasBuffer<ConnectedEdge>(node))
                foreach (var edge in EntityManager.GetBuffer<ConnectedEdge>(node, true)) Read(edge.m_Edge);
            return guides;

            void Read(Entity owner)
            {
                TrackTaperDependency(owner);
                if (!EntityManager.HasBuffer<Game.Net.SubLane>(owner)) return;
                foreach (var sub in EntityManager.GetBuffer<Game.Net.SubLane>(owner, true))
                {
                    Entity lane = sub.m_SubLane;
                    if (!seen.Add(lane) || !EntityManager.HasComponent<Game.Net.SecondaryLane>(lane) || EntityManager.HasComponent<Game.Tools.Hidden>(lane) || EntityManager.HasComponent<Deleted>(lane) || EntityManager.HasComponent<Game.Tools.Temp>(lane)) continue;
                    TrackTaperDependency(lane);
                    if (!EntityManager.TryGetComponent(lane, out Game.Net.Curve curve) ||
                        !EntityManager.TryGetComponent(lane, out PrefabRef prefab) ||
                        !prefabs.TryGetPrefab<NetLaneGeometryPrefab>(prefab, out var geometry) || geometry == null || geometry.m_Meshes == null || !System.Text.RegularExpressions.Regex.IsMatch(geometry.name, @"\b(edge|lane|merge|center) line\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) continue;
                    foreach (var mesh in geometry.m_Meshes)
                    {
                        if (mesh?.m_Mesh == null || mesh.m_RequireEditor ||
                            !mesh.m_Mesh.TryGet<DecalProperties>(out var decal) ||
                            (decal.m_LayerMask & Game.Rendering.DecalLayers.Roads) == 0) continue;
                        // Native marking centerlines, not texture pixels or arrow outlines.
                        float from = 0;
                        if (EntityManager.HasBuffer<Game.Net.CutRange>(lane))
                            foreach (var cut in EntityManager.GetBuffer<Game.Net.CutRange>(lane, true))
                            {
                                if (cut.m_CurveDelta.min > from) guides.Add(new RoadGrassTaperGeometry.Lane { Marking = true, Curve = MathUtils.Cut(curve.m_Bezier, new Bounds1(from, cut.m_CurveDelta.min)) });
                                from = math.max(from, cut.m_CurveDelta.max);
                            }
                        if (from < 1) guides.Add(new RoadGrassTaperGeometry.Lane { Curve = MathUtils.Cut(curve.m_Bezier, new Bounds1(from, 1)) });
                        break;
                    }
                }
            }
        }

        internal bool TryGetTaperStart(RoadGrassResolvedMedianAnchor anchor, out float3 position)
        {
            position = default;
            if (!RoadGrassMedianGeometry.TryGetOrientedApproach(EntityManager, anchor, out var approach)) return false;
            position = (approach.m_Left.d + approach.m_Right.d) * 0.5f;
            return true;
        }

        private bool TryCreateManualTaper(RoadGrassResolvedMedianAnchor anchor, float3 destination,
            out RoadGrassTransitionMesh.Half half, out EdgeGeometry geometry)
        {
            half = null; geometry = default;
            if (anchor == null || !m_GameReady || m_SuspendedForSave || m_WaitingForLoadSettle ||
                !EntityManager.Exists(anchor.Key.Edge) || EntityManager.HasComponent<Deleted>(anchor.Key.Edge) ||
                m_ActivePairs.ContainsKey(anchor.Key) ||
                !RoadGrassMedianGeometry.TryGetOrientedApproach(EntityManager, anchor, out var approach)) return false;
            anchor = GetStyledContinuation(anchor);
            var borders = RoadGrassTransitionMesh.ReadBorderWidths(EntityManager, anchor);
            // Some Road Builder profiles omit vertex-match borders. Keep a small
            // curb band in those profiles instead of collapsing every vertex to a point.
            float fallback = math.clamp(anchor.Width * 0.04f, 0.12f, 0.25f);
            borders = math.max(borders, new float2(fallback));
            borders = math.min(borders, new float2(anchor.Width * 0.25f));
            float radius = math.cmax(borders) + 0.15f;
            float3 start = (approach.m_Left.d + approach.m_Right.d) * 0.5f;
            float3 direction = math.normalizesafe(new float3(destination.x-start.x,0,destination.z-start.z));
            destination -= direction * RoadGrassTaperGeometry.Inset;
            if (!RoadGrassTaperGeometry.TryBuildManual(approach,destination,radius,out geometry,out var middle)) return false;
            half = RoadGrassTransitionMesh.Create(anchor,geometry,borders,float2.zero);
            // The inside of the nose ends behind its outside. This leaves a real
            // curb band around the rounded end instead of a zero-area terminal seam.
            var node = half.Geometry;
            node.m_Left.m_Right = middle;
            node.m_Right.m_Left = middle;
            node.m_Left.m_Length.y = node.m_Right.m_Length.x = MathUtils.Length(middle);
            node.m_Middle = middle;
            node.m_SyncVertexTargetsLeft = new float4(0,0,0,borders.x/radius);
            node.m_SyncVertexTargetsRight = new float4(1-borders.y/radius,1,1,1);
            half.Geometry = node;
            return true;
        }

        internal bool TryBuildManualTaperPreview(RoadGrassResolvedMedianAnchor anchor, float3 destination, out EdgeGeometry geometry) =>
            TryCreateManualTaper(anchor,destination,out _,out geometry);

        internal bool TryPlaceManualTaper(RoadGrassResolvedMedianAnchor anchor, float3 destination,
            MedianCapChoice cap, MedianSurfaceChoice surface)
        {
            if (!CanEditEndpoint(anchor,cap,surface,true) ||
                !EntityManager.TryGetComponent(anchor.Node,out Node node) ||
                !TryCreateManualTaper(anchor,destination,out _,out _)) return false;
            StoreEndpointStyle(new RoadGrassSavedEndpointStyle {
                Edge=anchor.Key.Edge,Node=anchor.Node,Anchor=anchor.Data,
                Cap=cap==MedianCapChoice.Rounded?MedianSavedCap.Rounded:MedianSavedCap.Straight,
                Surface=MedianSavedSurface.None,ExtendIntoIntersection=true,HasTaperEnd=true,
                TaperEndOffset=destination-node.m_Position
            });
            TryEditSegmentSurface(GetSegmentForEndpoint(anchor),surface);
            return true;
        }

        private bool TryPlanManualTaper(RoadGrassResolvedMedianAnchor anchor, RoadGrassSavedEndpointStyle style,
            out RoadGrassTransitionMesh.Half half)
        {
            half = null;
            if (!EntityManager.TryGetComponent(anchor.Node,out Node node) ||
                !EntityManager.TryGetComponent(anchor.Key.Edge,out EdgeGeometry edge)) return false;
            float3 destination = node.m_Position + style.TaperEndOffset;
            anchor = GetStyledContinuation(anchor);
            Entity source = GetOriginalEndpoint(anchor.Key);
            if (m_TaperPlans.TryGetValue(anchor.Key,out var cached) && cached.Manual &&
                cached.NodeComposition==source && cached.Edge.Equals(edge) && cached.Destination.Equals(destination) &&
                cached.Source.SourceComposition==anchor.SourceComposition &&
                CompositionDataEquals(cached.Source.StandaloneComposition,anchor.StandaloneComposition) &&
                CompositionPiecesEqual(cached.Source.Pieces,anchor.Pieces))
            { half=cached.Half; return half!=null; }
            TryCreateManualTaper(anchor,destination,out half,out _);
            m_TaperPlans[anchor.Key]=new TaperPlan { Source=anchor,NodeComposition=source,Edge=edge,
                Manual=true,Destination=destination,Half=half };
            TrackTaperDependency(anchor.Key.Edge);
            TrackTaperDependency(anchor.Node);
            return half!=null;
        }

        internal List<float3> GetTaperSnapPoints(RoadGrassResolvedMedianAnchor anchor)
            => GetTaperSnapPoints(anchor.Node);

        internal List<float3> GetTaperSnapPoints(Entity node)
        {
            if (m_TaperSnapPoints.TryGetValue(node,out var points)) { AdvanceSnapPoints(node); return points; }
            var graph = new RoadGrassContourGraph(PolygonContourGuides(node));
            foreach (var anchor in GetNodeAnchors(node))
                if (RoadGrassMedianGeometry.TryGetOrientedApproach(EntityManager, anchor, out var approach))
                    graph.ExtraPoints.Add((approach.m_Left.d + approach.m_Right.d) * .5f);
            m_ContourGraphs[node] = graph; points = graph.Points; m_TaperSnapPoints[node] = points;
            TrackTaperDependency(node);
            if (!EntityManager.TryGetComponent(node, out Node data)) return points;
            m_TaperSnapBuilders[node] = graph.Build(data.m_Position);
            AdvanceSnapPoints(node);
            return points;
        }
    }
}
