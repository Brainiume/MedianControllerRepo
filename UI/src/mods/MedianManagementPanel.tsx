import { bindValue, trigger, useValue } from "cs2/api";
import { useLocalization } from "cs2/l10n";
import { useLayoutEffect, useRef, useState } from "react";
import mod from "../../mod.json";
import { VanillaComponentResolver } from "./VanillaComponentResolver";
import grass from "../icons/grass.svg";
import plain from "../icons/plain.svg";
import reset from "../icons/all.svg";

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
const snapping$ = bindValue<boolean>(mod.id, "PolygonSnapping");

export const MedianManagementPanel = () => {
    const selected = useValue(selected$), surface = useValue(surface$);
    const editable = useValue(editable$), message = useValue(message$);
    const polygon = useValue(polygon$), opening = useValue(opening$);
    const values = [useValue(lineOffset$), useValue(medianOffset$), useValue(lineRadius$), useValue(medianRadius$)];
    const offsetLimit = useValue(offsetLimit$);
    const snapping = useValue(snapping$);
    const { translate } = useLocalization();
    const { Section, ToolButton, toolButtonTheme, FOCUS_DISABLED, Slider, sliderTheme } = VanillaComponentResolver.instance;
    const panel = useRef<HTMLElement>(null);
    const [position, setPosition] = useState({left: 0, bottom: 0});
    useLayoutEffect(() => {
        const place = () => {
            const options = panel.current?.closest(".median-controller-options")?.parentElement;
            if (!options) return;
            const bounds = options.getBoundingClientRect();
            setPosition({left: bounds.left, bottom: window.innerHeight - bounds.top + 12});
        };
        place();
        window.addEventListener("resize", place);
        return () => window.removeEventListener("resize", place);
    }, [selected, !!message, polygon, opening]);
    if (!selected && !message) return null;
    const label = (key: string, fallback: string) => translate(`RoadGrass.MANUAL[${key}]`, fallback);
    const field = (index: number, name: string) => {
        const max = index < 2 ? offsetLimit : 10;
        const set = (value: number) => { if (Number.isFinite(value)) trigger(mod.id, "SetPolygonShape", index, Math.min(max, Math.max(0, Math.round(value * 10) / 10))); };
        return <div role="group" aria-label={name} style={{marginTop: "10rem"}}
            onWheel={event => { event.preventDefault(); if (editable) set(values[index] + (event.deltaY < 0 ? .1 : -.1)); }}>
            <div style={{display: "flex", justifyContent: "space-between", marginBottom: "6rem"}}>
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
        <aside ref={panel} aria-label={label("Title", "Manual median")} style={{position: "fixed", left: position.left, bottom: position.bottom, zIndex: 10,
            marginBottom: "12rem", width: "300rem", padding: "12rem", borderRadius: "6rem",
            backgroundColor: "rgba(28, 36, 43, 0.97)", boxShadow: "0 4rem 14rem rgba(0,0,0,0.4)"}}>
            <div style={{fontSize: "16rem", marginBottom: "10rem"}}>{label("Title", "Manual median")}{selected ? ` #${selected}` : ""}</div>
            {!!selected && <>
                <Section title={label("Snapping", "Snapping")}>
                    <ToolButton focusKey={FOCUS_DISABLED} className={toolButtonTheme.button} src={reset} selected={snapping}
                        aria-label={label("Snapping", "Snapping")} tooltip={label("SnappingHint", "Turn snapping on or off when drawing polygon points")}
                        onSelect={() => trigger(mod.id, "SetPolygonSnapping", !snapping)} />
                    <span>{snapping ? label("On", "On") : label("Off", "Off")}</span>
                </Section>
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
        {polygon && <div>
            {field(0, opening ? label("LineOffset", "Line offset") : label("Offset", "Offset"))}
            {opening && field(1, label("MedianOffset", "Median offset"))}
            {field(2, opening ? label("LineRadius", "Line corner radius") : label("Radius", "Corner radius"))}
            {opening && field(3, label("MedianRadius", "Median corner radius"))}
            <ToolButton focusKey={FOCUS_DISABLED} className={toolButtonTheme.button} src={reset}
                aria-label={label("ResetShape", "Reset Shape")} tooltip={label("ResetShapeHint", "Reset offsets and corner radii; keep the selected surface")}
                aria-disabled={!editable} onSelect={() => editable && trigger(mod.id, "SetPolygonShape", -1, 0)} />
        </div>}
        </aside>
    </>;
};
