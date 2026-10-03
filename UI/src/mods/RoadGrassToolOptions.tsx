import { bindValue, trigger, useValue } from "cs2/api";
import { useLocalization } from "cs2/l10n";
import { getModule, ModuleRegistryExtend } from "cs2/modding";
import { Children, cloneElement } from "react";
import mod from "../../mod.json";
import { VanillaComponentResolver } from "./VanillaComponentResolver";
import { MedianManagementPanel } from "./MedianManagementPanel";
import connectIcon from "../icons/connect.svg";
import roundedIcon from "../icons/rounded.svg";
import straightIcon from "../icons/straight.svg";
import polygonIcon from "../icons/polygon.svg";
import grassIcon from "../icons/grass.svg";
import plainIcon from "../icons/plain.svg";
import allIcon from "../icons/all.svg";

const mode$ = bindValue<number>(mod.id, "ActiveMode");
const cap$ = bindValue<number>(mod.id, "EditCapChoice");
const surface$ = bindValue<number>(mod.id, "EditSurfaceChoice");
const filters$ = bindValue<number>(mod.id, "ResetFilters");
const active$ = bindValue<boolean>(mod.id, "IsActive");
const selectedManual$ = bindValue<number>(mod.id, "SelectedManualMedianId");
const selectedPolygon$ = bindValue<boolean>(mod.id, "SelectedIsPolygon");
const snapping$ = bindValue<boolean>(mod.id, "PolygonSnapping");
const placingPolygonPoints$ = bindValue<boolean>(mod.id, "PlacingPolygonPoints");
const polygonExtend$ = bindValue<boolean>(mod.id, "PolygonExtendFromMedian");
const tooltipStyle = getModule(
  "game-ui/common/tooltip/description-tooltip/description-tooltip.module.scss", "classes"
);
const sectionStyle = getModule(
  "game-ui/game/components/tool-options/mouse-tool-options/mouse-tool-options.module.scss", "classes"
);
// Scope the Figma column layout to these reused vanilla sections. rem follows game UI scaling.
const panelStyle = `
.median-controller-options { position: relative; width: 380rem; padding: 6rem 0; }
.median-controller-options .${sectionStyle.item} { min-height: 39rem; padding: 0 12rem; justify-content: flex-start; }
.median-controller-options .${sectionStyle.label} { flex: 0 0 132rem; width: 132rem; margin-right: 18rem; }
.median-controller-options .${sectionStyle.content} { justify-content: flex-start; margin-left: 0; }
`;

export const RoadGrassToolOptions: ModuleRegistryExtend = (Component: any) => () => {
  const active = useValue(active$);
  const mode = useValue(mode$);
  const selectedManual = useValue(selectedManual$);
  const selectedPolygon = useValue(selectedPolygon$);
  const snapping = useValue(snapping$);
  const placingPolygonPoints = useValue(placingPolygonPoints$);
  const polygonExtend = useValue(polygonExtend$);
  const cap = useValue(cap$);
  const surface = useValue(surface$);
  const filters = useValue(filters$);
  const { translate } = useLocalization();
  const result: JSX.Element = Component();
  if (!active) return result;
  const { Section, ToolButton, toolButtonTheme, FOCUS_DISABLED } = VanillaComponentResolver.instance;
  const title = (key: string, fallback: string) => translate(`RoadGrass.SECTION_TITLE[${key}]`, fallback);
  const button = (key: string, label: string, description: string, src: string,
    selected: boolean, action: string, value: number | boolean, disabled = false) => (
    <ToolButton key={key} src={src} selected={selected} aria-disabled={disabled} style={{opacity: disabled ? 0.5 : 1}} focusKey={FOCUS_DISABLED}
      className={toolButtonTheme.button} aria-label={label}
      onSelect={() => { if (!disabled) trigger(mod.id, action, value); }}
      tooltip={<div>
        <div className={tooltipStyle.title}>{translate(`RoadGrass.TOOLTIP_TITLE[${key}]`, label)}</div>
        <div className={tooltipStyle.content}>{translate(`RoadGrass.TOOLTIP_DESCRIPTION[${key}]`, description)}</div>
      </div>} />
  );
  const rows = [
    <Section key="mode" title={title("ToolMode", "Median Controller")}>
      {button("Connect", "Connect", "Connect two compatible median endpoints. The secondary action cancels a selection or removes the complete connection.", connectIcon, mode === 0, "SetActiveMode", 0)}
      {button("Edit", "Edit", "Edit a segment median surface, or apply the cap and surface together at an endpoint.", "coui://uil/Standard/Pencil.svg", mode === 1, "SetActiveMode", 1)}
      {button("Reset", "Reset", "Reset the highlighted segment surface, or the selected categories at an endpoint.", "coui://uil/Standard/XClose.svg", mode === 2, "SetActiveMode", 2)}
      {button("ManualContour", "Contour", "Select an existing median's style, then draw polygon corners anywhere on the road. Lane and marking snaps are optional. Select the first corner to finish. The secondary action undoes the last corner, cancels an empty drawing, or removes a placed median.", polygonIcon, mode === 3, "SetActiveMode", 3)}
    </Section>
  ];
  if (mode === 1 && !selectedManual) rows.push(
    <Section key="cap" title={title("EndCap", "End Cap")}>
      {button("Rounded", "Rounded", "Use the prefab's rounded endpoint cap.", roundedIcon, cap === 0, "SetEditCapChoice", 0)}
      {button("Straight", "Straight", "Use the closed, flat median cap found beside a sidewalk crossing.", straightIcon, cap === 1, "SetEditCapChoice", 1)}
    </Section>,
    <Section key="surface" title={title("Surface", "Surface")}>
      {button("Grass", "Grass", "Restore grass on the highlighted segment median.", grassIcon, surface === 0, "SetEditSurfaceChoice", 0)}
      {button("Plain", "Plain", "Remove grass from the highlighted segment median.", plainIcon, surface === 1, "SetEditSurfaceChoice", 1)}
    </Section>
  );
    if (mode === 3 && !(selectedManual && selectedPolygon)) {
        rows.push(
            <Section key="polygonExtend" title={title("PolygonExtendFromMedian", "Extend from median")}>
                {button("PolygonExtendFromMedian", "Extend from median", "Open the joining curb and remove the original median end cap where the polygon joins its two end corners. Turn off to keep the polygon closed.", connectIcon, polygonExtend, "SetPolygonExtendFromMedian", !polygonExtend)}
            </Section>
        );
    }
    if (mode === 3 && placingPolygonPoints) {
        rows.push(
            <Section key="polygonSnapping" title={title("Snapping", "Snapping")}>
                {button("PolygonSnapping", "Snapping", "Toggle lane, marking and median-corner snapping. Closing the polygon stays available when snapping is off.", polygonIcon, snapping, "SetPolygonSnapping", !snapping)}
            </Section>
        );
    }
    if (mode === 2) rows.push(
    <Section key="remove" title={title("Remove", "Remove")}>
      {button("All", "All", "Select all removal filters, or clear them when any are selected.", allIcon, filters === 7, "SetResetFilters", filters ? 0 : 7)}
      {button("Connection", "Connection", "Remove the complete connection, including the paired endpoint.", connectIcon, (filters & 1) !== 0, "SetResetFilters", filters ^ 1)}
      {button("EndCap", "End Cap", "Clear only this endpoint's cap override.", roundedIcon, (filters & 2) !== 0, "SetResetFilters", filters ^ 2)}
      {button("Surface", "Surface", "Restore the highlighted segment median's original prefab surface.", grassIcon, (filters & 4) !== 0, "SetResetFilters", filters ^ 4)}
    </Section>
  );
  return cloneElement(result, {}, ...Children.toArray(result.props.children),
    <style key="median-controller-style">{panelStyle}</style>,
    <div key="median-controller-options" className="median-controller-options">{rows}{(mode === 3 || (mode === 1 && !!selectedManual)) && <MedianManagementPanel />}</div>);
};
