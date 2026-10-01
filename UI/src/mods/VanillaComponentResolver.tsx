import { Theme, UniqueFocusKey } from "cs2/bindings";
import { ModuleRegistry } from "cs2/modding";
import { HTMLAttributes, ReactNode } from "react";

type ToolButtonProps = {
  focusKey?: UniqueFocusKey | null;
  src: string;
  selected?: boolean;
  tooltip?: string | ReactNode | null;
  className?: string;
  onSelect?: (value: unknown) => unknown;
} & HTMLAttributes<unknown>;

type SectionProps = {
  title?: string | null;
  children: JSX.Element | JSX.Element[];
};

const registryIndex = {
  Section: ["game-ui/game/components/tool-options/mouse-tool-options/mouse-tool-options.tsx", "Section"],
  ToolButton: ["game-ui/game/components/tool-options/tool-button/tool-button.tsx", "ToolButton"],
  toolButtonTheme: ["game-ui/game/components/tool-options/tool-button/tool-button.module.scss", "classes"],
  FOCUS_DISABLED: ["game-ui/common/focus/focus-key.ts", "FOCUS_DISABLED"],
  Slider: ["game-ui/common/input/slider/slider.tsx", "Slider"],
  sliderTheme: ["game-ui/editor/themes/editor-slider.module.scss", "classes"]
} as const;

export class VanillaComponentResolver {
  private static current?: VanillaComponentResolver;
  private readonly cache: Partial<Record<keyof typeof registryIndex, any>> = {};

  public static get instance(): VanillaComponentResolver {
    return this.current!;
  }

  public static setRegistry(registry: ModuleRegistry): void {
    this.current = new VanillaComponentResolver(registry);
  }

  private constructor(private readonly registry: ModuleRegistry) {}

  private resolve(entry: keyof typeof registryIndex): any {
    const [path, exportName] = registryIndex[entry];
    return (this.cache[entry] ??= this.registry.registry.get(path)![exportName]);
  }

  public get Section(): (props: SectionProps) => JSX.Element {
    return this.resolve("Section");
  }

  public get ToolButton(): (props: ToolButtonProps) => JSX.Element {
    return this.resolve("ToolButton");
  }

  public get toolButtonTheme(): Theme {
    return this.resolve("toolButtonTheme");
  }

  public get FOCUS_DISABLED(): UniqueFocusKey {
    return this.resolve("FOCUS_DISABLED");
  }
  public get Slider(): (props: { start: number; end: number; value: number; onChange: (value: number) => void; theme: Theme; disabled?: boolean; }) => JSX.Element {
    return this.resolve("Slider");
  }
  public get sliderTheme(): Theme { return this.resolve("sliderTheme"); }
}
