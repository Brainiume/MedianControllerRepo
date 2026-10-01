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
        internal float4 SelectedPolygonShape => m_ManualMedians.TryGetValue(SelectedManualMedianId, out var median) ? median.Saved.Shape : default;
        internal bool SelectedPolygonHasOpening
        {
            get
            {
                return m_ManualMedians.TryGetValue(SelectedManualMedianId, out var median) && median.HasOpening;
            }
        }
        internal float SelectedPolygonOffsetLimit
        {
            get
            {
                if (!m_ManualMedians.TryGetValue(SelectedManualMedianId, out var median) || median.Points.Count == 0) return 1;
                float2 low = median.Points[0].xz, high = low;
                foreach (var p in median.Points) { low = math.min(low, p.xz); high = math.max(high, p.xz); }
                return math.clamp(math.length(high - low), 1, 100);
            }
        }
        private bool EnsurePolygonPlan(ManualMedian median, out RoadGrassResolvedMedianAnchor source)
        {
            if (!TryResolveSavedAnchor(median.Saved.Edge, median.Saved.Anchor, out source)) return false;
            if (median.Sections != null && median.PlanEpoch == m_PolygonPlanEpoch) return true;
            if (!TryResolvePolygon(median, out source, out var points) ||
                !TryPlanPolygon(source, points, median.Saved.Shape, out var sections, out var attached, out var reason)) return false;
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
            var shape = median.Saved.Shape;
            if (field == -1) shape = default;
            else
            {
                shape[field] = math.clamp(value, 0, field < 2 ? SelectedPolygonOffsetLimit : 10);
                if (!SelectedPolygonHasOpening) { if (field < 2) shape.y = shape.x; else shape.w = shape.z; }
            }
            if (math.all(shape == median.Saved.Shape)) return;
            if (!TryResolvePolygon(median, out var source, out var points) ||
                !TryPlanPolygon(source, points, shape, out var sections, out var attached, out var reason)) return;
            median.Saved.Shape = shape; median.Sections = sections; median.Halves = null;
            median.PlanEpoch = m_PolygonPlanEpoch; median.Attached = attached; median.ShapeMessage = reason;
            var records = EntityManager.GetBuffer<RoadGrassSavedManualMedian>(m_CityQuery.GetSingletonEntity());
            for (int i = 0; i < records.Length; i++) if (records[i].Id == median.Saved.Id) records[i] = median.Saved;
            ManualMessage = reason; m_Dirty = true;
        }
    }
}
