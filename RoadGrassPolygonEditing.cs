using System.Collections.Generic;
using Colossal.Entities;
using Game.Net;
using Unity.Entities;
using Unity.Mathematics;

namespace RoadGrass
{
    internal partial class RoadGrassJoinCompositionSystem
    {
        private int m_PolygonPlanEpoch;
        internal bool SelectedIsPolygon => m_ManualMedians.TryGetValue(SelectedManualMedianId, out var median) && median.Points.Count >= 3;
        internal bool TryGetSelectedPolygonPoints(out RoadGrassResolvedMedianAnchor source, out List<float3> points)
        {
            source = null; points = null;
            return CanEditSelectedManualMedian && SelectedIsPolygon &&
                TryResolvePolygon(m_ManualMedians[SelectedManualMedianId], out source, out points);
        }

        internal float3 ProjectPolygonEditPoint(RoadGrassResolvedMedianAnchor source, float3 point) =>
            GetPolygonHeightProjection(source)?.Invoke(point) ?? point;

        internal bool SetSelectedPolygonPoints(IReadOnlyList<float3> points)
        {
            if (points == null || points.Count < 3 ||
                !TryGetSelectedPolygonPoints(out var source, out var original)) return false;
            foreach (var point in points) if (!math.all(math.isfinite(point))) return false;
            var median = m_ManualMedians[SelectedManualMedianId];
            if (!TryPlanPolygon(source, points, median.Saved.Shape, out var sections, out var attached,
                out var reason, median.Saved.ExtendFromMedian)) { ManualMessage = reason; return false; }
            var references = new List<RoadGrassSavedManualMedianVertex>(points.Count);
            foreach (var point in points)
            {
                int previous = original.IndexOf(point);
                references.Add(previous >= 0 && median.References.Count == original.Count ? median.References[previous] :
                    CapturePolygonReference(median.Saved.Id, source, point, false));
            }
            var vertices = EntityManager.GetBuffer<RoadGrassSavedManualMedianVertex>(m_CityQuery.GetSingletonEntity());
            int write = 0;
            for (int read = 0; read < vertices.Length; read++)
                if (vertices[read].Id != median.Saved.Id) vertices[write++] = vertices[read];
            vertices.ResizeUninitialized(write);
            median.Points.Clear(); median.References.Clear();
            median.ResolvedPoints = null;
            foreach (var vertex in references)
            { vertices.Add(vertex); median.Points.Add(vertex.Offset); median.References.Add(vertex); }
            median.Sections = sections; median.Halves = null; median.Attached = attached;
            median.HasOpening = false;
            foreach (bool opening in PolygonOpeningEdges(source, points)) median.HasOpening |= opening;
            median.PlanEpoch = m_PolygonPlanEpoch; median.ShapeMessage = reason;
            ManualMessage = reason; QueuePolygonEdit(median.Saved.Id);
            return true;
        }
        internal float4 SelectedPolygonShape => m_ManualMedians.TryGetValue(SelectedManualMedianId, out var median) ? median.Saved.Shape : default;
        internal float SelectedPolygonTextureTiling => m_ManualMedians.TryGetValue(SelectedManualMedianId, out var median) ? median.Saved.EffectiveTextureTiling : 1;
        internal bool SelectedPolygonCrosswalkGaps => m_ManualMedians.TryGetValue(SelectedManualMedianId, out var median) && median.Saved.CrosswalkGaps;
        internal void SetPolygonCrosswalkGaps(bool value)
        {
            if (!CanEditSelectedManualMedian || !SelectedIsPolygon) return;
            var median = m_ManualMedians[SelectedManualMedianId];
            if (median.Saved.CrosswalkGaps == value) return;
            median.Saved.CrosswalkGaps = value;
            var records = EntityManager.GetBuffer<RoadGrassSavedManualMedian>(m_CityQuery.GetSingletonEntity());
            for (int i = 0; i < records.Length; i++) if (records[i].Id == median.Saved.Id) records[i] = median.Saved;
            QueuePolygonEdit(median.Saved.Id);
        }
        internal void SetPolygonTextureTiling(float value)
        {
            if (!CanEditSelectedManualMedian || !SelectedIsPolygon || !math.isfinite(value)) return;
            var median = m_ManualMedians[SelectedManualMedianId];
            value = math.clamp(value, .25f, 4);
            if (value == median.Saved.EffectiveTextureTiling) return;
            median.Saved.TextureTiling = value;
            var records = EntityManager.GetBuffer<RoadGrassSavedManualMedian>(m_CityQuery.GetSingletonEntity());
            for (int i = 0; i < records.Length; i++) if (records[i].Id == median.Saved.Id) records[i] = median.Saved;
            if (RoadGrassPolygonPrototypeBatchSystem.Instance?.TryRetile(median.Saved.Id, value) != true) QueuePolygonEdit(median.Saved.Id);
        }
        internal bool SelectedPolygonExtend => m_ManualMedians.TryGetValue(SelectedManualMedianId, out var median) && median.Saved.ExtendFromMedian;
        internal float SelectedPolygonCurbWidth
        {
            get
            {
                if (!m_ManualMedians.TryGetValue(SelectedManualMedianId, out var median)) return .12f;
                if (median.Saved.CurbWidth > 0) return median.Saved.CurbWidth;
                if (median.NativeCurbWidth > 0) return median.NativeCurbWidth;
                if (TryResolveSavedAnchor(median.Saved.Edge, median.Saved.Anchor, out var source) &&
                    TryPolygonRenderProfile(source, median.Saved.Surface, out _, out _, out var profile, out _))
                    return median.NativeCurbWidth = profile.CurbWidth;
                return .12f;
            }
        }

        internal void SetPolygonCurbWidth(float value)
        {
            if (!CanEditSelectedManualMedian || !SelectedIsPolygon || !math.isfinite(value)) return;
            var median = m_ManualMedians[SelectedManualMedianId];
            value = value <= 0 ? 0 : math.clamp(value, .03f, 1);
            if (value == median.Saved.CurbWidth) return;
            median.Saved.CurbWidth = value;
            median.Sections = null; median.Halves = null;
            var records = EntityManager.GetBuffer<RoadGrassSavedManualMedian>(m_CityQuery.GetSingletonEntity());
            for (int i = 0; i < records.Length; i++) if (records[i].Id == median.Saved.Id) records[i] = median.Saved;
            QueuePolygonEdit(median.Saved.Id);
        }
        internal bool SelectedPolygonHasOpening
        {
            get
            {
                return m_ManualMedians.TryGetValue(SelectedManualMedianId, out var median) && median.HasOpening;
            }
        }
        internal float SelectedPolygonOffsetLimit => 2;
        private bool EnsurePolygonPlan(ManualMedian median, out RoadGrassResolvedMedianAnchor source)
        {
            if (!TryResolveSavedAnchor(median.Saved.Edge, median.Saved.Anchor, out source)) return false;
            if (median.Sections != null && median.PlanEpoch == m_PolygonPlanEpoch) return true;
            if (!TryResolvePolygon(median, out source, out var points))
            {
                median.ShapeMessage = "Polygon outline could not be resolved";
                if (median.Saved.Id == SelectedManualMedianId) ManualMessage = median.ShapeMessage;
                return false;
            }
            if (!TryPlanPolygon(source, points, median.Saved.Shape, out var sections, out var attached, out var reason, median.Saved.ExtendFromMedian))
            {
                median.ShapeMessage = reason;
                if (median.Saved.Id == SelectedManualMedianId) ManualMessage = reason;
                return false;
            }
            median.Sections = sections; median.Attached = attached; median.ShapeMessage = reason;
            median.HasOpening = false;
            foreach (bool opening in PolygonOpeningEdges(source, points)) median.HasOpening |= opening;
            median.PlanEpoch = m_PolygonPlanEpoch; median.Halves = null;
            if (median.Saved.Id == SelectedManualMedianId) ManualMessage = reason;
            return true;
        }
        internal void SetPolygonShape(int field, float value)
        {
            if (!CanEditSelectedManualMedian || !SelectedIsPolygon || !math.isfinite(value) || field < -1 || field > 3) return;
            var median = m_ManualMedians[SelectedManualMedianId];
            if (field == -1) SetPolygonCurbWidth(0);
            var shape = median.Saved.Shape;
            if (field == -1) shape = default;
            else
            {
                shape[field] = math.clamp(value, 0, SelectedPolygonOffsetLimit);
                if (!SelectedPolygonHasOpening) { if (field < 2) shape.y = shape.x; else shape.w = shape.z; }
            }
            if (math.all(shape == median.Saved.Shape)) return;
            if (!TryResolvePolygon(median, out var source, out var points) ||
                !TryPlanPolygon(source, points, shape, out var sections, out var attached, out var reason, median.Saved.ExtendFromMedian)) return;
            median.Saved.Shape = shape; median.Sections = sections; median.Halves = null;
            median.PlanEpoch = m_PolygonPlanEpoch; median.Attached = attached; median.ShapeMessage = reason;
            var records = EntityManager.GetBuffer<RoadGrassSavedManualMedian>(m_CityQuery.GetSingletonEntity());
            for (int i = 0; i < records.Length; i++) if (records[i].Id == median.Saved.Id) records[i] = median.Saved;
            ManualMessage = reason; QueuePolygonEdit(median.Saved.Id);
        }

        internal void SetPolygonExtend(bool value)
        {
            if (!CanEditSelectedManualMedian || !SelectedIsPolygon) return;
            var median = m_ManualMedians[SelectedManualMedianId];
            if (median.Saved.ExtendFromMedian == value) return;
            median.Saved.ExtendFromMedian = value;
            median.Sections = null; median.Halves = null;
            var records = EntityManager.GetBuffer<RoadGrassSavedManualMedian>(m_CityQuery.GetSingletonEntity());
            for (int i = 0; i < records.Length; i++) if (records[i].Id == median.Saved.Id) records[i] = median.Saved;
            QueuePolygonEdit(median.Saved.Id);
        }
    }
}
