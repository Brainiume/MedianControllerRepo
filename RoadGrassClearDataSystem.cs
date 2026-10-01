using Game;
using Game.City;
using Colossal.Serialization.Entities;
using Unity.Entities;
using UnityEngine.Scripting;

namespace RoadGrass
{
    /// <summary>
    /// Runs the settings action once in a game update, before either persistence
    /// system can replay records from the current city.
    /// </summary>
    [Preserve]
    internal partial class RoadGrassClearDataSystem : GameSystemBase
    {
        private EntityQuery m_CityQuery;
        private bool m_GameReady;
        private bool m_ClearRequested;

        internal static RoadGrassClearDataSystem Instance { get; private set; }
        internal bool CanClear => m_GameReady && !m_CityQuery.IsEmptyIgnoreFilter;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            Instance = this;
            m_CityQuery = GetEntityQuery(ComponentType.ReadOnly<City>());
        }

        [Preserve]
        protected override void OnGamePreload(Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            m_GameReady = false;
            m_ClearRequested = false;
        }

        [Preserve]
        protected override void OnGameLoadingComplete(Purpose purpose, GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);
            m_GameReady = true;
        }

        internal void RequestClear()
        {
            if (CanClear)
            {
                m_ClearRequested = true;
            }
        }

        [Preserve]
        protected override void OnUpdate()
        {
            if (!m_ClearRequested || !CanClear)
            {
                return;
            }

            m_ClearRequested = false;
            RoadGrassJoinCompositionSystem.Instance?.ClearAllData();
            RoadGrassOverridePersistenceSystem.Instance?.ClearAllData();
            RoadGrassMeshInjectSystem.Instance?.RestoreAllToVanilla();
            Mod.TryInfo("Cleared all Median Controller connections, endpoint styles, and legacy road overrides from the current city; save the city to keep the removal");
        }

        [Preserve]
        protected override void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
            base.OnDestroy();
        }
    }
}
