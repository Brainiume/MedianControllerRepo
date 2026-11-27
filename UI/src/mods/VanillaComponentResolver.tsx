import { Theme, UniqueFocusKey } from "cs2/bindings";
import { ModuleRegistry } from "cs2/modding";
import { HTMLAttributes, ReactNode } from "react";

type LocalizedStringValue = { __Type: "Game.UI.Localization.LocalizedString"; id: null; value: string; args: null; };

type ToolButtonProps = {
  focusKey?: UniqueFocusKey | null;
  src: string;
  selected?: boolean;
  disabled?: boolean;
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
  toolOptionsPanelTheme: ["game-ui/game/components/tool-options/tool-options-panel.module.scss", "classes"],
  FOCUS_DISABLED: ["game-ui/common/focus/focus-key.ts", "FOCUS_DISABLED"],
  Slider: ["game-ui/common/input/slider/slider.tsx", "Slider"],
  Dropdown: ["game-ui/common/input/dropdown/dropdown.tsx", "Dropdown"],
  DropdownToggle: ["game-ui/common/input/dropdown/dropdown-toggle.tsx", "DropdownToggle"],
  DropdownItem: ["game-ui/common/input/dropdown/items/dropdown-item.tsx", "DropdownItem"],
  Scrollable: ["game-ui/common/scrolling/scrollable.tsx", "Scrollable"],
  dropdownTheme: ["game-ui/game/themes/game-dropdown.module.scss", "classes"],
  Panel: ["game-ui/common/panel/panel.tsx", "Panel"],
  Portal: ["game-ui/common/portal/portal.tsx", "Portal"],
  PanelTitleBar: ["game-ui/common/panel/panel-title-bar.tsx", "PanelTitleBar"],
  InfoSection: ["game-ui/game/components/selected-info-panel/shared-components/info-section/info-section.tsx", "InfoSection"],
  InfoSectionFoldout: ["game-ui/game/components/selected-info-panel/shared-components/info-section/info-section-foldout.tsx", "InfoSectionFoldout"],
  InfoRow: ["game-ui/game/components/selected-info-panel/shared-components/info-row/info-row.tsx", "InfoRow"],
  Checkbox: ["game-ui/common/input/toggle/checkbox/checkbox.tsx", "Checkbox"],
  Tooltip: ["game-ui/common/tooltip/tooltip.tsx", "Tooltip"],
  locStrValue: ["game-ui/common/localization/localization-bindings.ts", "locStrValue"],
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
  public get toolOptionsPanelTheme(): Theme {
    return this.resolve("toolOptionsPanelTheme");
  }

  public get FOCUS_DISABLED(): UniqueFocusKey {
    return this.resolve("FOCUS_DISABLED");
  }
  public get Slider(): (props: { start: number; end: number; value: number; onChange: (value: number) => void; theme?: Theme; disabled?: boolean; gamepadStep?: number; }) => JSX.Element {
    return this.resolve("Slider");
  }
  public get dropdownTheme(): Theme { return this.resolve("dropdownTheme"); }
  public get Scrollable(): (props: { children: ReactNode; className?: string; style?: React.CSSProperties; trackVisibility?: "scrollable" | "always" | "reserve"; }) => JSX.Element { return this.resolve("Scrollable"); }
  public get Panel(): React.ForwardRefExoticComponent<React.HTMLAttributes<HTMLElement> & { header?: ReactNode; footer?: ReactNode; allowFocusExit?: boolean; } & React.RefAttributes<HTMLElement>> { return this.resolve("Panel"); }
  public get Portal(): (props: { children: ReactNode; }) => JSX.Element { return this.resolve("Portal"); }
  public get PanelTitleBar(): (props: { children: ReactNode; }) => JSX.Element { return this.resolve("PanelTitleBar"); }
  public get InfoSection(): (props: { children: ReactNode; disableFocus?: boolean; }) => JSX.Element { return this.resolve("InfoSection"); }
  public get InfoSectionFoldout(): (props: { header: ReactNode; children: ReactNode; initialExpanded?: boolean; disableFocus?: boolean; onToggleExpanded?: (expanded: boolean) => void; }) => JSX.Element { return this.resolve("InfoSectionFoldout"); }
  public get InfoRow(): (props: { icon?: string; left?: ReactNode; right?: ReactNode; disableFocus?: boolean; }) => JSX.Element { return this.resolve("InfoRow"); }
  public get Checkbox(): (props: { checked: boolean; disabled?: boolean; debugName?: string; onChange: (value: boolean) => void; }) => JSX.Element { return this.resolve("Checkbox"); }
  public get Tooltip(): (props: { tooltip: ReactNode; children: ReactNode; }) => JSX.Element { return this.resolve("Tooltip"); }
  public get locStrValue(): (value: string) => LocalizedStringValue { return this.resolve("locStrValue"); }
  public get Dropdown(): (props: { focusKey?: UniqueFocusKey; initialFocused?: UniqueFocusKey; theme?: Theme; content: ReactNode; children: ReactNode; }) => JSX.Element {
    return this.resolve("Dropdown");
  }
  public get DropdownToggle(): (props: { disabled?: boolean; className?: string; children: ReactNode; "aria-label"?: string; }) => JSX.Element {
    return this.resolve("DropdownToggle");
  }
  public get DropdownItem(): (props: { value: number; icon?: string; iconTint?: boolean; disabled?: boolean; selected?: boolean; className?: string; tooltip?: LocalizedStringValue; onChange: (value: number) => void; children: ReactNode; }) => JSX.Element {
    return this.resolve("DropdownItem");
  }
}
