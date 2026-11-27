import { bindValue, trigger, useValue } from "cs2/api";
import { useLocalization } from "cs2/l10n";
import { getModule, ModuleRegistryExtend } from "cs2/modding";
import { Children, cloneElement } from "react";
import mod from "../../mod.json";
import { VanillaComponentResolver } from "./VanillaComponentResolver";
import { MedianManagementPanel } from "./MedianManagementPanel";
import { MedianSurfaceDropdown } from "./MedianSurfaceDropdown";
import connectIcon from "../icons/connect.svg";
import roundedIcon from "../icons/rounded.svg";
import straightIcon from "../icons/straight.svg";
import polygonIcon from "../icons/polygon.svg";
import grassIcon from "../icons/grass.svg";
import allIcon from "../icons/all.svg";

const mode$ = bindValue<number>(mod.id, "ActiveMode");
const cap$ = bindValue<number>(mod.id, "EditCapChoice");
const surface$ = bindValue<number>(mod.id, "EditSurfaceChoice");
const textureTiling$ = bindValue<number>(mod.id, "EditTextureTiling");
const filters$ = bindValue<number>(mod.id, "ResetFilters");
const active$ = bindValue<boolean>(mod.id, "IsActive");
const selectedManual$ = bindValue<number>(mod.id, "SelectedManualMedianId");
const selectedPolygon$ = bindValue<boolean>(mod.id, "SelectedIsPolygon");
const snapping$ = bindValue<boolean>(mod.id, "PolygonSnapping");
const placingPolygonPoints$ = bindValue<boolean>(mod.id, "PlacingPolygonPoints");
const polygonExtend$ = bindValue<boolean>(mod.id, "PolygonExtendFromMedian");
const crosswalkGaps$ = bindValue<boolean>(mod.id, "CrosswalkGaps");
const crosswalkGapsAvailable$ = bindValue<boolean>(mod.id, "CrosswalkGapsAvailable");
const tooltipStyle = getModule(
  "game-ui/common/tooltip/description-tooltip/description-tooltip.module.scss", "classes"
);
export const RoadGrassToolOptions: ModuleRegistryExtend = (Component: any) => () => {
  const active = useValue(active$);
  const mode = useValue(mode$);
  const selectedManual = useValue(selectedManual$);
  const selectedPolygon = useValue(selectedPolygon$);
  const snapping = useValue(snapping$);
  const placingPolygonPoints = useValue(placingPolygonPoints$);
  const polygonExtend = useValue(polygonExtend$);
  const crosswalkGaps = useValue(crosswalkGaps$);
  const crosswalkGapsAvailable = useValue(crosswalkGapsAvailable$);
  const cap = useValue(cap$);
  const surface = useValue(surface$);
  const textureTiling = useValue(textureTiling$);
  const filters = useValue(filters$);
  const { translate } = useLocalization();
  const result: JSX.Element = Component();
  if (!active) return result;
  const { Section, ToolButton, toolButtonTheme, FOCUS_DISABLED, Checkbox, Tooltip, Slider, InfoRow } = VanillaComponentResolver.instance;
  const title = (key: string, fallback: string) => translate(`RoadGrass.SECTION_TITLE[${key}]`, fallback);
  const button = (key: string, label: string, description: string, src: string,
    selected: boolean, action: string, value: number | boolean, disabled = false) => (
    <ToolButton key={key} src={src} selected={selected} disabled={disabled} focusKey={FOCUS_DISABLED}
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
      <MedianSurfaceDropdown focusKey="median-controller-edit-surface" value={surface < 2 ? surface + 1 : surface}
        onChange={value => trigger(mod.id, "SetEditSurfaceChoice", value <= 2 ? value - 1 : value)} />
    </Section>
  );
  if (mode === 1 && !selectedManual && surface >= 11) rows.push(
    <div key="textureTiling" style={{padding: "6rem 0"}}>
      <InfoRow disableFocus left={translate("RoadGrass.MANUAL[TextureTiling]", "Texture tiling")} right={`${textureTiling.toFixed(2)}x`} />
      <div style={{width: "86%", alignSelf: "center"}}>
        <Slider start={.25} end={4} value={textureTiling} gamepadStep={.05}
          onChange={value => Number.isFinite(value) && trigger(mod.id, "SetEditTextureTiling", Math.round(value * 20) / 20)} />
      </div>
    </div>
  );
    if (mode === 3 && !(selectedManual && selectedPolygon)) {
        rows.push(
            <Section key="polygonExtend" title={title("PolygonExtendFromMedian", "Extend from median")}>
                {button("PolygonExtendFromMedian", "Extend from median", "Open the joining curb and remove the original median end cap where the polygon joins its two end corners. Turn off to keep the polygon closed.", connectIcon, polygonExtend, "SetPolygonExtendFromMedian", !polygonExtend)}
            </Section>
        );
    }
    if ((mode === 0 || mode === 1 || mode === 3) && !(selectedManual && selectedPolygon)) {
        rows.push(<Section key="crosswalkGaps" title={title("CrosswalkGaps", "Crosswalk gaps")}>
            <Tooltip tooltip={crosswalkGapsAvailable
                ? translate("RoadGrass.MANUAL[CrosswalkGapsHint]", "Leave detected crosswalks at street level by cutting gaps through this median")
                : translate("RoadGrass.MANUAL[CrosswalkGapsLocked]", "Temporarily disabled. Enable Crosswalk gaps in Developer settings.")}>
                <Checkbox debugName={title("CrosswalkGaps", "Crosswalk gaps")} checked={crosswalkGaps} disabled={!crosswalkGapsAvailable}
                    onChange={value => crosswalkGapsAvailable && trigger(mod.id, "SetCrosswalkGaps", value)} />
            </Tooltip>
        </Section>);
    }
    if (mode === 3 && placingPolygonPoints) {
        rows.push(
            <Section key="polygonSnapping" title={title("Snapping", "Snapping")}>
                {button("PolygonSnapping", "Contour snapping", "Follow connected lane and median edges with red points. Grey points connect straight. Disable for free placement.", polygonIcon, snapping, "SetPolygonSnapping", !snapping)}
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
    <div key="median-controller-options" className="median-controller-options">{rows}{(mode === 3 || (mode === 1 && !!selectedManual)) && <MedianManagementPanel />}</div>);
};
