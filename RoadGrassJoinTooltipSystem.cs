using Game;
using Game.Input;
using Game.Tools;
using Game.UI.Localization;
using Game.UI.Tooltip;
using UnityEngine.Scripting;

namespace RoadGrass
{
    internal enum RoadGrassJoinTooltipState
    {
        SelectFirst,
        NoMedian,
        SelectFirstHovered,
        AutoConnect,
        RemoveLinked,
        SelectSecond,
        SelectedAgain,
        ValidSecond,
        InvalidSameRoad,
        InvalidDifferentJunction,
        InvalidGeometry,
        SelectEndpoint,
        EditEndpoint,
        ResetEndpoint,
        NoResetFilters,
        UnsupportedStyle,
        UnsupportedTaper,
        SelectTaperStart,
        PickTaperStart,
        SelectTaperEnd,
        ManualGuideRejected,
        PlaceTaperEnd,
        EditSegment,
        ResetSegment,
        SelectPolygon,
        SelectManualMedian,
        ResetManualMedian,
        AddPolygonPoint,
        ClosePolygon,
        InvalidPolygon,
        RemovePolygon,
        PolygonMaterialUnavailable
    }

    /// <summary>
    /// State-aware mouse guidance following the vanilla/Better Bulldozer
    /// TooltipSystemBase pattern. The tool owns the state; this UI-phase system
    /// only publishes the localized tooltip.
    /// </summary>
    [Preserve]
    internal partial class RoadGrassJoinTooltipSystem : TooltipSystemBase
    {
        private ToolSystem m_ToolSystem;
        private RoadGrassJoinToolSystem m_JoinToolSystem;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            m_ToolSystem = World.GetOrCreateSystemManaged<ToolSystem>();
            m_JoinToolSystem = World.GetOrCreateSystemManaged<RoadGrassJoinToolSystem>();
        }

        [Preserve]
        protected override void OnUpdate()
        {
            if (m_ToolSystem.activeTool != m_JoinToolSystem)
            {
                return;
            }

            RoadGrassJoinTooltipState state = m_JoinToolSystem.TooltipState;
            GetTooltip(
                state,
                out TooltipColor color,
                out string localeKey,
                out string fallback);
            AddMouseTooltip(new StringTooltip
            {
                path = $"RoadGrass.JoinMedian.{state}",
                value = LocalizedString.IdWithFallback(localeKey, fallback),
                color = color
            });
            AddInputHint(GetInputAction(state));
            if (state == RoadGrassJoinTooltipState.SelectManualMedian)
            {
                AddMouseTooltip(new StringTooltip
                {
                    path = "RoadGrass.JoinMedian.RemoveManualMedian",
                    value = LocalizedString.IdWithFallback("RoadGrass.MANUAL[RemoveHint]", "Remove only this manual median")
                });
                AddInputHint(m_JoinToolSystem.SecondaryApplyInput);
            }
            if (m_JoinToolSystem.HasPendingSelection && state != RoadGrassJoinTooltipState.SelectedAgain)
            {
                AddMouseTooltip(new StringTooltip
                {
                    path = "RoadGrass.JoinMedian.CancelSelection",
                    value = m_JoinToolSystem.HasPolygonCorners
                        ? LocalizedString.IdWithFallback("RoadGrass.MOUSE_TOOLTIP[UndoPolygonPoint]", "Remove last polygon corner")
                        : LocalizedString.IdWithFallback("RoadGrass.MOUSE_TOOLTIP[CancelSelection]", "Cancel selection")
                });
                AddInputHint(m_JoinToolSystem.SecondaryApplyInput);
            }
        }

        private void AddInputHint(IProxyAction input)
        {
            if (input == null) return;
            var device = InputManager.instance.activeControlScheme == InputManager.ControlScheme.KeyboardAndMouse
                ? InputManager.DeviceType.Keyboard | InputManager.DeviceType.Mouse : InputManager.DeviceType.Gamepad;
            void Add(ProxyAction action)
            {
                if (!action.enabled) return;
                var hint = new InputHintTooltip(action);
                hint.Refresh(device);
                AddMouseTooltip(hint);
            }
            if (input is ProxyAction action) Add(action);
            else if (input is UIBaseInputAction.IState state)
                foreach (var actionState in state.actions) Add(actionState);
        }

        private IProxyAction GetInputAction(RoadGrassJoinTooltipState state)
        {
            switch (state)
            {
                case RoadGrassJoinTooltipState.RemoveLinked:
                case RoadGrassJoinTooltipState.SelectedAgain:
                case RoadGrassJoinTooltipState.RemovePolygon:
                    return m_JoinToolSystem.SecondaryApplyInput;
                case RoadGrassJoinTooltipState.EditSegment:
                case RoadGrassJoinTooltipState.PickTaperStart:
                case RoadGrassJoinTooltipState.SelectManualMedian:
                case RoadGrassJoinTooltipState.ResetManualMedian:
                case RoadGrassJoinTooltipState.AddPolygonPoint:
                case RoadGrassJoinTooltipState.ClosePolygon:
                case RoadGrassJoinTooltipState.PlaceTaperEnd:
                case RoadGrassJoinTooltipState.ResetSegment:
                case RoadGrassJoinTooltipState.EditEndpoint:
                case RoadGrassJoinTooltipState.ResetEndpoint:
                case RoadGrassJoinTooltipState.SelectFirstHovered:
                case RoadGrassJoinTooltipState.AutoConnect:
                case RoadGrassJoinTooltipState.ValidSecond:
                    return m_JoinToolSystem.ApplyInput;
                default:
                    return null;
            }
        }

        private static void GetTooltip(
            RoadGrassJoinTooltipState state,
            out TooltipColor color,
            out string localeKey,
            out string fallback)
        {
            color = TooltipColor.Info;
            localeKey = $"RoadGrass.MOUSE_TOOLTIP[{state}]";
            switch (state)
            {
                case RoadGrassJoinTooltipState.EditSegment:
                    fallback = "Apply surface";
                    break;
                case RoadGrassJoinTooltipState.ResetSegment:
                    fallback = "Reset segment surface";
                    break;
                case RoadGrassJoinTooltipState.SelectEndpoint:
                    fallback = "Hover a median strip or endpoint";
                    break;
                case RoadGrassJoinTooltipState.EditEndpoint:
                    fallback = "Apply cap and surface";
                    break;
                case RoadGrassJoinTooltipState.ResetEndpoint:
                    fallback = "Reset selected categories";
                    break;
                case RoadGrassJoinTooltipState.NoResetFilters:
                    color = TooltipColor.Warning;
                    fallback = "Select a reset filter";
                    break;
                case RoadGrassJoinTooltipState.UnsupportedStyle:
                    color = TooltipColor.Error;
                    fallback = "Style unavailable for this median";
                    break;
                case RoadGrassJoinTooltipState.UnsupportedTaper:
                    color = TooltipColor.Error;
                    fallback = "No safe intersection taper for this median";
                    break;
                case RoadGrassJoinTooltipState.SelectTaperStart:
                    fallback = "Hover the median endpoint to extend";
                    break;
                case RoadGrassJoinTooltipState.PickTaperStart:
                    fallback = "Select median start";
                    break;
                case RoadGrassJoinTooltipState.SelectTaperEnd:
                    fallback = "Choose an end point in front of the median";
                    break;
                case RoadGrassJoinTooltipState.PlaceTaperEnd:
                    fallback = "Place rounded taper — snaps to lane intersections";
                    break;
                case RoadGrassJoinTooltipState.NoMedian:
                    color = TooltipColor.Warning;
                    fallback = "No selectable median";
                    break;
                case RoadGrassJoinTooltipState.SelectPolygon:
                    fallback = "Hover an intersection to draw a median";
                    break;
                case RoadGrassJoinTooltipState.SelectManualMedian:
                    fallback = "Select manual median";
                    break;
                case RoadGrassJoinTooltipState.ResetManualMedian:
                    fallback = "Remove this polygon median";
                    break;
                case RoadGrassJoinTooltipState.AddPolygonPoint:
                    fallback = "Add contour point — click the first point to finish";
                    break;
                case RoadGrassJoinTooltipState.ClosePolygon:
                    fallback = "Close contour and create median";
                    break;
                case RoadGrassJoinTooltipState.RemovePolygon:
                    fallback = "Remove polygon median";
                    break;
                case RoadGrassJoinTooltipState.PolygonMaterialUnavailable:
                    color = TooltipColor.Error;
                    fallback = "Polygon rendering unavailable — contour retained";
                    break;
                case RoadGrassJoinTooltipState.ManualGuideRejected:
                    color = TooltipColor.Warning;
                    fallback = RoadGrassJoinCompositionSystem.Instance?.ManualMessage ?? "Cannot build this contour";
                    break;
                case RoadGrassJoinTooltipState.InvalidPolygon:
                    color = TooltipColor.Warning;
                    fallback = "Contour crosses itself or is too narrow for the padding and curb";
                    break;
                case RoadGrassJoinTooltipState.SelectFirstHovered:
                    fallback = "Select median";
                    break;
                case RoadGrassJoinTooltipState.AutoConnect:
                    fallback = "Connect medians";
                    break;
                case RoadGrassJoinTooltipState.RemoveLinked:
                    fallback = "Remove connection";
                    break;
                case RoadGrassJoinTooltipState.SelectSecond:
                    fallback = "Select another median at this junction";
                    break;
                case RoadGrassJoinTooltipState.SelectedAgain:
                    fallback = "Cancel selection";
                    break;
                case RoadGrassJoinTooltipState.ValidSecond:
                    fallback = "Connect medians";
                    break;
                case RoadGrassJoinTooltipState.InvalidSameRoad:
                    color = TooltipColor.Error;
                    fallback = "Choose a different road";
                    break;
                case RoadGrassJoinTooltipState.InvalidDifferentJunction:
                    color = TooltipColor.Error;
                    fallback = "Choose the same junction";
                    break;
                case RoadGrassJoinTooltipState.InvalidGeometry:
                    color = TooltipColor.Error;
                    fallback = "Incompatible median geometry";
                    break;
                default:
                    fallback = "Hover a junction";
                    break;
            }
        }
    }
}
