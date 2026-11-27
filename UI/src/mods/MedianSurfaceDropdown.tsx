import { bindValue, useValue } from "cs2/api";
import { useLocalization } from "cs2/l10n";
import { useState } from "react";
import mod from "../../mod.json";
import { VanillaComponentResolver } from "./VanillaComponentResolver";
import grass from "../icons/grass.svg";
import plain from "../icons/plain.svg";
import gridIcon from "../icons/grid.svg";

type SurfaceOption = { id: number; name: string; nameKey: string; icon: string; available: boolean; };
const options$ = bindValue<SurfaceOption[]>(mod.id, "PolygonSurfaceOptions");

// Shared across the polygon and road Edit selectors for the current UI session.
let preferredGridView = false;
const surfaceStyles = `
.median-surface-menu { width: 342rem; max-width: 90vw; }
.median-surface-shell { --contentPadding: 0rem; }
.median-surface-browser { display: flex; flex-direction: column; max-height: 43.2vh; min-height: 0; }
.median-surface-scroll { flex: 1 1 auto; min-height: 0; --contentPadding: 5.4rem; }
.median-surface-list { display: flex; flex-direction: column; }
.median-surface-grid { display: flex; flex-direction: row; flex-wrap: wrap; align-content: flex-start; }
.median-surface-item { display: flex; flex-direction: row; align-items: center; width: 100%; height: 50.4rem; padding: 7.2rem 10.8rem; font-size: 16.2rem; line-height: 19.8rem; text-align: left; }
.median-surface-preview { width: 36rem; height: 36rem; border-radius: 50%; flex-shrink: 0; }
.median-surface-label { flex: 1 1 auto; min-width: 0; margin-left: 10.8rem; overflow: hidden; white-space: nowrap; text-overflow: ellipsis; }
.median-surface-tile { flex-direction: column; justify-content: flex-start; width: 50%; height: 118.8rem; padding: 7.2rem; text-align: center; }
.median-surface-tile-preview { width: 57.6rem; height: 57.6rem; }
.median-surface-tile-label { flex: 0 1 auto; width: 100%; height: 39.6rem; margin-left: 0; margin-top: 7.2rem; justify-content: center; white-space: normal; }
.median-surface-footer { display: flex; flex-direction: row; align-items: center; flex-shrink: 0; height: 43.2rem; padding: 5.4rem 10.8rem; border-top: 1rem solid var(--dividerColor); }
.median-surface-view-button { transform: scale(.9); transform-origin: left center; }
.median-surface-toggle { display: flex; flex-direction: row; align-items: center; min-height: 39.6rem; max-width: 237.6rem; padding: 5.4rem 9rem; font-size: 16.2rem; line-height: 19.8rem; }
.median-surface-toggle-preview { width: 28.8rem; height: 28.8rem; }
.median-surface-indicator { width: 12.6rem; height: 12.6rem; margin-left: 7.2rem; }
.median-surface-current { display: flex; flex-direction: row; align-items: center; width: 100%; min-width: 0; }
`;

export const MedianSurfaceDropdown = ({ value, disabled = false, focusKey, onChange }: {
    value: number; disabled?: boolean; focusKey: string; onChange: (value: number) => void;
}) => {
    const supplied = useValue(options$);
    const [gridView, setGridView] = useState(preferredGridView);
    const { translate } = useLocalization();
    const { Dropdown, DropdownToggle, DropdownItem, dropdownTheme, Scrollable, ToolButton, toolButtonTheme, locStrValue } = VanillaComponentResolver.instance;
    const options = supplied?.length ? supplied.filter(option => option.id !== 0) : [
        { id: 1, name: "Grass", nameKey: "RoadGrass.MANUAL[Grass]", icon: grass, available: true },
        { id: 2, name: "Pavement", nameKey: "RoadGrass.MANUAL[Plain]", icon: plain, available: true }
    ];
    const current = options.find(option => option.id === value) || options[0];
    const label = (option: SurfaceOption) => translate(option.nameKey, option.name);
    const icon = (option: SurfaceOption) => option.icon || (option.id === 2 ? plain : grass);
    const viewLabel = translate(`RoadGrass.MANUAL[${gridView ? "SurfaceListView" : "SurfaceGridView"}]`, gridView ? "List view" : "Grid view");
    const theme = { ...dropdownTheme,
        dropdownMenu: `${dropdownTheme.dropdownMenu} median-surface-menu`,
        indicator: `${dropdownTheme.indicator || ""} median-surface-indicator`,
        scrollable: `${dropdownTheme.scrollable || ""} median-surface-shell`
    };
    const rows = gridView ? Math.ceil(options.length / 2) : options.length;
    return <>
        <style>{surfaceStyles}</style>
        <Dropdown theme={theme} focusKey={focusKey} initialFocused={current.id}
            content={<div className="median-surface-browser" style={{height: `${Math.min(rows, gridView ? 3 : 6) * (gridView ? 118.8 : 50.4) + 54}rem`}}>
                <Scrollable className="median-surface-scroll" trackVisibility="scrollable">
                    <div className={gridView ? "median-surface-grid" : "median-surface-list"}>
                        {options.map(option => <DropdownItem key={option.id} value={option.id}
                            className={`median-surface-item${gridView ? " median-surface-tile" : ""}`}
                            tooltip={locStrValue(label(option))} selected={current.id === option.id}
                            disabled={disabled || !option.available} onChange={onChange}>
                            <img className={`median-surface-preview${gridView ? " median-surface-tile-preview" : ""}`} src={icon(option)} alt="" />
                            <span className={`median-surface-label${gridView ? " median-surface-tile-label" : ""}`}>{label(option)}</span>
                        </DropdownItem>)}
                    </div>
                </Scrollable>
                <div className="median-surface-footer">
                    <ToolButton focusKey={`${focusKey}-grid-view`} className={`${toolButtonTheme.button} median-surface-view-button`} src={gridIcon}
                        selected={gridView} aria-label={viewLabel} tooltip={viewLabel}
                        onSelect={() => { preferredGridView = !gridView; setGridView(preferredGridView); }} />
                </div>
            </div>}>
            <DropdownToggle className="median-surface-toggle" disabled={disabled}
                aria-label={`${translate("RoadGrass.MANUAL[Surface]", "Surface")}: ${label(current)}`}>
                <div className="median-surface-current">
                    <img className="median-surface-preview median-surface-toggle-preview" src={icon(current)} alt="" />
                    <span className="median-surface-label">{label(current)}</span>
                </div>
            </DropdownToggle>
        </Dropdown>
    </>;
};
