using System.Collections.Generic;
using Colossal.Entities;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Rendering;
using Unity.Entities;
using Unity.Mathematics;

namespace RoadGrass
{
    internal partial class RoadGrassJoinCompositionSystem
    {
        private sealed class ManualMedian
        {
            internal RoadGrassSavedManualMedian Saved;
            internal readonly List<float3> Points = new();
            internal readonly List<RoadGrassSavedManualMedianVertex> References = new();
            internal int PlanEpoch = -1;
            internal List<RoadGrassPolygonSections.Section> Sections;
            internal List<RoadGrassTransitionMesh.Half> Halves;
            internal bool Attached;
            internal bool HasOpening;
            internal string ShapeMessage = "";
        }
        private readonly Dictionary<int, ManualMedian> m_ManualMedians = new();
        private readonly Dictionary<RoadGrassMedianAnchorKey, int> m_ManualKeys = new();
        private bool m_ManualLoaded, m_ManualPreviewSet;
        private RoadGrassMedianAnchorKey m_ManualPreviewKey;
        private float3 m_ManualPreviewPoint;
        private MedianSavedSurface m_ManualPreviewSurface;
        private RoadGrassTransitionMesh.Half m_ManualPreview;
        private string m_ManualPreviewReason;
        internal int SelectedManualMedianId { get; private set; }
        internal string ManualMessage { get; private set; } = "";
        internal int SelectedManualMedianSurface => m_ManualMedians.TryGetValue(SelectedManualMedianId, out var median) ? (int)median.Saved.Surface : 0;
        internal bool CanEditSelectedManualMedian => m_GameReady && !m_SuspendedForSave && !m_WaitingForLoadSettle &&
            m_ManualMedians.ContainsKey(SelectedManualMedianId);

        private void ResetManualRuntime()
        {
            m_ManualMedians.Clear(); m_ManualKeys.Clear(); m_ManualLoaded = false;
            m_PolygonRenderKeys.Clear();
            m_PolygonPreviewPoints = null;
            m_ManualPreviewSet = false; SelectedManualMedianId = 0; ManualMessage = "";
        }
        private void HydrateManualMedians()
        {
            if (m_ManualLoaded || m_CityQuery.IsEmptyIgnoreFilter) return;
            m_ManualLoaded = true;
            Entity city = m_CityQuery.GetSingletonEntity();
            if (EntityManager.HasBuffer<RoadGrassSavedPolygonVertex>(city) && EntityManager.GetBuffer<RoadGrassSavedPolygonVertex>(city, true).Length > 0)
                ManualMessage = "Legacy free polygons have no native median profile; redraw them from a median endpoint";
            if (!EntityManager.HasBuffer<RoadGrassSavedManualMedian>(city) || !EntityManager.HasBuffer<RoadGrassSavedManualMedianVertex>(city)) return;
            foreach (var saved in EntityManager.GetBuffer<RoadGrassSavedManualMedian>(city, true))
            {
                if (saved.Id <= 0 || saved.Id >= int.MaxValue / 256 || m_ManualMedians.ContainsKey(saved.Id)) continue;
                m_ManualMedians.Add(saved.Id, new ManualMedian { Saved = saved });
            }
            foreach (var vertex in EntityManager.GetBuffer<RoadGrassSavedManualMedianVertex>(city, true))
                if (m_ManualMedians.TryGetValue(vertex.Id, out var median) && median.Points.Count < 129)
                { median.Points.Add(vertex.Offset); median.References.Add(vertex); }
            foreach (var entry in m_ManualMedians)
                if (entry.Value.Points.Count == 2) m_ManualKeys[entry.Value.Saved.Key] = entry.Key;
        }
        private void ClearManualMedians()
        {
            if (!m_CityQuery.IsEmptyIgnoreFilter)
            {
                Entity city = m_CityQuery.GetSingletonEntity();
                if (EntityManager.HasBuffer<RoadGrassSavedManualMedian>(city)) EntityManager.GetBuffer<RoadGrassSavedManualMedian>(city).Clear();
                if (EntityManager.HasBuffer<RoadGrassSavedManualMedianVertex>(city)) EntityManager.GetBuffer<RoadGrassSavedManualMedianVertex>(city).Clear();
                if (EntityManager.HasBuffer<RoadGrassSavedPolygonVertex>(city)) EntityManager.GetBuffer<RoadGrassSavedPolygonVertex>(city).Clear();
            }
            ResetManualRuntime(); m_Dirty = true;
        }
        internal void SelectManualMedian(int id)
        { SelectedManualMedianId = m_ManualMedians.ContainsKey(id) ? id : 0; ManualMessage = ""; }

        internal bool CanStartManualMedian(RoadGrassResolvedMedianAnchor anchor) => anchor != null && m_GameReady &&
            !m_SuspendedForSave && !m_WaitingForLoadSettle && !m_ActivePairs.ContainsKey(anchor.Key) &&
            !m_ManualKeys.ContainsKey(anchor.Key) && !HasExtension(anchor) && TryGetTaperStart(anchor, out _) &&
            TryPlanEndpoint(anchor, MedianSavedCap.None, out _, true);

        private bool TryManualSurface(RoadGrassResolvedMedianAnchor anchor, MedianSavedSurface surface, out RoadGrassResolvedMedianAnchor result)
        {
            result = null;
            if (!EntityManager.TryGetComponent(GetOriginalSegment(anchor.Key.Edge), out NetCompositionData original)) return false;
            var flags = RoadGrassMeshInjectSystem.RemoveStopFlags(original.m_Flags);
            if (!TryResolveStyleAnchor(anchor, WithSurface(flags, surface), out result)) return false;
            if (surface == MedianSavedSurface.None) return true;
            return TryResolveStyleAnchor(anchor, WithSurface(flags, surface == MedianSavedSurface.Grass ? MedianSavedSurface.Plain : MedianSavedSurface.Grass), out var other) &&
                !CompositionPiecesEqual(result.Pieces, other.Pieces);
        }

        internal bool TryManualPreview(RoadGrassResolvedMedianAnchor anchor, float3 destination, MedianSavedSurface surface, out EdgeGeometry geometry)
        {
            geometry = default;
            if (anchor == null) return false;
            if (!m_ManualPreviewSet || !m_ManualPreviewKey.Equals(anchor.Key) || !m_ManualPreviewPoint.Equals(destination) || m_ManualPreviewSurface != surface)
            {
                m_ManualPreviewSet = true; m_ManualPreviewKey = anchor.Key; m_ManualPreviewPoint = destination; m_ManualPreviewSurface = surface;
                TryCreateGuidedMedian(anchor, destination, surface, out m_ManualPreview, out m_ManualPreviewReason);
            }
            ManualMessage = m_ManualPreviewReason ?? "";
            if (m_ManualPreview == null) return false;
            geometry = BuildEdgeGeometry(m_ManualPreview.Geometry.m_Left.m_Left, m_ManualPreview.Geometry.m_Right.m_Right);
            return true;
        }

        private bool TryCreateGuidedMedian(RoadGrassResolvedMedianAnchor anchor, float3 destination, MedianSavedSurface surface,
            out RoadGrassTransitionMesh.Half half, out string reason)
        {
            half = null; reason = "Native median profile unavailable";
            if (anchor == null || !m_GameReady || m_SuspendedForSave || m_WaitingForLoadSettle || !math.all(math.isfinite(destination)) ||
                m_ActivePairs.ContainsKey(anchor.Key) || !RoadGrassMedianGeometry.TryGetOrientedApproach(EntityManager, anchor, out var approach) ||
                !TryManualSurface(anchor, surface, out var styled) || !TryPlanEndpoint(anchor, MedianSavedCap.None, out _, true)) return false;
            EdgeNodeGeometry pavement;
            if (anchor.Key.End && EntityManager.TryGetComponent(anchor.Key.Edge, out EndNodeGeometry end)) pavement = end.m_Geometry;
            else if (!anchor.Key.End && EntityManager.TryGetComponent(anchor.Key.Edge, out StartNodeGeometry startNode)) pavement = startNode.m_Geometry;
            else return false;
            var lanes = new List<RoadGrassTaperGeometry.Lane>();
            var guides = new List<RoadGrassTaperGeometry.Lane>();
            var seen = new HashSet<Entity>();
            if (!ReadLanes(anchor.Node, true) || !EntityManager.HasBuffer<ConnectedEdge>(anchor.Node)) return false;
            foreach (var edge in EntityManager.GetBuffer<ConnectedEdge>(anchor.Node, true))
                if (!ReadLanes(edge.m_Edge, false)) return false;
            float3 start = (approach.m_Left.d + approach.m_Right.d) * .5f;
            guides.AddRange(GetMarkingGuides(anchor.Node));
            float3 forward = math.normalizesafe((approach.m_Left.d - approach.m_Left.c + approach.m_Right.d - approach.m_Right.c) * new float3(1,0,1));
            if (!RoadGrassLaneGuideResolver.TryResolve(start, forward, anchor.Width, destination, guides, out var samples, out reason)) return false;
            if (!RoadGrassLaneGuideResolver.TryFit(samples, out var center)) { reason = "Guide needs more native sections than this contour supports"; return false; }
            var borders = RoadGrassTransitionMesh.ReadBorderWidths(EntityManager, styled);
            borders = math.min(math.max(borders, new float2(.12f)), new float2(styled.Width * .25f));
            float radius = math.cmax(borders) + .15f;
            float3 tip = center.d;
            float3 direction = math.normalizesafe((samples[samples.Count-1] - samples[samples.Count-2]) * new float3(1,0,1));
            tip -= direction * radius;
            float3 across = new float3(-direction.z,0,direction.x);
            if (math.dot(across, approach.m_Right.d-approach.m_Left.d)<0) across=-across;
            float3 offset = (approach.m_Right.d-approach.m_Left.d)*.5f;
            float nose = math.min(styled.Width*.5f, math.sqrt(2f/3f*radius*math.max(radius,math.dot(tip-center.b,direction))));
            var left = new Bezier4x3(approach.m_Left.d,center.b-offset*.66f,tip-across*nose,tip);
            var right = new Bezier4x3(approach.m_Right.d,center.b+offset*.66f,tip+across*nose,tip);
            var mid = MathUtils.Lerp(left,right,.5f);
            // Check spatial deviation, not parameter speed: rounded noses slow to zero.
            foreach (var p in samples)
            {
                float nearest = float.MaxValue;
                for (int i=0;i<=96;i++) nearest=math.min(nearest,math.distance(p.xz,MathUtils.Position(mid,i/96f).xz));
                if(nearest>math.max(.3f,radius*1.2f)) { reason="This guide cannot be represented by a native median section"; return false; }
            }
            if (!RoadGrassTaperGeometry.ValidateGuide(approach,pavement,left,right,out reason)) return false;
            var geometry = BuildEdgeGeometry(left,right);
            half = RoadGrassTransitionMesh.Create(styled,geometry,borders,float2.zero);
            var native = half.Geometry;
            mid.d -= direction*radius; mid.c = math.lerp(mid.b,mid.d,.75f);
            native.m_Middle=mid; native.m_Left.m_Right=mid; native.m_Right.m_Left=mid;
            native.m_Left.m_Length.y=native.m_Right.m_Length.x=MathUtils.Length(mid);
            native.m_SyncVertexTargetsLeft=new float4(0,0,0,borders.x/radius);
            native.m_SyncVertexTargetsRight=new float4(1-borders.y/radius,1,1,1);
            half.Geometry=native;
            foreach(var planned in m_TaperPlans)
                if(!planned.Key.Equals(anchor.Key)&&planned.Value.Half!=null&&planned.Value.Half.Source.Node==anchor.Node&&
                    RoadGrassTaperGeometry.Overlaps(native,planned.Value.Half.Geometry))
                {half=null;reason="Contour overlaps another median";return false;}
            foreach(var pair in m_ActivePairs)
            {
                if(!m_Resolved.TryGetValue(pair.Key,out var a)||a.Node!=anchor.Node||!m_Resolved.TryGetValue(pair.Value,out var b))continue;
                if(!TryBuildPairGeometry(a,b,out var ga,out _))continue;
                if(RoadGrassTaperGeometry.Overlaps(native,RoadGrassTransitionMesh.Create(a,ga,float2.zero,float2.zero).Geometry))
                {half=null;reason="Contour overlaps a median connection";return false;}
            }
            reason=""; return true;

            bool ReadLanes(Entity owner, bool isNode)
            {
                m_TaperDependencies.Add(owner);
                if (!EntityManager.HasBuffer<Game.Net.SubLane>(owner)) return false;
                foreach(var sub in EntityManager.GetBuffer<Game.Net.SubLane>(owner,true))
                {
                    Entity lane=sub.m_SubLane;
                    if(!seen.Add(lane)||EntityManager.HasComponent<Deleted>(lane)||EntityManager.HasComponent<Game.Tools.Temp>(lane))continue;
                    bool car=EntityManager.HasComponent<Game.Net.CarLane>(lane);
                    if(!car&&!EntityManager.HasComponent<Game.Net.PedestrianLane>(lane)&&!EntityManager.HasComponent<Game.Net.TrackLane>(lane))continue;
                    m_TaperDependencies.Add(lane);
                    if(!EntityManager.TryGetComponent(lane,out Game.Net.Curve curve)||!EntityManager.TryGetComponent(lane,out PrefabRef prefab)||
                       !EntityManager.TryGetComponent(prefab.m_Prefab,out NetLaneData data))return false;
                    var item=new RoadGrassTaperGeometry.Lane{Curve=curve.m_Bezier,Width=data.m_Width};
                    lanes.Add(item);if(isNode&&car)guides.Add(item);
                }
                return true;
            }
        }

        internal bool PlaceManualMedian(RoadGrassResolvedMedianAnchor anchor, float3 destination, MedianSavedSurface surface)
        {
            if (!CanStartManualMedian(anchor) || !TryManualPreview(anchor,destination,surface,out _) || m_CityQuery.IsEmptyIgnoreFilter) return false;
            Entity city=m_CityQuery.GetSingletonEntity();
            int id=1;foreach(int existing in m_ManualMedians.Keys)id=math.max(id,existing+1);
            var saved=new RoadGrassSavedManualMedian{Id=id,Node=anchor.Node,Edge=anchor.Key.Edge,Anchor=anchor.Data,Surface=surface};
            var median=new ManualMedian{Saved=saved};
            float3 origin=EntityManager.GetComponentData<Node>(anchor.Node).m_Position;
            TryGetTaperStart(anchor,out var start);
            median.Points.Add(start-origin);median.Points.Add(destination-origin);
            if(!EntityManager.HasBuffer<RoadGrassSavedManualMedian>(city))EntityManager.AddBuffer<RoadGrassSavedManualMedian>(city);
            if(!EntityManager.HasBuffer<RoadGrassSavedManualMedianVertex>(city))EntityManager.AddBuffer<RoadGrassSavedManualMedianVertex>(city);
            EntityManager.GetBuffer<RoadGrassSavedManualMedian>(city).Add(saved);
            var vertices=EntityManager.GetBuffer<RoadGrassSavedManualMedianVertex>(city);
            foreach(var p in median.Points)vertices.Add(new RoadGrassSavedManualMedianVertex{Id=id,Offset=p});
            m_ManualMedians.Add(id,median);m_ManualKeys.Add(saved.Key,id);
            SelectedManualMedianId=id;ManualMessage="";ClearTaperPlans();m_Dirty=true;return true;
        }

        internal bool SetManualSurface(int id, MedianSavedSurface surface)
        {
            if((uint)surface>2||!CanEditSelectedManualMedian||!m_ManualMedians.TryGetValue(id,out var median))return false;
            if(median.Saved.Surface==surface)return true;
            if (median.Points.Count >= 3)
            {
                if (!EnsurePolygonPlan(median, out var source) || !BindPolygonSurface(source, median.Sections, surface, out var halves, out var error)) return false;
                median.Halves = halves;
            }
            else
            {
                if (!TryResolveManual(median, out var anchor, out var destination)) return false;
                if (!TryCreateGuidedMedian(anchor,destination,surface,out _,out var reason)) { ManualMessage=reason; return false; }
            }
            var saved=median.Saved;saved.Surface=surface;median.Saved=saved;
            var records=EntityManager.GetBuffer<RoadGrassSavedManualMedian>(m_CityQuery.GetSingletonEntity());
            for(int i=0;i<records.Length;i++)if(records[i].Id==id)records[i]=saved;
            if (median.Points.Count < 3) ClearTaperPlans();
            m_Dirty=true;ManualMessage=median.ShapeMessage;return true;
        }
        internal void RemoveManualMedian(int id)
        {
            if(!m_GameReady||m_SuspendedForSave||m_WaitingForLoadSettle||!m_ManualMedians.TryGetValue(id,out var median))return;
            Entity city=m_CityQuery.GetSingletonEntity();
            var records=EntityManager.GetBuffer<RoadGrassSavedManualMedian>(city);
            for(int i=records.Length-1;i>=0;i--)if(records[i].Id==id)records.RemoveAt(i);
            var vertices=EntityManager.GetBuffer<RoadGrassSavedManualMedianVertex>(city);
            for(int i=vertices.Length-1;i>=0;i--)if(vertices[i].Id==id)vertices.RemoveAt(i);
            m_ManualMedians.Remove(id);
            if (median.Points.Count == 2) m_ManualKeys.Remove(median.Saved.Key);
            if(SelectedManualMedianId==id)SelectedManualMedianId=0;
            ClearTaperPlans();m_Dirty=true;ManualMessage="";
        }
        private bool TryResolveManual(ManualMedian median,out RoadGrassResolvedMedianAnchor anchor,out float3 destination)
        {
            anchor=null;destination=default;
            if(median.Points.Count!=2||!math.all(math.isfinite(median.Points[0]))||!math.all(math.isfinite(median.Points[1]))||
               !TryResolveSavedAnchor(median.Saved.Edge,median.Saved.Anchor,out anchor)||anchor.Node!=median.Saved.Node||!anchor.Key.Equals(median.Saved.Key)||
               !EntityManager.TryGetComponent(anchor.Node,out Node node)||!TryGetTaperStart(anchor,out var start)||
               math.distance(start,node.m_Position+median.Points[0])>.25f)return false;
            destination=node.m_Position+median.Points[1];return math.all(math.isfinite(destination));
        }
        internal IEnumerable<int> ManualMediansAtNode(Entity node)
        { foreach(var entry in m_ManualMedians)if(node == Entity.Null || entry.Value.Saved.Node==node)yield return entry.Key; }
        internal bool GetManualOutline(int id,out EdgeGeometry geometry)
        {
            geometry=default;
            if(!m_ManualMedians.TryGetValue(id,out var median)||!m_TaperPlans.TryGetValue(median.Saved.Key,out var plan)||plan.Half==null)return false;
            geometry=BuildEdgeGeometry(plan.Half.Geometry.m_Left.m_Left,plan.Half.Geometry.m_Right.m_Right);return true;
        }
        private void SynchronizeManualMedians()
        {
            m_PolygonRenderKeys.Clear();
            var deleted=new List<int>();
            foreach(var entry in m_ManualMedians)
                if(!EntityManager.Exists(entry.Value.Saved.Edge)||!EntityManager.Exists(entry.Value.Saved.Node)||
            EntityManager.HasComponent<Deleted>(entry.Value.Saved.Edge)||EntityManager.HasComponent<Deleted>(entry.Value.Saved.Node)||
            (entry.Value.Points.Count >= 3 && !TryResolveSavedAnchor(entry.Value.Saved.Edge, entry.Value.Saved.Anchor, out _)))deleted.Add(entry.Key);
            foreach(int id in deleted)RemoveManualMedian(id);
            if (deleted.Count > 0) ManualMessage = $"{deleted.Count} medians removed because their parent median was lost";
            foreach(var entry in m_ManualMedians)
            {
                var median=entry.Value;
                m_TaperDependencies.Add(median.Saved.Edge);
                m_TaperDependencies.Add(median.Saved.Node);
                if (median.Points.Count >= 3) { SynchronizePolygon(entry.Key, median); continue; }
                if(!TryResolveManual(median,out var anchor,out var destination))
                {ManualMessage="Saved contour unavailable: its source cannot be rebound safely";continue;}
                var key=anchor.Key;
                RoadGrassTransitionMesh.Half half;
                if(m_TaperPlans.TryGetValue(key,out var cached)&&cached.Manual&&cached.Destination.Equals(destination)&&cached.Half!=null)
                    half=cached.Half;
                else
                {
                    if(!TryCreateGuidedMedian(anchor,destination,median.Saved.Surface,out half,out var reason))
                    {if(SelectedManualMedianId==entry.Key)ManualMessage=reason;continue;}
                    // Traffic-light refreshes may invalidate plans without changing
                    // the visible shape/profile. Keep the existing carrier in that case.
                    if(m_Variants.TryGetValue(key,out var existing)&&existing.Transition!=null&&
                       existing.Transition.Geometry.Equals(half.Geometry)&&
                       CompositionDataEquals(existing.Transition.Composition,half.Composition)&&
                       CompositionPiecesEqual(existing.Transition.Pieces,half.Pieces))half=existing.Transition;
                    m_TaperPlans[key]=new TaperPlan{Source=half.Source,Half=half,Manual=true,Destination=destination};
                }
                m_RenderPairs[key]=key;m_Resolved[key]=half.Source;
                BatchDataHelpers.CalculateNodeParameters(half.Geometry,half.Composition,out var parameters);
                m_RenderStates[key]=new MedianRenderState(key,parameters,half.Geometry.m_Bounds);
            }
        }
    }
}
