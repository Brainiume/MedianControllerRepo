using Game.Modding;
using Game.Settings;
using Colossal.IO.AssetDatabase;
using Game.UI;

namespace RoadGrass
{
    [FileLocation("MedianController")]
    public sealed class RoadGrassSettings : ModSetting
    {
        internal const float DefaultStretchFactor = 0.84f;
        private float m_NarrowMedianStretchFactor = DefaultStretchFactor;
        private float m_OneToTwoMedianStretchFactor = DefaultStretchFactor;
        internal static float AppliedStretchFactor { get; private set; } = DefaultStretchFactor;
        internal static float AppliedOneToTwoStretchFactor { get; private set; } = DefaultStretchFactor;
        internal static bool VerboseLoggingEnabled { get; private set; }
        internal static bool AutomaticMedianDeletionDisabled { get; private set; }
        internal static bool CrosswalkGapsEnabled { get; private set; }
        public RoadGrassSettings(IMod mod) : base(mod)
        {
            SetDefaults();
        }

        public override void SetDefaults()
        {
            NarrowMedianStretchFactor = DefaultStretchFactor;
            OneToTwoMedianStretchFactor = DefaultStretchFactor;
            VerboseLogging = false;
            PreventAutomaticMedianDeletion = false;
            EnableCrosswalkGaps = false;
        }

        [SettingsUISection("General", "Logging")]
        public bool VerboseLogging { get; set; }

        [SettingsUISection("General", "SaveData")]
        public bool PreventAutomaticMedianDeletion { get; set; }

        [SettingsUISection("Developer", "Experimental")]
        public bool EnableCrosswalkGaps { get; set; }

        [SettingsUISection("Developer", "IntersectionCapture")]
        [SettingsUIButton]
        [SettingsUIDisableByCondition(typeof(RoadGrassSettings), nameof(NoCityLoaded))]
        public bool CaptureIntersection { set => MedianIntersectionCaptureTool.Instance?.Activate(); }

        [SettingsUISection("Developer", "IntersectionCapture")]
        [SettingsUIButton]
        public bool OpenIntersectionCaptures { set => MedianIntersectionCaptureTool.OpenFolder(); }

        [SettingsUISection("Developer", "IntersectionCapture")]
        public string IntersectionCaptureStatus => MedianIntersectionCaptureTool.Status;

        [SettingsUISection("General", "Textures")]
        [SettingsUIButton]
        public bool OpenTextureImportForm { set => RoadGrassTextureImportForm.Open(); }

        [SettingsUISection("General", "Textures")]
        [SettingsUIButton]
        public bool RefreshCustomSurfaces { set => RoadGrassCustomTextures.RefreshLibrary(); }

        [SettingsUISection("General", "Textures")]
        [SettingsUIButton]
        public bool OpenTextureImportFolder { set => RoadGrassCustomTextures.OpenImports(); }

        [SettingsUISection("General", "Textures")]
        [SettingsUIButton]
        public bool ImportMedianTextures { set => RoadGrassCustomTextures.Import(); }

        [SettingsUISection("General", "Textures")]
        public string TextureImportStatus => RoadGrassCustomTextures.Status;

        [SettingsUISection("General", "Transitions")]
        [SettingsUISlider(min = 0.5f, max = 1.5f, step = 0.01f, scalarMultiplier = 1, unit = Unit.kFloatTwoFractions)]
        public float NarrowMedianStretchFactor
        {
            get => m_NarrowMedianStretchFactor;
            set => m_NarrowMedianStretchFactor = float.IsNaN(value) || float.IsInfinity(value)
                ? DefaultStretchFactor : System.Math.Max(0.5f, System.Math.Min(1.5f, value));
        }

        [SettingsUISection("General", "Transitions")]
        [SettingsUISlider(min = 0.5f, max = 1.5f, step = 0.01f, scalarMultiplier = 1, unit = Unit.kFloatTwoFractions)]
        public float OneToTwoMedianStretchFactor
        {
            get => m_OneToTwoMedianStretchFactor;
            set => m_OneToTwoMedianStretchFactor = float.IsNaN(value) || float.IsInfinity(value)
                ? DefaultStretchFactor : System.Math.Max(0.5f, System.Math.Min(1.5f, value));
        }

        internal static float GetStretchFactor(float widthA, float widthB) =>
            System.Math.Abs(System.Math.Min(widthA, widthB) - 1f) < .01f &&
            System.Math.Abs(System.Math.Max(widthA, widthB) - 2f) < .01f
                ? AppliedOneToTwoStretchFactor : AppliedStretchFactor;

        public override void Apply()
        {
            VerboseLoggingEnabled = VerboseLogging;
            AutomaticMedianDeletionDisabled = PreventAutomaticMedianDeletion;
            CrosswalkGapsEnabled = EnableCrosswalkGaps;
            if (AppliedStretchFactor != NarrowMedianStretchFactor || AppliedOneToTwoStretchFactor != OneToTwoMedianStretchFactor)
            {
                AppliedStretchFactor = NarrowMedianStretchFactor;
                AppliedOneToTwoStretchFactor = OneToTwoMedianStretchFactor;
                RoadGrassJoinCompositionSystem.Instance?.RefreshTransitionCalibration();
            }
            base.Apply();
        }

        [SettingsUISection("General", "SaveData")]
        [SettingsUIButton]
        [SettingsUIConfirmation]
        [SettingsUIDisableByCondition(typeof(RoadGrassSettings), nameof(NoCityLoaded))]
        public bool ClearCurrentCity
        {
            set => RoadGrassClearDataSystem.Instance?.RequestClear();
        }

        public static bool NoCityLoaded() =>
            RoadGrassClearDataSystem.Instance?.CanClear != true;
    }
}
