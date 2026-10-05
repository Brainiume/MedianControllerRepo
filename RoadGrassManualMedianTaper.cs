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
        private readonly Dictionary<Entity, List<float3>> m_TaperSnapPoints = new();
        private readonly Dictionary<Entity, IEnumerator<bool>> m_TaperSnapBuilders = new();

        private void AdvanceSnapPoints(Entity node)
        {
            if (!m_TaperSnapBuilders.TryGetValue(node, out var builder)) return;
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            do
            {
                if (builder.MoveNext()) continue;
                builder.Dispose(); m_TaperSnapBuilders.Remove(node); break;
            }
            while ((System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000d / System.Diagnostics.Stopwatch.Frequency < 1);
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
                    if (!seen.Add(lane) || EntityManager.HasComponent<Deleted>(lane) || EntityManager.HasComponent<Game.Tools.Temp>(lane)) continue;
                    TrackTaperDependency(lane);
                    if (!EntityManager.TryGetComponent(lane, out Game.Net.Curve curve) ||
                        !EntityManager.TryGetComponent(lane, out PrefabRef prefab) ||
                        !prefabs.TryGetPrefab<NetLaneGeometryPrefab>(prefab, out var geometry) || geometry == null || geometry.m_Meshes == null) continue;
                    foreach (var mesh in geometry.m_Meshes)
                    {
                        if (mesh?.m_Mesh == null || mesh.m_RequireEditor ||
                            !mesh.m_Mesh.TryGet<DecalProperties>(out var decal) ||
                            (decal.m_LayerMask & Game.Rendering.DecalLayers.Roads) == 0) continue;
                        // Native marking centerlines, not texture pixels or arrow outlines.
                        guides.Add(new RoadGrassTaperGeometry.Lane { Curve = curve.m_Bezier, Width = 0 });
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
            StoreSegmentSurface(GetSegmentForEndpoint(anchor),surface==MedianSurfaceChoice.Grass?MedianSavedSurface.Grass:MedianSavedSurface.Plain);
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
            points = new List<float3>();
            m_TaperSnapPoints[node]=points;
            TrackTaperDependency(node);
            if (!EntityManager.HasBuffer<Game.Net.SubLane>(node)) return points;
            var lines = new List<Line3.Segment>();
            var guides = new List<int>();
            int guide = 0;
            foreach (var entry in EntityManager.GetBuffer<Game.Net.SubLane>(node,true))
            {
                Entity lane=entry.m_SubLane;
                TrackTaperDependency(lane);
                if (EntityManager.HasComponent<Deleted>(lane) || !EntityManager.HasComponent<Game.Net.CarLane>(lane) ||
                    !EntityManager.TryGetComponent(lane,out Game.Net.Curve curve) ||
                    !EntityManager.TryGetComponent(lane,out PrefabRef prefab) ||
                    !EntityManager.TryGetComponent(prefab.m_Prefab,out NetLaneData data)) continue;
                for (int side=-1;side<=1;side+=2)
                {
                    guide++;
                    float3 previous=default;
                    for (int i=0;i<=24;i++)
                    {
                        float t=i/24f;
                        float3 p=MathUtils.Position(curve.m_Bezier,t);
                        float2 tangent=math.normalizesafe(MathUtils.Tangent(curve.m_Bezier,t).xz);
                        p.xz+=new float2(-tangent.y,tangent.x)*(side*data.m_Width*0.5f);
                        if (i==0 || i==24) AddPoint(p);
                        if (i>0) { lines.Add(new Line3.Segment(previous,p)); guides.Add(guide); }
                        previous=p;
                    }
                }
            }
            int firstMarkingGuide = guide + 1;
            if (EntityManager.TryGetComponent(node, out Node nodeData))
            {
                float radius = 8;
                foreach (var p in points) radius = math.max(radius, math.distance(p.xz, nodeData.m_Position.xz) + 2);
                foreach (var marking in GetMarkingGuides(node))
                {
                    guide++;
                    float3 previous = marking.Curve.a;
                    for (int i = 1; i <= 64; i++)
                    {
                        float3 p = MathUtils.Position(marking.Curve, i / 64f);
                        float2 delta = p.xz - previous.xz;
                        float t = math.saturate(math.dot(nodeData.m_Position.xz - previous.xz, delta) / math.max(.0001f, math.lengthsq(delta)));
                        if (math.distance(math.lerp(previous.xz, p.xz, t), nodeData.m_Position.xz) <= radius)
                        {
                            lines.Add(new Line3.Segment(previous, p));
                            guides.Add(guide);
                        }
                        previous = p;
                    }
                }
            }
            // Spatial candidates avoid comparing distant samples; remaining work is frame-budgeted.
            m_TaperSnapBuilders[node] = FindIntersections();
            AdvanceSnapPoints(node);
            return points;
            IEnumerator<bool> FindIntersections()
            {
                int work = 0;
            foreach (var pair in RoadGrassSnapCandidates.Pairs(lines))
            {
                int i = pair.x, j = pair.y;
                if ((++work & 63) == 0) yield return true;
                if (guides[i] == guides[j]) continue;
                bool marking = guides[i] >= firstMarkingGuide || guides[j] >= firstMarkingGuide;
                var a=lines[i];var b=lines[j];
                if (math.any(math.max(a.a.xz,a.b.xz)<math.min(b.a.xz,b.b.xz)) ||
                    math.any(math.max(b.a.xz,b.b.xz)<math.min(a.a.xz,a.b.xz))) continue;
                float2 u=a.b.xz-a.a.xz,v=b.b.xz-b.a.xz,w=b.a.xz-a.a.xz;
                float determinant=u.x*v.y-u.y*v.x;
                // Parallel/overlapping guides are not lane crossings. Adjacent samples
                // on one guide must never become snap points.
                if (math.abs(determinant)<math.max(0.000001f,math.length(u)*math.length(v)*.0001f)) continue;
                float t=(w.x*v.y-w.y*v.x)/determinant, s=(w.x*u.y-w.y*u.x)/determinant;
                if(t>=0&&t<=1&&s>=0&&s<=1)
                {
                    float3 p = math.lerp(a.a,a.b,t), q = math.lerp(b.a,b.b,s);
                    // Lane envelopes use the junction's projected geometry; their heights
                    // can differ on the same road. Preserve those original snap points.
                    if (!marking) AddPoint(p);
                    else if (math.abs(p.y-q.y) <= .5f) AddPoint((p+q)*.5f);
                }
            }
            }
            void AddPoint(float3 p)
            {
                if(!math.all(math.isfinite(p)))return;
                foreach(var old in points)if(math.distancesq(old,p)<.0001f)return;
                points.Add(p);
            }
        }
    }
}
