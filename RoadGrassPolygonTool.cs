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
        private int PickManualMedian(RoadGrassJoinCompositionSystem manager, Line3.Segment ray, float fovTan)
        {
            int hit = 0;
            float nearest = float.MaxValue;
            foreach (int id in manager.ManualMediansAtNode(Entity.Null))
            foreach (var outline in manager.GetManualOutlines(id))
            {
                bool a = RoadGrassPolygonSections.RayHit(outline.m_Start, ray, out float sa);
                bool b = RoadGrassPolygonSections.RayHit(outline.m_End, ray, out float sb);
                float score = math.min(a ? sa : float.MaxValue, b ? sb : float.MaxValue);
                if (score >= nearest) continue;
                nearest = score;
                hit = id;
            }
            return hit;
        }

        private JobHandle DrawManualMedianSelection(JobHandle inputDeps, RoadGrassJoinCompositionSystem manager, int hovered)
        {
            var commands = new NativeList<OverlayCommand>(Allocator.TempJob);
            foreach (var outline in manager.GetManualOutlines(manager.SelectedManualMedianId))
                AddPreviewHalf(ref commands, outline, SelectedColor);
            foreach (var outline in manager.GetManualOutlines(hovered))
                AddPreviewHalf(ref commands, outline, HoverColor);
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
            int hit = source == null && hasRay ? PickManualMedian(manager, ray, fovTan) : 0;
            RoadGrassResolvedMedianAnchor extension = null;
            float nearest = float.MaxValue;
            foreach (var selected in manager.GetManualOutlines(manager.SelectedManualMedianId)) AddPreviewHalf(ref commands, selected, SelectedColor);
            foreach (var highlight in manager.GetManualOutlines(hit)) AddPreviewHalf(ref commands, highlight, HoverColor);
            if(source==null&&hasRay&&hit==0)
                foreach(var candidate in manager.GetNodeAnchors(node))
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
                TooltipState = hit != 0 ? RoadGrassJoinTooltipState.SelectManualMedian : available ?
                    RoadGrassJoinTooltipState.PickTaperStart : RoadGrassJoinTooltipState.SelectTaperStart;
                if (hovered != null && hit == 0) AddFootprint(ref commands, hovered, available ? HoverColor : InvalidColor);
                if (applyAction.WasReleasedThisFrame())
                {
                    if (hit != 0) manager.SelectManualMedian(hit);
                    else if (available) { manager.SelectManualMedian(0); m_FirstAnchor = hovered.Key; m_PolygonPoints.Clear(); }
                }
            }
            else
            {
                AddFootprint(ref commands, source, SelectedColor);
                float3 destination = default;
                bool snapped = false;
                var points = Options.PolygonSnapping ? new List<float3>(manager.GetTaperSnapPoints(node)) : new List<float3>();
                if (RoadGrassMedianGeometry.TryGetOrientedApproach(EntityManager, source, out var opening))
                { if (Options.PolygonSnapping) { points.Insert(0, opening.m_Right.d); points.Insert(0, opening.m_Left.d); } }
                float3 delta = ray.b - ray.a;
                snapped = PickPolygonSnap(points, ray, hasRay, fovTan, ref commands, out destination);
                bool closing = false;
                if (hasRay && m_PolygonCornerStarts.Count >= 3)
                {
                    float3 first = m_PolygonPoints[0];
                    float t = math.dot(first - ray.a, delta) / math.max(.0001f, math.lengthsq(delta));
                    float tolerance = math.clamp(math.distance(ray.a, first) * fovTan * .003f, .2f, .45f);
                    closing = t >= 0 && t <= 1 && math.distance(first, ray.a + delta * t) <= tolerance;
                    if (closing) { destination = first; snapped = true; }
                }
                bool hasPosition = hasRay && snapped;
                if (hasRay && !snapped)
                {
                    if (GetRaycastResult(out Entity _, out Game.Common.RaycastHit roadHit))
                    { destination = roadHit.m_Position; hasPosition = true; }
                    else if (math.abs(delta.y) > .0001f && manager.TryGetTaperStart(source, out var start))
                    {
                        float height = m_PolygonPoints.Count == 0 ? start.y : m_PolygonPoints[0].y;
                        float t = (height - ray.a.y) / delta.y;
                        if (t >= 0 && t <= 1) { destination = ray.a + delta * t; hasPosition = true; }
                    }
                }
                var surface = MedianSavedSurface.None;
                var candidate = new List<float3>(m_PolygonPoints);
                bool distinct = hasPosition && (candidate.Count == 0 || math.distance(candidate[candidate.Count - 1].xz, destination.xz) >= .1f);
                if (distinct)
                {
                    bool followsGuide = Options.PolygonSnapping && snapped && m_PolygonCornerSnaps.Count > 0 && m_PolygonCornerSnaps[m_PolygonCornerSnaps.Count - 1];
                    if (closing) followsGuide &= m_PolygonCornerSnaps[0];
                    bool openingEdge = candidate.Count > 0 &&
                        ((candidate[candidate.Count - 1].Equals(opening.m_Left.d) && destination.Equals(opening.m_Right.d)) ||
                         (candidate[candidate.Count - 1].Equals(opening.m_Right.d) && destination.Equals(opening.m_Left.d)));
                    if (followsGuide && !openingEdge)
                    {
                        var path = manager.PolygonGuideEdge(node, candidate[candidate.Count - 1], destination);
                        for (int i = 1; i < path.Count - (closing ? 1 : 0); i++) candidate.Add(path[i]);
                    }
                    else if (!closing) candidate.Add(destination);
                }
                bool valid = candidate.Count >= 3 && manager.TryPolygonPreview(source, candidate, surface, out _);
                TooltipState = closing && valid ? RoadGrassJoinTooltipState.ClosePolygon :
                    candidate.Count >= 3 && !valid ? RoadGrassJoinTooltipState.ManualGuideRejected : RoadGrassJoinTooltipState.AddPolygonPoint;
                for (int i = 0; i < candidate.Count; i++)
                {
                    var p = candidate[i];
                    Color color = closing && valid ? ValidColor : HoverColor;
                    AddLine(ref commands, color, new Line3.Segment(p - new float3(.2f,0,0), p + new float3(.2f,0,0)), .4f);
                    if (i > 0) AddLine(ref commands, color, new Line3.Segment(candidate[i - 1], p), OutlineWidth);
                }
                if (candidate.Count >= 3) AddLine(ref commands, closing && valid ? ValidColor : HoverColor,
                    new Line3.Segment(candidate[candidate.Count - 1], candidate[0]), OutlineWidth);
                if (closing && valid && manager.TryPolygonPreview(source, candidate, surface, out var preview))
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
                    if (closing && valid && manager.PlacePolygon(source, candidate, surface, references))
                    {
                        int selected = manager.SelectedManualMedianId;
                        Options.SetActiveMode((int)MedianControllerMode.Edit);
                        manager.SelectManualMedian(selected);
                    }
                    else if (!closing && distinct && m_PolygonCornerStarts.Count < 32 && candidate.Count <= 128)
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
