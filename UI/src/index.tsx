import { ModRegistrar } from "cs2/modding";
import mod from "../mod.json";
import { RoadGrassToolOptions } from "mods/RoadGrassToolOptions";
import { ToolOptionsVisibility } from "mods/ToolOptionsVisibility";
import { VanillaComponentResolver } from "mods/VanillaComponentResolver";

const register: ModRegistrar = (moduleRegistry) => {
  VanillaComponentResolver.setRegistry(moduleRegistry);

  moduleRegistry.extend(
    "game-ui/game/components/tool-options/mouse-tool-options/mouse-tool-options.tsx",
    "MouseToolOptions",
    RoadGrassToolOptions
  );

  moduleRegistry.extend(
    "game-ui/game/components/tool-options/tool-options-panel.tsx",
    "useToolOptionsVisible",
    ToolOptionsVisibility
  );

  console.log(`${mod.id} UI module registrations completed.`);
};

export default register;
