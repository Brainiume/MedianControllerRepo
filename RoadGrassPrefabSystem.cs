using Colossal;
using Colossal.Serialization.Entities;
using Game;
using Game.Prefabs;
using Game.SceneFlow;
using Game.UI;
using UnityEngine;
using UnityEngine.Scripting;

namespace RoadGrass
{
    [Preserve]
    internal partial class RoadGrassPrefabSystem : UISystemBase
    {
        internal const string PrefabName = "Median Controller";
        private PrefabSystem m_PrefabSystem;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
        }

        [Preserve]
        protected override void OnGamePreload(Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);

            var toolID = new PrefabID(nameof(FencePrefab), PrefabName);
            if (m_PrefabSystem.TryGetPrefab(toolID, out _))
            {
                return;
            }

            var grassID = new PrefabID(nameof(FencePrefab), "Grass");
            if (!m_PrefabSystem.TryGetPrefab(grassID, out var grassPrefab) ||
                !grassPrefab.TryGet(out UIObject grassUI))
            {
                return;
            }

            PrefabBase toolPrefab = grassPrefab.Clone(PrefabName);

            toolPrefab.Remove<UIObject>();
            UIObject toolUI = ScriptableObject.CreateInstance<UIObject>();
            toolUI.name = PrefabName;
            toolUI.m_Group = grassUI.m_Group;
            toolUI.m_Icon = grassUI.m_Icon;
            toolUI.m_Priority = grassUI.m_Priority + 1;
            toolUI.m_IsDebugObject = grassUI.m_IsDebugObject;
            toolUI.active = grassUI.active;
            toolPrefab.AddComponentFrom(toolUI);

            if (!m_PrefabSystem.AddPrefab(toolPrefab))
            {
                Mod.TryError("Failed to add Median Controller to PrefabSystem");
                return;
            }

            Mod.TryInfo("Added Median Controller as a clone of the vanilla Grass road upgrade");
        }

        [Preserve]
        protected override void OnUpdate()
        {
            base.OnUpdate();
        }
    }
}
