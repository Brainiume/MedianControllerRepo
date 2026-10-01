using System.Collections.Generic;
using Colossal;

namespace RoadGrass
{
    internal sealed class LocaleEN : IDictionarySource
    {
        private readonly RoadGrassSettings m_Settings;

        internal LocaleEN(RoadGrassSettings settings)
        {
            m_Settings = settings;
        }

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(
            IList<IDictionaryEntryError> errors,
            Dictionary<string, int> indexCounts)
        {
            return new Dictionary<string, string>
            {
                { "RoadGrass.MANUAL[Title]", "Manual median" },
                { "RoadGrass.MOUSE_TOOLTIP[SelectTaperStart]", "Select a base median style" },
                { "RoadGrass.MOUSE_TOOLTIP[PickTaperStart]", "Use this median's style" },
                { "RoadGrass.MOUSE_TOOLTIP[AddPolygonPoint]", "Add polygon point" },
                { "RoadGrass.MOUSE_TOOLTIP[ClosePolygon]", "Create median" },
                { "RoadGrass.MANUAL[Surface]", "Surface" },
                { "RoadGrass.MANUAL[Grass]", "Grass" },
                { "RoadGrass.MANUAL[Plain]", "Plain" },
                { "RoadGrass.MANUAL[ResetSurface]", "Reset Surface" },
                { "RoadGrass.MANUAL[Remove]", "Remove" },
                { "RoadGrass.MANUAL[RemoveHint]", "Remove median" },
                { "RoadGrass.MOUSE_TOOLTIP[SelectManualMedian]", "Select median" },
                { "RoadGrass.MOUSE_TOOLTIP[SelectTaperEnd]", "Choose a lane-guide snap point" },
                { "RoadGrass.MOUSE_TOOLTIP[PlaceTaperEnd]", "Place median" },
                { m_Settings.GetSettingsLocaleID(), "Median Controller" },
                { m_Settings.GetOptionTabLocaleID("General"), "General" },
                { m_Settings.GetOptionGroupLocaleID("SaveData"), "Save data" },
                { m_Settings.GetOptionGroupLocaleID("Transitions"), "Median transitions" },
                { m_Settings.GetOptionLabelLocaleID(nameof(RoadGrassSettings.NarrowMedianStretchFactor)), "Narrow median stretch factor" },
                { m_Settings.GetOptionDescLocaleID(nameof(RoadGrassSettings.NarrowMedianStretchFactor)), "Adjust the smaller median at the join between different-width connections. 0.84 is the calibrated default; 1.00 is unadjusted. Lower values narrow it and higher values widen it. Updates existing connections immediately. Original road widths and equal-width connections stay unchanged. Report the displayed value when calibrating." },
                { m_Settings.GetOptionLabelLocaleID(nameof(RoadGrassSettings.ClearCurrentCity)), "Remove all Median Controller data from this city" },
                { m_Settings.GetOptionDescLocaleID(nameof(RoadGrassSettings.ClearCurrentCity)), "Remove all Median Controller connections, endpoint cap and surface styles, and legacy road overrides from the loaded city. Save the city afterward to make the removal permanent." },
                { m_Settings.GetOptionWarningLocaleID(nameof(RoadGrassSettings.ClearCurrentCity)), "Remove all Median Controller connections, endpoint styles, and legacy road overrides from this city? This cannot be undone after saving." },
                { "Assets.NAME[Median Controller]", "Median Controller" },
                {
                    "Assets.DESCRIPTION[Median Controller]",
                    "Connect median endpoints, edit their end caps and surfaces, or reset selected endpoint changes."
                },
                { "RoadGrass.SECTION_TITLE[ToolMode]", "Median Controller" },
                { "RoadGrass.SECTION_TITLE[EndCap]", "End Cap" },
                { "RoadGrass.SECTION_TITLE[Surface]", "Surface" },
                { "RoadGrass.SECTION_TITLE[Remove]", "Remove" },
                { "RoadGrass.MOUSE_TOOLTIP[CancelSelection]", "Cancel selection" },
                { "RoadGrass.MOUSE_TOOLTIP[SelectEndpoint]", "Hover a median strip or endpoint" },
                { "RoadGrass.MOUSE_TOOLTIP[EditEndpoint]", "Apply cap and surface" },
                { "RoadGrass.MOUSE_TOOLTIP[ResetEndpoint]", "Reset selected categories" },
                { "RoadGrass.MOUSE_TOOLTIP[EditSegment]", "Apply surface" },
                { "RoadGrass.MOUSE_TOOLTIP[ResetSegment]", "Reset segment surface" },
                { "RoadGrass.MOUSE_TOOLTIP[NoResetFilters]", "Select a reset filter" },
                { "RoadGrass.MOUSE_TOOLTIP[UnsupportedStyle]", "Style unavailable for this median" },
                { "RoadGrass.TOOLTIP_TITLE[Connect]", "Connect" },
                { "RoadGrass.TOOLTIP_DESCRIPTION[Connect]", "Connect two median endpoints. Right-click cancels a selection or removes the complete connection." },
                { "RoadGrass.TOOLTIP_TITLE[Edit]", "Edit" },
                { "RoadGrass.TOOLTIP_DESCRIPTION[Edit]", "Select a polygon median to open its edit controls. You can also edit a road segment median surface or an endpoint's cap and surface." },
                { "RoadGrass.TOOLTIP_TITLE[Reset]", "Reset" },
                { "RoadGrass.TOOLTIP_DESCRIPTION[Reset]", "Click a polygon median to remove it. On roads, reset the highlighted segment surface or the selected endpoint categories." },
                { "RoadGrass.MOUSE_TOOLTIP[ResetManualMedian]", "Remove this polygon median" },
                { "RoadGrass.TOOLTIP_TITLE[Rounded]", "Rounded" },
                { "RoadGrass.TOOLTIP_DESCRIPTION[Rounded]", "Use rounded endpoint cap." },
                { "RoadGrass.TOOLTIP_TITLE[Straight]", "Straight" },
                { "RoadGrass.TOOLTIP_DESCRIPTION[Straight]", "Use the closed, flat median cap found beside a sidewalk crossing." },
                { "RoadGrass.TOOLTIP_TITLE[Grass]", "Grass" },
                { "RoadGrass.TOOLTIP_DESCRIPTION[Grass]", "Restore grass on the highlighted segment median." },
                { "RoadGrass.TOOLTIP_TITLE[Plain]", "Plain" },
                { "RoadGrass.TOOLTIP_DESCRIPTION[Plain]", "Remove grass from the highlighted segment median." },
                { "RoadGrass.TOOLTIP_TITLE[All]", "All" },
                { "RoadGrass.TOOLTIP_DESCRIPTION[All]", "Select all removal filters, or clear them when any are selected." },
                { "RoadGrass.TOOLTIP_TITLE[Connection]", "Connection" },
                { "RoadGrass.TOOLTIP_DESCRIPTION[Connection]", "Remove the complete connection, including the paired endpoint." },
                { "RoadGrass.TOOLTIP_TITLE[EndCap]", "End Cap" },
                { "RoadGrass.TOOLTIP_DESCRIPTION[EndCap]", "Clear only this endpoint's cap override." },
                { "RoadGrass.TOOLTIP_TITLE[Surface]", "Surface" },
                { "RoadGrass.TOOLTIP_DESCRIPTION[Surface]", "Restore the highlighted segment median's original surface." },
                { "RoadGrass.SECTION_TITLE[RestoreArea]", "Restore grass" },
                { "RoadGrass.TOOLTIP_TITLE[Median]", "Median grass" },
                { "RoadGrass.TOOLTIP_DESCRIPTION[Median]", "Restore only grass patches in the middle median." },
                { "RoadGrass.TOOLTIP_TITLE[RoadEdge]", "Road-edge grass" },
                { "RoadGrass.TOOLTIP_DESCRIPTION[RoadEdge]", "Restore only grass patches along the outer edges of the road." },
                { "RoadGrass.TOOLTIP_TITLE[JoinMedian]", "Join median" },
                { "RoadGrass.TOOLTIP_DESCRIPTION[JoinMedian]", "Select two median pieces at the same node. Different road types and widths are supported. Right-click cancels or removes the complete link." },
                { "RoadGrass.TOOLTIP_TITLE[RestoreVanilla]", "Restore vanilla" },
                { "RoadGrass.TOOLTIP_DESCRIPTION[RestoreVanilla]", "Remove Median Controller changes from the selected road and restore its vanilla appearance." },
                { "RoadGrass.MOUSE_TOOLTIP[SelectFirst]", "Hover over a node" },
                { "RoadGrass.MOUSE_TOOLTIP[NoMedian]", "No selectable median" },
                { "RoadGrass.MOUSE_TOOLTIP[SelectFirstHovered]", "Select median" },
                { "RoadGrass.MOUSE_TOOLTIP[AutoConnect]", "Connect medians" },
                { "RoadGrass.MOUSE_TOOLTIP[RemoveLinked]", "Remove connection" },
                { "RoadGrass.MOUSE_TOOLTIP[SelectSecond]", "Select another median at this node" },
                { "RoadGrass.MOUSE_TOOLTIP[SelectedAgain]", "Cancel selection" },
                { "RoadGrass.MOUSE_TOOLTIP[ValidSecond]", "Connect medians" },
                { "RoadGrass.MOUSE_TOOLTIP[InvalidSameRoad]", "Choose a different road" },
                { "RoadGrass.MOUSE_TOOLTIP[InvalidDifferentJunction]", "Choose the same node" },
                { "RoadGrass.MOUSE_TOOLTIP[InvalidGeometry]", "Incompatible median geometry" },
            };
        }

        public void Unload()
        {
        }
    }
}
