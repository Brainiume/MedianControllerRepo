using Colossal.UI.Binding;
using Game;
using Game.Prefabs;
using Game.Tools;
using Game.UI;
using UnityEngine.Scripting;

namespace RoadGrass
{
    // Retained for legacy road-override save compatibility.
    [System.Flags]
    internal enum RoadGrassArea
    {
        None = 0, Median = 1, RoadEdge = 2, JoinMedian = 4, RestoreVanilla = 8
    }

    [Preserve]
    internal partial class RoadGrassUISystem : UISystemBase
    {
        internal const string ModId = "RoadGrass";
        private ToolSystem m_ToolSystem;
        private NetToolSystem m_NetToolSystem;
        private RoadGrassJoinToolSystem m_JoinToolSystem;
        private ValueBinding<int> m_ModeBinding;
        private ValueBinding<int> m_CapBinding;
        private ValueBinding<int> m_SurfaceBinding;
        private ValueBinding<int> m_ResetBinding;
        private ValueBinding<bool> m_IsActiveBinding;
        private ValueBinding<bool> m_ExtendBinding;
        private ValueBinding<bool> m_SnappingBinding;
        internal bool PolygonSnapping { get; private set; } = true;
        private ValueBinding<int> m_SelectedManualBinding, m_ManualSurfaceBinding;
        private ValueBinding<bool> m_CanEditManualBinding;
        private ValueBinding<string> m_ManualMessageBinding;
        private ValueBinding<bool> m_PolygonSelectedBinding, m_PolygonOpeningBinding;
        private ValueBinding<float> m_LineOffsetBinding, m_MedianOffsetBinding, m_LineRadiusBinding, m_MedianRadiusBinding, m_OffsetLimitBinding;
        private bool m_Synchronizing;

        internal MedianControllerMode ActiveMode { get; private set; } = MedianControllerMode.Connect;
        internal MedianCapChoice EditCapChoice { get; private set; } = MedianCapChoice.Rounded;
        internal bool ExtendIntoIntersection { get; private set; }
        internal MedianSurfaceChoice EditSurfaceChoice { get; private set; } = MedianSurfaceChoice.Grass;
        internal MedianResetFilters ResetFilters { get; private set; } = MedianResetFilters.All;
        public override GameMode gameMode => GameMode.GameOrEditor;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            m_ToolSystem = World.GetOrCreateSystemManaged<ToolSystem>();
            m_NetToolSystem = World.GetOrCreateSystemManaged<NetToolSystem>();
            m_JoinToolSystem = World.GetOrCreateSystemManaged<RoadGrassJoinToolSystem>();
            AddBinding(m_SnappingBinding = new ValueBinding<bool>(ModId, "PolygonSnapping", PolygonSnapping));
            AddBinding(new TriggerBinding<bool>(ModId, "SetPolygonSnapping", value =>
            { PolygonSnapping = value; m_SnappingBinding.Update(value); }));
            AddBinding(m_SelectedManualBinding = new ValueBinding<int>(ModId, "SelectedManualMedianId", 0));
            AddBinding(m_ManualSurfaceBinding = new ValueBinding<int>(ModId, "SelectedManualMedianSurface", 0));
            AddBinding(m_CanEditManualBinding = new ValueBinding<bool>(ModId, "CanEditSelectedManualMedian", false));
            AddBinding(m_ManualMessageBinding = new ValueBinding<string>(ModId, "ManualMedianMessage", ""));
            AddBinding(m_PolygonSelectedBinding = new ValueBinding<bool>(ModId, "SelectedIsPolygon", false));
            AddBinding(m_PolygonOpeningBinding = new ValueBinding<bool>(ModId, "PolygonHasOpening", false));
            AddBinding(m_LineOffsetBinding = new ValueBinding<float>(ModId, "PolygonLineOffset", 0));
            AddBinding(m_MedianOffsetBinding = new ValueBinding<float>(ModId, "PolygonMedianOffset", 0));
            AddBinding(m_LineRadiusBinding = new ValueBinding<float>(ModId, "PolygonLineRadius", 0));
            AddBinding(m_MedianRadiusBinding = new ValueBinding<float>(ModId, "PolygonMedianRadius", 0));
            AddBinding(m_OffsetLimitBinding = new ValueBinding<float>(ModId, "PolygonOffsetLimit", 1));
            AddBinding(new TriggerBinding<int, float>(ModId, "SetPolygonShape", (field, value) => RoadGrassJoinCompositionSystem.Instance?.SetPolygonShape(field, value)));
            AddBinding(new TriggerBinding<int>(ModId, "SetManualMedianSurface", value =>
            {
                var manager = RoadGrassJoinCompositionSystem.Instance;
                if (manager != null && value >= 0 && value <= 2)
                    manager.SetManualSurface(manager.SelectedManualMedianId, (MedianSavedSurface)value);
            }));
            AddBinding(new TriggerBinding(ModId, "RemoveManualMedian", () =>
            {
                var manager = RoadGrassJoinCompositionSystem.Instance;
                manager?.RemoveManualMedian(manager.SelectedManualMedianId);
            }));
            AddBinding(m_ModeBinding = new ValueBinding<int>(ModId, "ActiveMode", (int)ActiveMode));
            AddBinding(m_CapBinding = new ValueBinding<int>(ModId, "EditCapChoice", (int)EditCapChoice));
            AddBinding(m_SurfaceBinding = new ValueBinding<int>(ModId, "EditSurfaceChoice", (int)EditSurfaceChoice));
            AddBinding(m_ResetBinding = new ValueBinding<int>(ModId, "ResetFilters", (int)ResetFilters));
            AddBinding(m_IsActiveBinding = new ValueBinding<bool>(ModId, "IsActive", false));
            AddBinding(m_ExtendBinding = new ValueBinding<bool>(ModId, "ExtendIntoIntersection", false));
            AddBinding(new TriggerBinding<bool>(ModId, "SetExtendIntoIntersection", value =>
            {
                ExtendIntoIntersection = value;
                m_JoinToolSystem.ClearTransientSelection();
                m_ExtendBinding.Update(value);
            }));
            AddBinding(new TriggerBinding<int>(ModId, "SetActiveMode", SetActiveMode));
            AddBinding(new TriggerBinding<int>(ModId, "SetEditCapChoice", value =>
            {
                if (value < 0 || value > (int)MedianCapChoice.Straight) return;
                EditCapChoice = (MedianCapChoice)value;
                m_CapBinding.Update(value);
            }));
            AddBinding(new TriggerBinding<int>(ModId, "SetEditSurfaceChoice", value =>
            {
                if (value < 0 || value > (int)MedianSurfaceChoice.Plain) return;
                EditSurfaceChoice = (MedianSurfaceChoice)value;
                m_SurfaceBinding.Update(value);
            }));
            AddBinding(new TriggerBinding<int>(ModId, "SetResetFilters", value =>
            {
                if (value < 0 || (value & ~(int)MedianResetFilters.All) != 0) return;
                ResetFilters = (MedianResetFilters)value;
                m_ResetBinding.Update(value);
            }));
            m_ToolSystem.EventToolChanged += OnToolChanged;
            m_ToolSystem.EventPrefabChanged += OnPrefabChanged;
        }

        private void OnToolChanged(ToolBaseSystem tool) => SynchronizeTool();
        private void OnPrefabChanged(PrefabBase prefab) => SynchronizeTool();

        private void SynchronizeTool()
        {
            if (m_Synchronizing) return;
            m_Synchronizing = true;
            try
            {
                if (m_ToolSystem.activeTool == m_NetToolSystem &&
                    m_NetToolSystem.prefab?.name == RoadGrassPrefabSystem.PrefabName)
                {
                    m_JoinToolSystem.SetToolPrefab(m_NetToolSystem.prefab);
                    m_ToolSystem.activeTool = m_JoinToolSystem;
                }
                m_IsActiveBinding.Update(m_ToolSystem.activeTool == m_JoinToolSystem &&
                    m_JoinToolSystem.GetPrefab()?.name == RoadGrassPrefabSystem.PrefabName);
            }
            finally { m_Synchronizing = false; }
        }

        internal void SetActiveMode(int value)
        {
            if (value < 0 || value > (int)MedianControllerMode.Polygon) return;
            if (ActiveMode != (MedianControllerMode)value)
            {
                ActiveMode = (MedianControllerMode)value;
                ExtendIntoIntersection = false;
                m_ExtendBinding.Update(false);
                m_JoinToolSystem.ClearTransientSelection();
                m_ModeBinding.Update(value);
            }
            SynchronizeTool();
        }

        protected override void OnUpdate()
        {
            base.OnUpdate();
            var manager = RoadGrassJoinCompositionSystem.Instance;
            m_SelectedManualBinding.Update(manager?.SelectedManualMedianId ?? 0);
            m_ManualSurfaceBinding.Update(manager?.SelectedManualMedianSurface ?? 0);
            m_CanEditManualBinding.Update(manager?.CanEditSelectedManualMedian ?? false);
            m_ManualMessageBinding.Update(manager?.ManualMessage ?? "");
            m_PolygonSelectedBinding.Update(manager?.SelectedIsPolygon ?? false);
            m_PolygonOpeningBinding.Update(manager?.SelectedPolygonHasOpening ?? false);
            var shape = manager?.SelectedPolygonShape ?? default;
            m_LineOffsetBinding.Update(shape.x); m_MedianOffsetBinding.Update(shape.y);
            m_LineRadiusBinding.Update(shape.z); m_MedianRadiusBinding.Update(shape.w);
            m_OffsetLimitBinding.Update(manager?.SelectedPolygonOffsetLimit ?? 1);
        }

        [Preserve]
        protected override void OnDestroy()
        {
            m_ToolSystem.EventToolChanged -= OnToolChanged;
            m_ToolSystem.EventPrefabChanged -= OnPrefabChanged;
            base.OnDestroy();
        }
    }
}
