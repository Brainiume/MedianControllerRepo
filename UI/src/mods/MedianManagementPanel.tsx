import { bindValue, trigger, useValue } from "cs2/api";
import { useLocalization } from "cs2/l10n";
import { useLayoutEffect, useRef, useState } from "react";
import { getModule } from "cs2/modding";
import mod from "../../mod.json";
import { VanillaComponentResolver } from "./VanillaComponentResolver";
import grass from "../icons/grass.svg";
import plain from "../icons/plain.svg";
import reset from "../icons/all.svg";
import connect from "../icons/connect.svg";
import resetCurb from "../icons/reset-curb.svg";
import resetShape from "../icons/reset-shape.svg";

const sectionStyle = getModule(
    "game-ui/game/components/tool-options/mouse-tool-options/mouse-tool-options.module.scss", "classes"
);
const buttonStyle = getModule(
    "game-ui/game/components/tool-options/tool-button/tool-button.module.scss", "classes"
);
const propertiesStyle = `
    .median-controller-properties { display: flex; flex-direction: column; font-family: Overpass; }
    .median-controller-properties > * { flex-shrink: 0; margin-top: 4rem; }
    .median-controller-properties > :first-child { margin-top: 0; }
    .median-controller-properties-title { font-size: 16rem; line-height: 21rem; }
    .median-controller-properties .${buttonStyle.button} { width: 28rem; height: 28rem; border-radius: 4rem; }
    .median-controller-properties .${sectionStyle.item} { min-height: 39rem; padding: 0; }
    .median-controller-properties .${sectionStyle.label} { flex: 0 0 132rem; width: 132rem; margin-right: 18rem; font-size: 16rem; font-weight: 700; line-height: 21rem; }
    .median-controller-properties .${sectionStyle.content} { justify-content: flex-start; margin-left: 0; }
    .median-controller-properties .${sectionStyle.content} > * { margin-left: 0; margin-right: 8rem; }
    .median-controller-properties .${sectionStyle.content} > :last-child { margin-right: 0; }
    .median-controller-properties-fields { display: flex; flex-direction: column; }
    .median-controller-properties-field { display: flex; flex-direction: column; height: 48rem; margin-top: 4rem; }
    .median-controller-properties-field:first-child { margin-top: 0; }
    .median-controller-properties-caption { display: flex; flex-direction: row; justify-content: space-between; height: 22rem; margin-bottom: 6rem; font-size: 14rem; line-height: 20rem; }
    .median-controller-properties-field > :last-child { height: 12rem; }
    .median-controller-properties-footer { display: flex; flex-direction: row; align-items: center; height: 38rem; margin-top: 4rem; }
    .median-controller-properties-footer > * { margin-right: 14rem; }
`;

const selected$ = bindValue<number>(mod.id, "SelectedManualMedianId");
const surface$ = bindValue<number>(mod.id, "SelectedManualMedianSurface");
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
const curbWidth$ = bindValue<number>(mod.id, "PolygonCurbWidth");

export const MedianManagementPanel = () => {
    const selected = useValue(selected$), surface = useValue(surface$);
    const editable = useValue(editable$), message = useValue(message$);
    const polygon = useValue(polygon$), opening = useValue(opening$);
    const values = [useValue(lineOffset$), useValue(medianOffset$), useValue(lineRadius$), useValue(medianRadius$)];
    const offsetLimit = useValue(offsetLimit$);
    const polygonExtend = useValue(polygonExtend$);
    const curbWidth = useValue(curbWidth$);
    const { translate } = useLocalization();
    const { Section, ToolButton, toolButtonTheme, FOCUS_DISABLED, Slider, sliderTheme } = VanillaComponentResolver.instance;
    const panel = useRef<HTMLElement>(null);
    const [position, setPosition] = useState({left: 0, bottom: 0});
    useLayoutEffect(() => {
        const place = () => {
            const options = panel.current?.closest(".median-controller-options")?.parentElement;
            if (!options) return;
            const bounds = options.getBoundingClientRect();
            setPosition({left: bounds.left, bottom: window.innerHeight - bounds.top + (polygon ? 0 : 12)});
        };
        place();
        window.addEventListener("resize", place);
        return () => window.removeEventListener("resize", place);
    }, [selected, !!message, polygon, opening]);
    if (!selected && !message) return null;
    const label = (key: string, fallback: string) => translate(`RoadGrass.MANUAL[${key}]`, fallback);
    const field = (index: number, name: string) => {
        const max = offsetLimit;
        const set = (value: number) => { if (Number.isFinite(value)) trigger(mod.id, "SetPolygonShape", index, Math.min(max, Math.max(0, Math.round(value * 10) / 10))); };
        return <div className="median-controller-properties-field" role="group" aria-label={name}
            onWheel={event => { event.preventDefault(); if (editable) set(values[index] + (event.deltaY < 0 ? .1 : -.1)); }}>
            <div className="median-controller-properties-caption">
                <span>{name}</span><span>{values[index].toFixed(1)} m</span>
            </div>
            <Slider start={0} end={max} value={values[index]} disabled={!editable} theme={sliderTheme} onChange={value => editable && set(value)} />
        </div>;
    };
    const action = (key: string, text: string, src: string, value: number) =>
        <ToolButton focusKey={FOCUS_DISABLED} className={toolButtonTheme.button} src={src}
            aria-label={label(key, text)} tooltip={label(key, text)} selected={surface === value}
            aria-disabled={!editable} style={{opacity: editable ? 1 : .5}}
            onSelect={() => editable && trigger(mod.id, "SetManualMedianSurface", value)} />;
    return <>
        <style>{propertiesStyle}</style>
        <aside ref={panel} className={polygon ? "median-controller-properties" : undefined} aria-label={polygon ? label("PolygonTitle", "Polygon Median") : label("Title", "Manual median")} style={{position: "fixed", left: position.left, bottom: position.bottom, zIndex: 10,
            marginBottom: "12rem", width: "300rem", padding: "12rem", borderRadius: "6rem",
            backgroundColor: polygon ? "#1c242b" : "rgba(28, 36, 43, 0.97)", boxShadow: "0 4rem 14rem rgba(0,0,0,0.4)"}}>
            <div className={polygon ? "median-controller-properties-title" : undefined} style={polygon ? undefined : {fontSize: "16rem", marginBottom: "10rem"}}>{polygon ? label("PolygonTitle", "Polygon Median") : label("Title", "Manual median")}{selected ? ` #${selected}` : ""}</div>
            {!!selected && <>
                <Section title={label("Surface", "Surface")}>
                    {action("Grass", "Grass", grass, 1)}
                    {action("Plain", "Plain", plain, 2)}
                    {action("ResetSurface", "Reset Surface", reset, 0)}
                </Section>
                <Section title={label("Remove", "Remove")}>
                    <ToolButton focusKey={FOCUS_DISABLED} className={toolButtonTheme.button} src="coui://uil/Standard/XClose.svg"
                        aria-label={label("Remove", "Remove")} tooltip={label("RemoveHint", "Remove only this manual median")}
                        aria-disabled={!editable} onSelect={() => editable && trigger(mod.id, "RemoveManualMedian")} />
                </Section>
            </>}
            {!!message && <div role="status" style={{fontSize: "13rem", marginTop: "8rem", whiteSpace: "normal"}}>{message}</div>}
        {polygon && <>
            <Section title={label("ExtendFromMedian", "Extend from median")}>
                <ToolButton focusKey={FOCUS_DISABLED} className={toolButtonTheme.button} src={connect} selected={polygonExtend}
                    aria-label={label("ExtendFromMedian", "Extend from median")}
                    tooltip={label("ExtendFromMedianHint", "Open the joining curb and remove the original median end cap where this polygon joins its two end corners")}
                    aria-disabled={!editable} onSelect={() => editable && trigger(mod.id, "SetPolygonExtendFromMedian", !polygonExtend)} />
            </Section>
            <div className="median-controller-properties-fields">
            {field(0, opening ? label("LineOffset", "Line offset") : label("Offset", "Offset"))}
            {opening && field(1, label("MedianOffset", "Median offset"))}
            {field(2, opening ? label("LineRadius", "Line corner radius") : label("Radius", "Corner radius"))}
            {opening && field(3, label("MedianRadius", "Median corner radius"))}
            <div className="median-controller-properties-field" role="group" aria-label={label("CurbWidth", "Curb width")}>
                <div className="median-controller-properties-caption">
                    <span>{label("CurbWidth", "Curb width")}</span><span>{curbWidth.toFixed(2)} m</span>
                </div>
                <Slider start={.03} end={1} value={curbWidth} disabled={!editable} theme={sliderTheme}
                    onChange={value => editable && Number.isFinite(value) && trigger(mod.id, "SetPolygonCurbWidth", Math.round(value * 100) / 100)} />
            </div>
            </div>
            <div className="median-controller-properties-footer">
                <ToolButton focusKey={FOCUS_DISABLED} className={toolButtonTheme.button} src={resetCurb}
                    aria-label={label("ResetCurb", "Reset curb width")} tooltip={label("ResetCurbHint", "Restore the original median prefab's curb width")}
                    aria-disabled={!editable} onSelect={() => editable && trigger(mod.id, "SetPolygonCurbWidth", 0)} />
            <ToolButton focusKey={FOCUS_DISABLED} className={toolButtonTheme.button} src={resetShape}
                aria-label={label("ResetShape", "Reset Shape")} tooltip={label("ResetShapeHint", "Reset offsets, corner radii and curb width; keep the selected surface")}
                aria-disabled={!editable} onSelect={() => editable && trigger(mod.id, "SetPolygonShape", -1, 0)} />
            </div>
        </>}
        </aside>
    </>;
};
