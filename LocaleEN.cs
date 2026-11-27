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
                // Edit the quoted English text. Keep locale keys and nameof(...) references unchanged.
                // Settings: Label = visible name, Desc = hover description, Warning = confirmation message.

                // Settings: mod name and tabs
                { "RoadGrass.MANUAL[MoreSettings]", "More Settings" },
                { m_Settings.GetSettingsLocaleID(), "Median Controller" },
                { m_Settings.GetOptionTabLocaleID("General"), "General" },
                { m_Settings.GetOptionTabLocaleID("Developer"), "Developer" },

                // Settings: General > Logging
                { m_Settings.GetOptionGroupLocaleID("Logging"), "Logging" },
                {
                    m_Settings.GetOptionLabelLocaleID(nameof(RoadGrassSettings.VerboseLogging)),
                    "Verbose logging"
                },
                {
                    m_Settings.GetOptionDescLocaleID(nameof(RoadGrassSettings.VerboseLogging)),
                    "Write diagnostic logs (stored in RoadGrass.log). Disabled by default; enable when " +
                    "troubleshooting."
                },

                // Settings: General > Save data
                { m_Settings.GetOptionGroupLocaleID("SaveData"), "Save data" },
                {
                    m_Settings.GetOptionLabelLocaleID(nameof(RoadGrassSettings.PreventAutomaticMedianDeletion)),
                    "Never automatically delete medians"
                },
                {
                    m_Settings.GetOptionDescLocaleID(nameof(RoadGrassSettings.PreventAutomaticMedianDeletion)),
                    "Prevent automatic median deletion when existing roads change. Medians always delete if " +
                    "their parent road is deleted or no longer exists. Manual removal, Reset and clearing " +
                    "city data still work."
                },
                {
                    m_Settings.GetOptionLabelLocaleID(nameof(RoadGrassSettings.ClearCurrentCity)),
                    "Remove all Median Controller data from this city"
                },
                {
                    m_Settings.GetOptionDescLocaleID(nameof(RoadGrassSettings.ClearCurrentCity)),
                    "Remove all Median Controller connections, endpoint cap and surface styles, and overrides " +
                    "from the loaded city. Save the city afterward to make the removal permanent."
                },
                {
                    m_Settings.GetOptionWarningLocaleID(nameof(RoadGrassSettings.ClearCurrentCity)),
                    "Remove all Median Controller connections, endpoint styles, and legacy road overrides " +
                    "from this city? This cannot be undone after saving."
                },

                // Settings: General > Custom median textures
                { m_Settings.GetOptionGroupLocaleID("Textures"), "Custom median textures" },
                {
                    m_Settings.GetOptionLabelLocaleID(nameof(RoadGrassSettings.OpenTextureImportForm)),
                    "Open texture importer"
                },
                {
                    m_Settings.GetOptionDescLocaleID(nameof(RoadGrassSettings.OpenTextureImportForm)),
                    "Open the surface texture importer in your browser. Select a ZIP, folder or texture " +
                    "files. The browser detects maps and builds the material in your custom surfaces folder. " +
                    "Refresh custom surfaces afterward."
                },
                {
                    m_Settings.GetOptionLabelLocaleID(nameof(RoadGrassSettings.RefreshCustomSurfaces)),
                    "Refresh custom surfaces"
                },
                {
                    m_Settings.GetOptionDescLocaleID(nameof(RoadGrassSettings.RefreshCustomSurfaces)),
                    "Reload finished material files."
                },
                {
                    m_Settings.GetOptionLabelLocaleID(nameof(RoadGrassSettings.OpenTextureImportFolder)),
                    "Open texture import folder"
                },
                {
                    m_Settings.GetOptionDescLocaleID(nameof(RoadGrassSettings.OpenTextureImportFolder)),
                    "Create a folder per material inside Imports. Add basecolor.png (required) and optional " +
                    "normal.png/normaldx.png, maskmap.png, metallic.png, occlusion.png, roughness.png or " +
                    "smoothness.png. PNGs up to 4096 pixels. Then press Import textures."
                },
                {
                    m_Settings.GetOptionLabelLocaleID(nameof(RoadGrassSettings.ImportMedianTextures)),
                    "Import textures"
                },
                {
                    m_Settings.GetOptionDescLocaleID(nameof(RoadGrassSettings.ImportMedianTextures)),
                    "Import Custom Median Surface Textures from mod folders."
                },
                {
                    m_Settings.GetOptionLabelLocaleID(nameof(RoadGrassSettings.TextureImportStatus)),
                    "Texture import status"
                },
                {
                    m_Settings.GetOptionDescLocaleID(nameof(RoadGrassSettings.TextureImportStatus)),
                    "Results of the last import."
                },

                // Settings: General > Median transitions
                { m_Settings.GetOptionGroupLocaleID("Transitions"), "Median transitions" },
                {
                    m_Settings.GetOptionLabelLocaleID(nameof(RoadGrassSettings.NarrowMedianStretchFactor)),
                    "Narrow median stretch factor"
                },
                {
                    m_Settings.GetOptionDescLocaleID(nameof(RoadGrassSettings.NarrowMedianStretchFactor)),
                    "Adjust the smaller median at the join between different-width connections. 0.84 is " +
                    "default. Lower values narrow it and higher values widen it. Updates existing connections " +
                    "immediately."
                },
                {
                    m_Settings.GetOptionLabelLocaleID(nameof(RoadGrassSettings.OneToTwoMedianStretchFactor)),
                    "1m to 2m median stretch factor"
                },
                {
                    m_Settings.GetOptionDescLocaleID(nameof(RoadGrassSettings.OneToTwoMedianStretchFactor)),
                    "Adjust 1m to 2m median joins. Lower values narrow it and higher values widen it. Updates " +
                    "existing connections immediately."
                },

                // Settings: Developer > Experimental features
                { m_Settings.GetOptionGroupLocaleID("Experimental"), "Experimental features" },
                {
                    m_Settings.GetOptionLabelLocaleID(nameof(RoadGrassSettings.EnableCrosswalkGaps)),
                    "Enable Crosswalk gaps"
                },
                {
                    m_Settings.GetOptionDescLocaleID(nameof(RoadGrassSettings.EnableCrosswalkGaps)),
                    "WIP Crosswalk Gaps."
                },

                // Polygon median properties and surface names
                { m_Settings.GetOptionGroupLocaleID("IntersectionCapture"), "Intersection capture (temporary developer tool)" },
                { m_Settings.GetOptionLabelLocaleID(nameof(RoadGrassSettings.CaptureIntersection)), "Capture intersection" },
                { m_Settings.GetOptionDescLocaleID(nameof(RoadGrassSettings.CaptureIntersection)), "Activate intersection capture tool. Close settings, hover a node and click its blue highlight to save intersection data." },
                { m_Settings.GetOptionLabelLocaleID(nameof(RoadGrassSettings.OpenIntersectionCaptures)), "Open intersection captures folder" },
                { m_Settings.GetOptionDescLocaleID(nameof(RoadGrassSettings.OpenIntersectionCaptures)), "Open ModsData/MedianController/IntersectionCaptures in your game user-data folder." },
                { m_Settings.GetOptionLabelLocaleID(nameof(RoadGrassSettings.IntersectionCaptureStatus)), "Capture status" },
                { m_Settings.GetOptionDescLocaleID(nameof(RoadGrassSettings.IntersectionCaptureStatus)), "Result of the latest capture" },
                { "RoadGrass.MANUAL[Title]", "Manual median" },
                { "RoadGrass.MANUAL[PolygonTitle]", "Polygon Median" },
                { "RoadGrass.MANUAL[EditPoints]", "Edit" },
                {
                    "RoadGrass.MANUAL[EditPointsHint]",
                    "Drag points to reshape the median, click an edge to add a point, or use the secondary " +
                    "action on a point to remove it"
                },
                { "RoadGrass.MANUAL[Surface]", "Surface" },
                { "RoadGrass.MANUAL[SurfaceGridView]", "Grid view" },
                { "RoadGrass.MANUAL[SurfaceListView]", "List view" },
                { "RoadGrass.MANUAL[Grass]", "Grass" },
                { "RoadGrass.MANUAL[Plain]", "Pavement" },
                { "RoadGrass.MANUAL[TerrainGrass]", "Terrain grass" },
                { "RoadGrass.MANUAL[TerrainDirt]", "Terrain dirt" },
                { "RoadGrass.MANUAL[TerrainRock]", "Terrain rock" },
                { "RoadGrass.MANUAL[TerrainExtra1]", "Dry" },
                { "RoadGrass.MANUAL[TerrainExtra2]", "Lush" },
                { "RoadGrass.MANUAL[TerrainExtra3]", "Dirt" },
                { "RoadGrass.MANUAL[TerrainExtra4]", "Sand" },
                { "RoadGrass.MANUAL[TextureTiling]", "Texture tiling" },
                { "RoadGrass.MANUAL[ResetSurface]", "Reset Surface" },
                { "RoadGrass.MANUAL[Remove]", "Remove" },
                { "RoadGrass.MANUAL[RemoveHint]", "Remove median" },
                { "RoadGrass.MANUAL[CrosswalkGaps]", "Crosswalk gaps" },
                {
                    "RoadGrass.MANUAL[CrosswalkGapsHint]",
                    "Leave detected crosswalks at street level by cutting gaps through this median"
                },
                {
                    "RoadGrass.MANUAL[CrosswalkGapsLocked]",
                    "Temporarily disabled. Enable Crosswalk gaps in Developer settings."
                },

                // Tool name and section headings
                { "Assets.NAME[Median Controller]", "Median Controller" },
                {
                    "Assets.DESCRIPTION[Median Controller]",
                    "Connect median endpoints, edit their end caps and surfaces, or reset selected endpoint " +
                    "changes."
                },
                { "RoadGrass.SECTION_TITLE[ToolMode]", "Median Controller" },
                { "RoadGrass.SECTION_TITLE[EndCap]", "End Cap" },
                { "RoadGrass.SECTION_TITLE[Surface]", "Surface" },
                { "RoadGrass.SECTION_TITLE[Remove]", "Remove" },
                { "RoadGrass.SECTION_TITLE[RestoreArea]", "Restore grass" },

                // Mouse action hints
                {
                    "RoadGrass.MOUSE_TOOLTIP[EditPolygonPoints]",
                    "Drag a point, or click an edge to add a point"
                },
                { "RoadGrass.MOUSE_TOOLTIP[DragPolygonPoint]", "Hold and drag point; release to apply" },
                { "RoadGrass.MOUSE_TOOLTIP[InsertPolygonPoint]", "Add point on this edge" },
                { "RoadGrass.MOUSE_TOOLTIP[RemovePolygonPoint]", "Remove point, or cancel the current drag" },
                { "RoadGrass.MOUSE_TOOLTIP[SelectTaperStart]", "Select a base median style" },
                { "RoadGrass.MOUSE_TOOLTIP[PickTaperStart]", "Use this median's style" },
                { "RoadGrass.MOUSE_TOOLTIP[AddPolygonPoint]", "Add polygon point" },
                { "RoadGrass.MOUSE_TOOLTIP[ClosePolygon]", "Create median" },
                { "RoadGrass.MOUSE_TOOLTIP[SelectManualMedian]", "Select median" },
                { "RoadGrass.MOUSE_TOOLTIP[SelectTaperEnd]", "Choose a lane-guide snap point" },
                { "RoadGrass.MOUSE_TOOLTIP[PlaceTaperEnd]", "Place median" },
                { "RoadGrass.MOUSE_TOOLTIP[CancelSelection]", "Cancel selection" },
                { "RoadGrass.MOUSE_TOOLTIP[SelectEndpoint]", "Hover a median strip or endpoint" },
                { "RoadGrass.MOUSE_TOOLTIP[EditEndpoint]", "Apply cap and surface" },
                { "RoadGrass.MOUSE_TOOLTIP[ResetEndpoint]", "Reset selected categories" },
                { "RoadGrass.MOUSE_TOOLTIP[EditSegment]", "Apply surface" },
                { "RoadGrass.MOUSE_TOOLTIP[ResetSegment]", "Reset segment surface" },
                { "RoadGrass.MOUSE_TOOLTIP[NoResetFilters]", "Select a reset filter" },
                { "RoadGrass.MOUSE_TOOLTIP[UnsupportedStyle]", "Style unavailable for this median" },
                { "RoadGrass.MOUSE_TOOLTIP[ResetManualMedian]", "Remove this polygon median" },
                { "RoadGrass.MOUSE_TOOLTIP[SelectFirst]", "Hover over a node" },
                { "RoadGrass.MOUSE_TOOLTIP[NoMedian]", "No selectable median" },
                { "RoadGrass.MOUSE_TOOLTIP[SelectFirstHovered]", "Select median" },
                { "RoadGrass.MOUSE_TOOLTIP[AutoConnect]", "Connect strips" },
                { "RoadGrass.MOUSE_TOOLTIP[RemoveLinked]", "Remove connection" },
                { "RoadGrass.MOUSE_TOOLTIP[SelectSecond]", "Select another median at this node" },
                { "RoadGrass.MOUSE_TOOLTIP[SelectedAgain]", "Cancel selection" },
                { "RoadGrass.MOUSE_TOOLTIP[ValidSecond]", "Connect strips" },
                { "RoadGrass.MOUSE_TOOLTIP[InvalidSameRoad]", "Choose a different road" },
                { "RoadGrass.MOUSE_TOOLTIP[InvalidDifferentJunction]", "Choose the same node" },
                { "RoadGrass.MOUSE_TOOLTIP[InvalidGeometry]", "Incompatible median geometry" },

                // Tool button titles and descriptions
                { "RoadGrass.TOOLTIP_TITLE[Connect]", "Connect" },
                {
                    "RoadGrass.TOOLTIP_DESCRIPTION[Connect]",
                    "Connect two median endpoints or roadside grass strips through an intersection. The " +
                    "secondary action cancels a selection or removes the complete connection."
                },
                { "RoadGrass.TOOLTIP_TITLE[Edit]", "Edit" },
                {
                    "RoadGrass.TOOLTIP_DESCRIPTION[Edit]",
                    "Select a polygon median to open its edit controls. You can also edit a road segment " +
                    "median surface or an endpoint's cap and surface."
                },
                { "RoadGrass.TOOLTIP_TITLE[Reset]", "Reset" },
                {
                    "RoadGrass.TOOLTIP_DESCRIPTION[Reset]",
                    "Click a polygon median to remove it. On roads, reset the highlighted segment surface or " +
                    "the selected endpoint categories."
                },
                { "RoadGrass.TOOLTIP_TITLE[Rounded]", "Rounded" },
                { "RoadGrass.TOOLTIP_DESCRIPTION[Rounded]", "Use rounded endpoint cap." },
                { "RoadGrass.TOOLTIP_TITLE[Straight]", "Straight" },
                {
                    "RoadGrass.TOOLTIP_DESCRIPTION[Straight]",
                    "Use the closed, flat median cap found beside a sidewalk crossing."
                },
                { "RoadGrass.TOOLTIP_TITLE[Grass]", "Grass" },
                { "RoadGrass.TOOLTIP_DESCRIPTION[Grass]", "Restore grass on the highlighted segment median." },
                { "RoadGrass.TOOLTIP_TITLE[Plain]", "Plain" },
                { "RoadGrass.TOOLTIP_DESCRIPTION[Plain]", "Remove grass from the highlighted segment median." },
                { "RoadGrass.TOOLTIP_TITLE[All]", "All" },
                {
                    "RoadGrass.TOOLTIP_DESCRIPTION[All]",
                    "Select all removal filters, or clear them when any are selected."
                },
                { "RoadGrass.TOOLTIP_TITLE[Connection]", "Connection" },
                {
                    "RoadGrass.TOOLTIP_DESCRIPTION[Connection]",
                    "Remove the complete connection, including the paired endpoint."
                },
                { "RoadGrass.TOOLTIP_TITLE[EndCap]", "End Cap" },
                { "RoadGrass.TOOLTIP_DESCRIPTION[EndCap]", "Clear only this endpoint's cap override." },
                { "RoadGrass.TOOLTIP_TITLE[Surface]", "Surface" },
                {
                    "RoadGrass.TOOLTIP_DESCRIPTION[Surface]",
                    "Restore the highlighted segment median's original surface."
                },
                { "RoadGrass.TOOLTIP_TITLE[Median]", "Median grass" },
                { "RoadGrass.TOOLTIP_DESCRIPTION[Median]", "Restore only grass patches in the middle median." },
                { "RoadGrass.TOOLTIP_TITLE[RoadEdge]", "Road-edge grass" },
                {
                    "RoadGrass.TOOLTIP_DESCRIPTION[RoadEdge]",
                    "Restore only grass patches along the outer edges of the road."
                },
                { "RoadGrass.TOOLTIP_TITLE[JoinMedian]", "Join median" },
                {
                    "RoadGrass.TOOLTIP_DESCRIPTION[JoinMedian]",
                    "Select two median pieces at the same node. Different road types and widths are " +
                    "supported. The secondary action cancels or removes the complete link."
                },
                { "RoadGrass.TOOLTIP_TITLE[RestoreVanilla]", "Restore vanilla" },
                {
                    "RoadGrass.TOOLTIP_DESCRIPTION[RestoreVanilla]",
                    "Remove Median Controller changes from the selected road and restore its vanilla " +
                    "appearance."
                },
            };
        }

        public void Unload()
        {
        }
    }
}
