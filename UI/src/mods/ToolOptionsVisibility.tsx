import { bindValue, useValue } from "cs2/api";
import { ModuleRegistryExtend } from "cs2/modding";
import mod from "../../mod.json";

const isActive$ = bindValue<boolean>(mod.id, "IsActive");

export const ToolOptionsVisibility: ModuleRegistryExtend = (Component: any) => {
  return () => {
    const active = useValue(isActive$);
    const vanillaVisible = Component();
    return vanillaVisible || active;
  };
};
