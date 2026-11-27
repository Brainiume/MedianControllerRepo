using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Net;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace RoadGrass
{
    internal partial class RoadGrassJoinToolSystem
    {
        private readonly List<float3> m_PolygonPoints = new();
        private readonly List<int> m_PolygonCornerStarts = new();
        private readonly List<bool> m_PolygonCornerSnaps = new();
        internal bool HasPolygonCorners => Options.ActiveMode == MedianControllerMode.Polygon && m_PolygonPoints.Count > 0;
        private int m_EditingPolygonId, m_DragPolygonPoint = -1;
        private List<float3> m_PolygonEditPoints;
        internal bool PolygonPointEditing => m_EditingPolygonId != 0 && Options.ActiveMode == MedianControllerMode.Edit &&
            RoadGrassJoinCompositionSystem.Instance?.SelectedManualMedianId == m_EditingPolygonId;

        internal void SetPolygonPointEditing(bool enabled)
        {
            m_EditingPolygonId = 0; m_DragPolygonPoint = -1; m_PolygonEditPoints = null;
            var manager = RoadGrassJoinCompositionSystem.Instance;
            if (!enabled || manager?.SelectedIsPolygon != true || !manager.CanEditSelectedManualMedian) return;
            int selected = manager.SelectedManualMedianId;
            Options.SetActiveMode((int)MedianControllerMode.Edit);
            manager.SelectManualMedian(selected);
            m_EditingPolygonId = selected;
        }

        private JobHandle UpdatePolygonPoints(JobHandle inputDeps, RoadGrassJoinCompositionSystem manager,
            bool hasRay, Line3.Segment ray, float fovTan)
        {
            if (!manager.TryGetSelectedPolygonPoints(out var source, out var saved))
            { SetPolygonPointEditing(false); return inputDeps; }
            var points = m_PolygonEditPoints ?? saved;
            var commands = new NativeList<OverlayCommand>(Allocator.TempJob);
            AddManualMedianOutline(ref commands, manager, m_EditingPolygonId, SelectedColor);
            float3 insertion = default, delta = ray.b - ray.a;
            int2 handles = hasRay ? RoadGrassPolygonSections.PickEditHandles(points, ray, fovTan, out insertion) : new int2(-1);
            int vertex = handles.x, edge = handles.y;
            if (m_DragPolygonPoint >= 0)
            {
                if (hasRay && math.abs(delta.y) > .0001f)
                {
                    float t = (points[m_DragPolygonPoint].y + .25f - ray.a.y) / delta.y;
                    if (t >= 0 && t <= 1)
                        points[m_DragPolygonPoint] = manager.ProjectPolygonEditPoint(source, ray.a + delta * t - new float3(0, .25f, 0));
                }
                if (secondaryApplyAction.WasReleasedThisFrame())
                { m_DragPolygonPoint = -1; m_PolygonEditPoints = null; points = saved; }
                else if (applyAction.WasReleasedThisFrame())
                {
                    if (!manager.SetSelectedPolygonPoints(points)) points = saved;
                    m_DragPolygonPoint = -1; m_PolygonEditPoints = null;
                }
            }
            else if (hasRay && vertex >= 0 && secondaryApplyAction.WasReleasedThisFrame())
            {
                if (points.Count > 3)
                {
                    var changed = new List<float3>(points); changed.RemoveAt(vertex);
                    if (manager.SetSelectedPolygonPoints(changed)) points = changed;
                }
            }
            else if (hasRay && applyAction.WasPressedThisFrame())
            {
                if (vertex >= 0)
                { m_PolygonEditPoints = new List<float3>(points); m_DragPolygonPoint = vertex; }
                else if (edge >= 0)
                {
                    var changed = new List<float3>(points); changed.Insert(edge + 1, insertion);
                    if (manager.SetSelectedPolygonPoints(changed)) points = changed;
                }
            }
            TooltipState = vertex >= 0 || m_DragPolygonPoint >= 0 ? RoadGrassJoinTooltipState.DragPolygonPoint :
                edge >= 0 ? RoadGrassJoinTooltipState.InsertPolygonPoint : RoadGrassJoinTooltipState.EditPolygonPoints;
            for (int i = 0; i < points.Count; i++)
            {
                float3 p = points[i] + new float3(0, .25f, 0), end = points[(i + 1) % points.Count] + new float3(0, .25f, 0);
                AddLine(ref commands, SelectedColor, new Line3.Segment(p, end), OutlineWidth);
                AddLine(ref commands, i == vertex || i == m_DragPolygonPoint ? HoverColor : SelectedColor,
                    new Line3.Segment(p - new float3(.15f, 0, 0), p + new float3(.15f, 0, 0)), .3f);
            }
            var buffer = m_OverlayRenderSystem.GetBuffer(out JobHandle dependencies);
            var result = new DrawOverlayJob { Commands = commands.AsArray(), Buffer = buffer }
                .Schedule(JobHandle.CombineDependencies(inputDeps, dependencies));
            m_OverlayRenderSystem.AddBufferWriter(result); commands.Dispose(result);
            return result;
        }

        private int PickManualMedian(RoadGrassJoinCompositionSystem manager, Line3.Segment ray, float fovTan)
        {
            var renderer = RoadGrassPolygonPrototypeBatchSystem.Instance;
            float nearest = float.MaxValue;
            int hit = renderer != null ? renderer.PickClosest(ray, out nearest) : 0;
            foreach (int id in manager.ManualMedianOutlineIds)
            {
                if (renderer?.HasPickGeometry(id) == true)
                {
                    continue;
                }
            foreach (var outline in manager.GetManualOutlines(id))
            {
                bool a = RoadGrassPolygonSections.RayHit(outline.m_Start, ray, out float sa);
                bool b = RoadGrassPolygonSections.RayHit(outline.m_End, ray, out float sb);
                float score = math.min(a ? sa : float.MaxValue, b ? sb : float.MaxValue);
                if (score >= nearest) continue;
                nearest = score;
                hit = id;
            }
            }
            return hit;
        }

        private void AddManualMedianOutline(ref NativeList<OverlayCommand> commands, RoadGrassJoinCompositionSystem manager, int id, Color color)
        {
            var renderer = RoadGrassPolygonPrototypeBatchSystem.Instance;
            if (renderer?.HasPickGeometry(id) == true)
            {
                foreach (var line in renderer.GetPickOutline(id)) AddLine(ref commands, color, line, OutlineWidth);
            }
            else
                foreach (var outline in manager.GetManualOutlines(id)) AddPreviewHalf(ref commands, outline, color);
        }

        private JobHandle DrawManualMedianSelection(JobHandle inputDeps, RoadGrassJoinCompositionSystem manager, int hovered)
        {
            var commands = new NativeList<OverlayCommand>(Allocator.TempJob);
            AddManualMedianOutline(ref commands, manager, manager.SelectedManualMedianId, SelectedColor);
            AddManualMedianOutline(ref commands, manager, hovered, HoverColor);
            var buffer = m_OverlayRenderSystem.GetBuffer(out JobHandle dependencies);
            var result = new DrawOverlayJob { Commands = commands.AsArray(), Buffer = buffer }
                .Schedule(JobHandle.CombineDependencies(inputDeps, dependencies));
            m_OverlayRenderSystem.AddBufferWriter(result);
            commands.Dispose(result);
            return result;
        }

        private JobHandle UpdatePolygon(JobHandle inputDeps, RoadGrassJoinCompositionSystem manager,
            RoadGrassResolvedMedianAnchor hovered, Entity node, bool hasRay, Line3.Segment ray, float fovTan)
        {
            RoadGrassResolvedMedianAnchor source = null;
            if (m_FirstAnchor.HasValue && !manager.TryResolveAnchor(m_FirstAnchor.Value, out source)) m_FirstAnchor = null;
            if (source == null) { m_PolygonPoints.Clear(); m_PolygonCornerStarts.Clear(); m_PolygonCornerSnaps.Clear(); }
            if (source != null) node = source.Node;
            var commands = new NativeList<OverlayCommand>(Allocator.TempJob);
            RoadGrassResolvedMedianAnchor extension = null;
            float nearest = float.MaxValue;
            AddManualMedianOutline(ref commands, manager, manager.SelectedManualMedianId, SelectedColor);
            if(source==null&&hasRay)
                foreach(var candidate in manager.GetNodeAnchors(node, false, m_AnchorCandidates))
                {
                    if(!manager.HasExtension(candidate)||!manager.TryBuildTaperPreview(candidate,out var outline))continue;
                    bool a=TryScoreFootprint(outline.m_Start.m_Left,outline.m_Start.m_Right,ray,fovTan,out float sa);
                    bool b=TryScoreFootprint(outline.m_End.m_Left,outline.m_End.m_Right,ray,fovTan,out float sb);
                    float score=math.min(a?sa:float.MaxValue,b?sb:float.MaxValue);
                    if(score>=nearest)continue;nearest=score;extension=candidate;
                }
            if(extension!=null&&manager.TryBuildTaperPreview(extension,out var legacy))AddPreviewHalf(ref commands,legacy,HoverColor);
            if (secondaryApplyAction.WasReleasedThisFrame())
            {
                if (source != null)
                {
                    if (m_PolygonCornerStarts.Count > 0)
                    {
                        int last = m_PolygonCornerStarts.Count - 1, first = m_PolygonCornerStarts[last];
                        m_PolygonPoints.RemoveRange(first, m_PolygonPoints.Count - first);
                        m_PolygonCornerStarts.RemoveAt(last); m_PolygonCornerSnaps.RemoveAt(last);
                    }
                else Options.SetActiveMode((int)MedianControllerMode.Edit);
                }
                else Options.SetActiveMode((int)MedianControllerMode.Edit);
                TooltipState = RoadGrassJoinTooltipState.SelectTaperStart;
            }
            else if (source == null)
            {
                bool available = manager.CanDrawPolygon(hovered);
                TooltipState = available ?
                    RoadGrassJoinTooltipState.PickTaperStart : RoadGrassJoinTooltipState.SelectTaperStart;
                if (hovered != null) AddFootprint(ref commands, hovered, available ? HoverColor : InvalidColor);
                if (applyAction.WasReleasedThisFrame())
                {
                    if (available) { manager.SelectManualMedian(0); m_FirstAnchor = hovered.Key; m_PolygonPoints.Clear(); }
                }
            }
            else
            {
                AddFootprint(ref commands, source, SelectedColor);
                float3 destination = default;
                bool snapped = false;
                var points = Options.PolygonSnapping ? new List<float3>(manager.GetTaperSnapPoints(node)) : new List<float3>();
                bool hasOpening = RoadGrassMedianGeometry.TryGetOrientedApproach(EntityManager, source, out var opening);
                if (hasOpening)
                { if (Options.PolygonSnapping) { points.Insert(0, opening.m_Right.d); points.Insert(0, opening.m_Left.d); } }
                if (Options.PolygonSnapping)
                    foreach (var guide in manager.PolygonContourGuides(node))
                        for (int i = 0; i < 32; i++)
                        {
                            float3 a = MathUtils.Position(guide.Curve, i / 32f), b = MathUtils.Position(guide.Curve, (i + 1) / 32f);
                            if (math.distance(a.xz, opening.m_Left.d.xz) < 65)
                                AddLine(ref commands, guide.Median ? new Color(.65f,.86f,.53f,.8f) : guide.Marking ? new Color(1,1,1,.4f) : new Color(.8f,.75f,.5f,.3f), new Line3.Segment(a,b), .035f);
                        }
                float3 delta = ray.b - ray.a;
                if (Options.PolygonSnapping)
                    snapped = PickPolygonSnap(points, ray, hasRay, fovTan, ref commands, out destination, p => m_PolygonPoints.Count == 0 || manager.PolygonContourEdge(node, m_PolygonPoints[m_PolygonPoints.Count - 1], p) != null);
                bool closing = false;
                if (hasRay && m_PolygonCornerStarts.Count >= 3)
                {
                    float3 first = m_PolygonPoints[0];
                    float t = math.dot(first - ray.a, delta) / math.max(.0001f, math.lengthsq(delta));
                    float tolerance = math.clamp(math.distance(ray.a, first) * fovTan * .003f, .2f, .45f);
                    closing = (Options.PolygonSnapping || applyAction.WasReleasedThisFrame()) &&
                        t >= 0 && t <= 1 && math.distance(first, ray.a + delta * t) <= tolerance;
                    if (closing && Options.PolygonSnapping) { destination = first; snapped = true; }
                }
                bool hasPosition = hasRay && snapped;
                if (hasRay && !snapped)
                {
                    if (GetRaycastResult(out Entity _, out Game.Common.RaycastHit roadHit))
                    {
                        // m_Position is the road centreline/node, not the cursor's surface hit.
                        // Intersect the current cursor ray at the hit height so delayed raycast results cannot shift the point sideways.
                        float t = math.abs(delta.y) > .0001f ? (roadHit.m_HitPosition.y - ray.a.y) / delta.y : -1;
                        if (t >= 0 && t <= 1) { destination = ray.a + delta * t; hasPosition = true; }
                    }
                    else if (math.abs(delta.y) > .0001f && manager.TryGetTaperStart(source, out var start))
                    {
                        float height = m_PolygonPoints.Count == 0 ? start.y : m_PolygonPoints[0].y;
                        float t = (height - ray.a.y) / delta.y;
                        if (t >= 0 && t <= 1) { destination = ray.a + delta * t; hasPosition = true; }
                    }
                }
                if (Options.PolygonSnapping && !snapped) hasPosition = false;
                var surface = MedianSavedSurface.Grass;
                var candidate = new List<float3>(m_PolygonPoints);
                bool distinct = hasPosition && (candidate.Count == 0 || math.distance(candidate[candidate.Count - 1].xz, destination.xz) >= .1f);
                if (distinct)
                {
                    bool followsGuide = Options.PolygonSnapping && snapped && m_PolygonCornerSnaps.Count > 0 && m_PolygonCornerSnaps[m_PolygonCornerSnaps.Count - 1];
                    if (closing) followsGuide &= m_PolygonCornerSnaps[0];
                    bool openingEdge = candidate.Count > 0 &&
                        ((candidate[candidate.Count - 1].Equals(opening.m_Left.d) && destination.Equals(opening.m_Right.d)) ||
                         (candidate[candidate.Count - 1].Equals(opening.m_Right.d) && destination.Equals(opening.m_Left.d)));
                    // Snapping an attachment corner must not drag the extension around
                    // the original rounded cap. Let the user's next corner set its direction.
                    bool attachmentEdge = Options.PolygonExtendFromMedian && hasOpening && candidate.Count > 0 &&
                        RoadGrassLaneGuideResolver.TouchesOpening(candidate[candidate.Count - 1], destination, opening);
                    if (followsGuide && !openingEdge)
                    {
                        var path = manager.PolygonGuideEdge(node, candidate[candidate.Count - 1], destination);
                        for (int i = 1; i < path.Count - (closing ? 1 : 0); i++) candidate.Add(path[i]);
                    }
                    else if (!closing) candidate.Add(destination);
                }
                bool valid = candidate.Count >= 3 && manager.TryPolygonPreview(source, candidate, surface, out _, Options.PolygonExtendFromMedian);
                TooltipState = closing && valid ? RoadGrassJoinTooltipState.ClosePolygon :
                    candidate.Count >= 3 && !valid ? RoadGrassJoinTooltipState.ManualGuideRejected : RoadGrassJoinTooltipState.AddPolygonPoint;
                for (int i = 0; i < candidate.Count; i++)
                {
                    var p = candidate[i];
                    bool straight = snapped && m_PolygonPoints.Count > 0 && manager.PolygonContourEdge(node, m_PolygonPoints[m_PolygonPoints.Count - 1], destination) == null;
                    Color color = closing && valid ? ValidColor : straight ? new Color(.65f,.7f,.72f,1) : HoverColor;
                    if (m_PolygonCornerStarts.Contains(i) || i == candidate.Count - 1)
                        for (int k = 0; k < 12; k++)
                        {
                            float a = k * math.PI * 2 / 12, b = (k + 1) * math.PI * 2 / 12;
                            AddLine(ref commands, i < m_PolygonPoints.Count ? SelectedColor : color,
                                new Line3.Segment(p + new float3(math.cos(a),0,math.sin(a))*.2f,
                                    p + new float3(math.cos(b),0,math.sin(b))*.2f), .07f);
                        }
                    if (i > 0) AddLine(ref commands, color, new Line3.Segment(candidate[i - 1], p), OutlineWidth);
                }
                if (candidate.Count >= 3) AddLine(ref commands, closing && valid ? ValidColor : HoverColor,
                    new Line3.Segment(candidate[candidate.Count - 1], candidate[0]), OutlineWidth);
                if (closing && valid && manager.TryPolygonPreview(source, candidate, surface, out var preview, Options.PolygonExtendFromMedian))
                    foreach (var half in preview)
                        AddPreviewHalf(ref commands, RoadGrassJoinCompositionSystem.BuildEdgeGeometry(half.Geometry.m_Left.m_Left, half.Geometry.m_Right.m_Right), ValidColor);
                if (applyAction.WasReleasedThisFrame() && hasPosition)
                {
                    var references = new List<bool>();
                    for (int i = 0; i < candidate.Count; i++)
                    {
                        int corner = m_PolygonCornerStarts.IndexOf(i);
                        references.Add(corner >= 0 ? m_PolygonCornerSnaps[corner] : Options.PolygonSnapping);
                    }
                    if (closing && valid && manager.PlacePolygon(source, candidate, surface, references, Options.PolygonExtendFromMedian, Options.CrosswalkGaps))
                    {
                        int selected = manager.SelectedManualMedianId;
                        Options.SetActiveMode((int)MedianControllerMode.Edit);
                        manager.SelectManualMedian(selected);
                    }
                    else if (!closing && distinct)
                    {
                        m_PolygonCornerStarts.Add(m_PolygonPoints.Count); m_PolygonCornerSnaps.Add(snapped);
                        m_PolygonPoints.Clear(); m_PolygonPoints.AddRange(candidate);
                    }
                }
            }
            var buffer=m_OverlayRenderSystem.GetBuffer(out JobHandle dependencies);
            var result=new DrawOverlayJob{Commands=commands.AsArray(),Buffer=buffer}.Schedule(JobHandle.CombineDependencies(inputDeps,dependencies));
            m_OverlayRenderSystem.AddBufferWriter(result);commands.Dispose(result);return result;
        }
    }
}
