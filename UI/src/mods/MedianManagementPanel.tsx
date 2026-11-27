import { bindValue, trigger, useValue } from "cs2/api";
import { useLocalization } from "cs2/l10n";
import { useLayoutEffect, useRef, useState } from "react";
import mod from "../../mod.json";
import { VanillaComponentResolver } from "./VanillaComponentResolver";
import { MedianSurfaceDropdown } from "./MedianSurfaceDropdown";
import { MedianPropertySlider as Slider } from "./MedianPropertySlider";
import connect from "../icons/connect.svg";
import resetCurb from "../icons/reset-curb.svg";
import resetShape from "../icons/reset-shape.svg";
import editPolygon from "../icons/polygon.svg";

const selected$ = bindValue<number>(mod.id, "SelectedManualMedianId");
const editable$ = bindValue<boolean>(mod.id, "CanEditSelectedManualMedian");
const message$ = bindValue<string>(mod.id, "ManualMedianMessage");
const polygon$ = bindValue<boolean>(mod.id, "SelectedIsPolygon");
const opening$ = bindValue<boolean>(mod.id, "PolygonHasOpening");
const lineOffset$ = bindValue<number>(mod.id, "PolygonLineOffset");
const medianOffset$ = bindValue<number>(mod.id, "PolygonMedianOffset");
const lineRadius$ = bindValue<number>(mod.id, "PolygonLineRadius");
const medianRadius$ = bindValue<number>(mod.id, "PolygonMedianRadius");
const offsetLimit$ = bindValue<number>(mod.id, "PolygonOffsetLimit");
const polygonExtend$ = bindValue<boolean>(mod.id, "PolygonExtendFromMedian");
const crosswalkGaps$ = bindValue<boolean>(mod.id, "CrosswalkGaps");
const crosswalkGapsAvailable$ = bindValue<boolean>(mod.id, "CrosswalkGapsAvailable");
const curbWidth$ = bindValue<number>(mod.id, "PolygonCurbWidth");
const textureTiling$ = bindValue<number>(mod.id, "PolygonTextureTiling");
const pointEditing$ = bindValue<boolean>(mod.id, "PolygonPointEditing");
const texture$ = bindValue<number>(mod.id, "SelectedMedianTexture");

export const MedianManagementPanel = () => {
    const selected = useValue(selected$);
    const editable = useValue(editable$), message = useValue(message$);
    const polygon = useValue(polygon$), opening = useValue(opening$);
    const values = [useValue(lineOffset$), useValue(medianOffset$), useValue(lineRadius$), useValue(medianRadius$)];
    const offsetLimit = useValue(offsetLimit$);
    const polygonExtend = useValue(polygonExtend$);
    const crosswalkGaps = useValue(crosswalkGaps$);
    const crosswalkGapsAvailable = useValue(crosswalkGapsAvailable$);
    const curbWidth = useValue(curbWidth$);
    const textureTiling = useValue(textureTiling$);
    const pointEditing = useValue(pointEditing$);
    const texture = useValue(texture$);
    const { translate } = useLocalization();
    const { Section, ToolButton, toolButtonTheme, toolOptionsPanelTheme, FOCUS_DISABLED, Panel, Portal, PanelTitleBar, InfoSection, InfoSectionFoldout, InfoRow, Checkbox, Tooltip } = VanillaComponentResolver.instance;
    const anchor = useRef<HTMLSpanElement>(null);
    const [position, setPosition] = useState({left: 0, bottom: 0, width: 0});
    useLayoutEffect(() => {
        const options = anchor.current?.closest(`.${toolOptionsPanelTheme.toolOptionsPanel}`);
        if (!options) return;
        const place = () => {
            const bounds = options.getBoundingClientRect();
            setPosition({left: bounds.left, bottom: window.innerHeight - bounds.top, width: bounds.width});
        };
        place();
        const observer = typeof ResizeObserver === "function" ? new ResizeObserver(place) : undefined;
        observer?.observe(options);
        options.addEventListener("transitionend", place);
        window.addEventListener("resize", place);
        return () => { observer?.disconnect(); options.removeEventListener("transitionend", place); window.removeEventListener("resize", place); };
    }, [selected, !!message, polygon, opening]);
    if (!selected && !message) return null;
    const label = (key: string, fallback: string) => translate(`RoadGrass.MANUAL[${key}]`, fallback);
    const field = (index: number, name: string) => {
        const max = offsetLimit;
        const set = (value: number) => { if (Number.isFinite(value)) trigger(mod.id, "SetPolygonShape", index, Math.min(max, Math.max(0, Math.round(value * 10) / 10))); };
        return <InfoSection disableFocus>
            <InfoRow disableFocus left={name} right={`${values[index].toFixed(1)} m`} />
            <div style={{width: "86%", alignSelf: "center"}}>
                <Slider key={`${selected}-${index}`} start={0} end={max} value={values[index]} gamepadStep={.1} disabled={!editable} onChange={value => editable && set(value)} />
            </div>
        </InfoSection>;
    };
    return <><span ref={anchor} style={{display: "none"}} /><Portal><Panel allowFocusExit
        aria-label={polygon ? label("PolygonTitle", "Polygon Median") : label("Title", "Manual median")}
        header={<PanelTitleBar>{polygon ? label("PolygonTitle", "Polygon Median") : label("Title", "Manual median")}{selected ? ` #${selected}` : ""}</PanelTitleBar>}
        style={{position: "fixed", left: position.left, bottom: position.bottom, padding: 0, marginBottom: "12rem", width: position.width, visibility: position.width ? "visible" : "hidden"}}>
            {!!selected && <InfoSection disableFocus><>

                <Section title={label("Surface", "Surface")}>
                    <MedianSurfaceDropdown key={selected} focusKey={`median-controller-surface-${selected}`}
                        value={texture} disabled={!editable} onChange={value => editable && trigger(mod.id, "SetMedianTexture", value)} />
                </Section>
                <Section title={label("Remove", "Remove")}>
                    <ToolButton focusKey={FOCUS_DISABLED} className={toolButtonTheme.button} src="coui://uil/Standard/XClose.svg"
                        aria-label={label("Remove", "Remove")} tooltip={label("RemoveHint", "Remove only this manual median")}
                        disabled={!editable} onSelect={() => editable && trigger(mod.id, "RemoveManualMedian")} />
                </Section>
            </></InfoSection>}
            {!!message && <InfoSection disableFocus><div role="status">{message}</div></InfoSection>}
        {polygon && <>
            <InfoSection disableFocus>
                <InfoRow disableFocus left={label("TextureTiling", "Texture tiling")} right={`${textureTiling.toFixed(2)}x`} />
                <div style={{width: "86%", alignSelf: "center"}}>
                    <Slider key={`${selected}-tiling`} start={.25} end={4} value={textureTiling} disabled={!editable} gamepadStep={.05}
                        onChange={value => editable && Number.isFinite(value) && trigger(mod.id, "SetPolygonTextureTiling", Math.round(value * 20) / 20)} />
                </div>
            </InfoSection>
            <InfoSection disableFocus>
            <Section title={label("EditPoints", "Edit")}>
                <ToolButton focusKey={FOCUS_DISABLED} className={toolButtonTheme.button} src={editPolygon} selected={pointEditing}
                    aria-label={label("EditPoints", "Edit")}
                    tooltip={label("EditPointsHint", "Drag points to reshape the median, click an edge to add a point, or use the secondary action on a point to remove it")}
                    disabled={!editable} onSelect={() => editable && trigger(mod.id, "SetPolygonPointEditing", !pointEditing)} />
            </Section>
            <Section title={label("ExtendFromMedian", "Extend from median")}>
                <ToolButton focusKey={FOCUS_DISABLED} className={toolButtonTheme.button} src={connect} selected={polygonExtend}
                    aria-label={label("ExtendFromMedian", "Extend from median")}
                    tooltip={label("ExtendFromMedianHint", "Open the joining curb and remove the original median end cap where this polygon joins its two end corners")}
                    disabled={!editable} onSelect={() => editable && trigger(mod.id, "SetPolygonExtendFromMedian", !polygonExtend)} />
            </Section>
            <Section title={label("CrosswalkGaps", "Crosswalk gaps")}>
                <Tooltip tooltip={crosswalkGapsAvailable
                    ? label("CrosswalkGapsHint", "Leave detected crosswalks at street level by cutting gaps through this median")
                    : label("CrosswalkGapsLocked", "Temporarily disabled. Enable Crosswalk gaps in Developer settings.")}>
                    <Checkbox debugName={label("CrosswalkGaps", "Crosswalk gaps")} checked={crosswalkGaps} disabled={!editable || !crosswalkGapsAvailable}
                        onChange={value => editable && crosswalkGapsAvailable && trigger(mod.id, "SetCrosswalkGaps", value)} />
                </Tooltip>
            </Section>
            </InfoSection>
            <InfoSectionFoldout key={`more-settings-${selected}`} header={label("MoreSettings", "More Settings")} initialExpanded={false} disableFocus>
                {field(0, opening ? label("LineOffset", "Line offset") : label("Offset", "Offset"))}
                {opening && field(1, label("MedianOffset", "Median offset"))}
                {field(2, opening ? label("LineRadius", "Line corner radius") : label("Radius", "Corner radius"))}
                {opening && field(3, label("MedianRadius", "Median corner radius"))}
            </InfoSectionFoldout>
            <InfoSection disableFocus>
                <InfoRow disableFocus left={label("CurbWidth", "Curb width")} right={`${curbWidth.toFixed(2)} m`} />
                <div style={{width: "86%", alignSelf: "center"}}>
                    <Slider key={`${selected}-curb`} start={.03} end={1} value={curbWidth} disabled={!editable} gamepadStep={.01}
                        onChange={value => editable && Number.isFinite(value) && trigger(mod.id, "SetPolygonCurbWidth", Math.round(value * 100) / 100)} />
                </div>
            </InfoSection>
            <InfoSection disableFocus><Section title={label("ResetShape", "Reset Shape")}>
                <ToolButton focusKey={FOCUS_DISABLED} className={toolButtonTheme.button} src={resetCurb}
                    aria-label={label("ResetCurb", "Reset curb width")} tooltip={label("ResetCurbHint", "Restore the original median prefab's curb width")}
                    disabled={!editable} onSelect={() => editable && trigger(mod.id, "SetPolygonCurbWidth", 0)} />
            <ToolButton focusKey={FOCUS_DISABLED} className={toolButtonTheme.button} src={resetShape}
                aria-label={label("ResetShape", "Reset Shape")} tooltip={label("ResetShapeHint", "Reset offsets, corner radii and curb width; keep the selected surface")}
                disabled={!editable} onSelect={() => editable && trigger(mod.id, "SetPolygonShape", -1, 0)} />
            </Section></InfoSection>
        </>}
    </Panel></Portal></>;
};
