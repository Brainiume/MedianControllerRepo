import { useEffect, useRef, useState } from "react";
import { VanillaComponentResolver } from "./VanillaComponentResolver";

export const MedianPropertySlider = (props: {
    start: number; end: number; value: number; disabled?: boolean; gamepadStep: number; onChange: (value: number) => void;
}) => {
    const { Slider } = VanillaComponentResolver.instance;
    const [value, setValue] = useState(props.value);
    const pending = useRef<number | null>(null), timer = useRef<ReturnType<typeof setTimeout> | null>(null);
    const commit = useRef(props.onChange); commit.current = props.onChange;
    const flush = () => {
        if (timer.current !== null) clearTimeout(timer.current);
        timer.current = null;
        if (pending.current !== null) { const next = pending.current; pending.current = null; commit.current(next); }
    };
    useEffect(() => { if (pending.current === null) setValue(props.value); }, [props.value]);
    useEffect(() => () => { if (timer.current !== null) clearTimeout(timer.current); }, []);
    return <div style={{width: "100%"}} onMouseUpCapture={flush} onKeyUpCapture={flush}>
        <Slider {...props} value={value} onChange={next => {
            setValue(next); pending.current = next;
            if (timer.current !== null) clearTimeout(timer.current);
            // Gamepad changes also commit after input settles; geometry never rebuilds for every drag sample.
            timer.current = setTimeout(flush, 180);
        }} />
    </div>;
};
