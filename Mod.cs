using System;
using Colossal.Logging;
using Game;
using Game.Modding;
using Game.Prefabs;
using Game.SceneFlow;
using Game.Serialization;
using Game.Tools;

namespace RoadGrass
{
    public class Mod : IMod
    {
        public static readonly ILog Log = LogManager
            .GetLogger($"{nameof(RoadGrass)}.{nameof(Mod)}")
            .SetShowsErrorsInUI(false);

        private RoadGrassSettings m_Settings;
        internal static Game.Input.ProxyAction CloseToolAction { get; private set; }

        public void OnLoad(UpdateSystem updateSystem)
        {
            m_Settings = new RoadGrassSettings(this);
            Colossal.IO.AssetDatabase.AssetDatabase.global.LoadSettings("MedianController", m_Settings);
            m_Settings.Apply();

            TryInfo($"{nameof(OnLoad)} median-controller-endpoint-styles-v1");
            TryInfo("Build marker: segment-median-surfaces-v4");

            if (GameManager.instance.modManager.TryGetExecutableAsset(this, out var asset))
            {
                TryInfo($"Current mod asset at {asset.path}");
            }

            GameManager.instance.localizationManager.AddSource("en-US", new LocaleEN(m_Settings));
            m_Settings.RegisterInOptionsUI();
            m_Settings.RegisterKeyBindings();
            CloseToolAction = m_Settings.GetAction(RoadGrassSettings.CloseTool);
            if (CloseToolAction != null) CloseToolAction.shouldBeEnabled = false;

            updateSystem.UpdateAt<RoadGrassPrefabSystem>(SystemUpdatePhase.UIUpdate);
            updateSystem.UpdateAt<RoadGrassUISystem>(SystemUpdatePhase.UIUpdate);
            updateSystem.UpdateAt<RoadGrassJoinTooltipSystem>(SystemUpdatePhase.UITooltip);
            updateSystem.UpdateAt<RoadGrassJoinToolSystem>(SystemUpdatePhase.ToolUpdate);

            updateSystem.UpdateAfter<RoadGrassMeshInjectSystem, NetCompositionSystem>(SystemUpdatePhase.Modification4);
            updateSystem.UpdateAfter<RoadGrassClearDataSystem, NetCompositionSystem>(SystemUpdatePhase.Modification4);
            updateSystem.UpdateBefore<RoadGrassClearDataSystem, RoadGrassOverridePersistenceSystem>(SystemUpdatePhase.Modification4);
            updateSystem.UpdateBefore<RoadGrassClearDataSystem, RoadGrassJoinCompositionSystem>(SystemUpdatePhase.Modification4);
            updateSystem.UpdateBefore<RoadGrassClearDataSystem, RoadGrassMeshInjectSystem>(SystemUpdatePhase.Modification4);
            updateSystem.UpdateBefore<RoadGrassMeshInjectSystem, NetCompositionMeshRefSystem>(SystemUpdatePhase.Modification4);
            updateSystem.UpdateAfter<RoadGrassOverridePersistenceSystem, NetCompositionSystem>(SystemUpdatePhase.Modification4);
            updateSystem.UpdateBefore<RoadGrassOverridePersistenceSystem, RoadGrassMeshInjectSystem>(SystemUpdatePhase.Modification4);
            updateSystem.UpdateAfter<RoadGrassJoinCompositionSystem, NetCompositionSystem>(SystemUpdatePhase.Modification4);
            updateSystem.UpdateBefore<RoadGrassJoinCompositionSystem, NetCompositionMeshRefSystem>(SystemUpdatePhase.Modification4);
            updateSystem.UpdateAfter<RoadGrassMeshRestoreSystem, NetCompositionMeshRefSystem>(SystemUpdatePhase.Modification4);
            updateSystem.UpdateAfter<RoadGrassJoinCompositionFinalizeSystem, NetCompositionMeshRefSystem>(SystemUpdatePhase.Modification4);
            updateSystem.UpdateBefore<RoadGrassMeshRestoreSystem, Game.Net.GeometrySystem>(SystemUpdatePhase.Modification4);
            updateSystem.UpdateAfter<RoadGrassJoinGeometryInvalidationSystem, Game.Net.GeometrySystem>(SystemUpdatePhase.Modification4);

            // Vanilla keeps ownership of the real endpoint batch. The extra
            // median-only instance is allocated after vanilla's group actions,
            // then receives pair-only matrices after the normal data pass.
            updateSystem.UpdateAfter<
                RoadGrassJoinMedianBatchSystem,
                Game.Rendering.BatchInstanceSystem.Groups>(
                SystemUpdatePhase.Rendering);
            updateSystem.UpdateBefore<
                RoadGrassJoinMedianBatchSystem,
                Game.Rendering.BatchDataSystem>(
                SystemUpdatePhase.Rendering);
            updateSystem.UpdateAfter<
                RoadGrassJoinMedianBatchDataSystem,
                Game.Rendering.BatchDataSystem>(
                SystemUpdatePhase.Rendering);

            updateSystem.UpdateBefore<RoadGrassPolygonPrototypeBatchSystem, Game.Rendering.ManagedBatchSystem>(SystemUpdatePhase.Rendering);
            updateSystem.UpdateBefore<RoadGrassPolygonPrototypeDataSystem, Game.Rendering.BatchDataSystem>(SystemUpdatePhase.Rendering);

            updateSystem.UpdateBefore<RoadGrassJoinSaveRestoreSystem, BeginPrefabSerializationSystem>(SystemUpdatePhase.Serialize);
            updateSystem.UpdateAfter<RoadGrassJoinSaveReapplySystem, WriteSystem>(SystemUpdatePhase.Serialize);
        }

        public void OnDispose()
        {
            RoadGrassPolygonPrototypeBatchSystem.Instance?.Shutdown();
            m_Settings?.UnregisterInOptionsUI();
            if (CloseToolAction != null) CloseToolAction.shouldBeEnabled = false;
            CloseToolAction = null;
            m_Settings = null;
            TryInfo(nameof(OnDispose));
        }

        /// <summary>
        /// Write through the native file sink inside our exception boundary.
        /// Unity's LogFormat handler otherwise reports sink failures itself,
        /// before an outer catch around Info/Warn/Error can handle them.
        /// </summary>
        internal static void TryInfo(string message)
        {
            TryWrite(Level.Info, message);
        }

        internal static void TryWarn(string message)
        {
            TryWrite(Level.Warn, message);
        }

        internal static void TryError(string message)
        {
            TryWrite(Level.Error, message);
        }

        private static void TryWrite(Level level, string message)
        {
            if (!RoadGrassSettings.VerboseLoggingEnabled) return;
            try
            {
                if (Log is UnityLogger logger && logger.isLevelEnabled(level))
                    logger.Internal_WriteStream(null, level, message, null, null);
            }
            catch (Exception)
            {
            }
        }
    }
}
