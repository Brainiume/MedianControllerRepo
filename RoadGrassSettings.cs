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
        internal static float AppliedStretchFactor { get; private set; } = DefaultStretchFactor;
        public RoadGrassSettings(IMod mod) : base(mod)
        {
            SetDefaults();
        }

        public override void SetDefaults()
        {
            NarrowMedianStretchFactor = DefaultStretchFactor;
        }

        [SettingsUISection("General", "Transitions")]
        [SettingsUISlider(min = 0.5f, max = 1.5f, step = 0.01f, scalarMultiplier = 1, unit = Unit.kFloatTwoFractions)]
        public float NarrowMedianStretchFactor
        {
            get => m_NarrowMedianStretchFactor;
            set => m_NarrowMedianStretchFactor = float.IsNaN(value) || float.IsInfinity(value)
                ? DefaultStretchFactor : System.Math.Max(0.5f, System.Math.Min(1.5f, value));
        }

        public override void Apply()
        {
            if (AppliedStretchFactor != NarrowMedianStretchFactor)
            {
                AppliedStretchFactor = NarrowMedianStretchFactor;
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
