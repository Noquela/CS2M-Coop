import {bindValue, trigger, useValue} from "cs2/api";
import {useEffect, useRef} from "react";
import mod from "../../mod.json";

// JSON pushed by the C# PlayerCursorSystem every frame:
//   [{ "x": 0..1, "y": 0..1, "n": "name", "c": "#hex" }]
// x/y are normalized screen coords (origin bottom-left), so we flip y for CSS.
const cursorLabels$ = bindValue<string>(mod.id, "CursorLabels", "[]");

interface CursorLabel {
    x: number;
    y: number;
    n: string;
    c: string;
}

// cohtml (the game's UI engine) does NOT honor position:fixed stretched by offsets, and the
// GameBottomRight slot lives inside a non-positioned 46rem strip — both together collapsed the
// old label layer invisibly. This version mounts in the fullscreen 'Game' slot and uses the
// vanilla overlay pattern: absolute + width/height 100%. Units are rem (CS2 design px), not vh.
let ackCounter = 0;

export const CursorLabels = () => {
    const json = useValue(cursorLabels$);
    const firstRef = useRef<HTMLDivElement | null>(null);

    let labels: CursorLabel[] = [];
    try {
        labels = JSON.parse(json) as CursorLabel[];
    } catch {
        labels = [];
    }

    // Render-ack: report the first label's real layouted rect back to C# (throttled) so the log
    // can prove the UI engine actually drew it (w/h > 0) — validated in the selftest.
    useEffect(() => {
        if (labels.length > 0 && firstRef.current && (ackCounter++ % 60) === 0) {
            const r = firstRef.current.getBoundingClientRect();
            trigger(mod.id, "CursorLabelsRendered",
                JSON.stringify({n: labels.length, x: r.left, y: r.top, w: r.width, h: r.height}));
        }
    }, [json]);

    if (!labels || labels.length === 0) {
        return null;
    }

    return (
        <div style={{position: "absolute", left: "0", top: "0", width: "100%", height: "100%", pointerEvents: "none", zIndex: 100}}>
            {labels.map((l, i) => (
                <div
                    key={i}
                    ref={i === 0 ? firstRef : undefined}
                    style={{
                        position: "absolute",
                        left: `${(l.x * 100).toFixed(3)}%`,
                        top: `${((1 - l.y) * 100).toFixed(3)}%`,
                        transform: "translate(-50%, -160%)",
                        backgroundColor: l.c,
                        color: "#ffffff",
                        border: "1rem solid rgba(0,0,0,0.6)",
                        borderRadius: "4rem",
                        padding: "2rem 8rem",
                        fontSize: "14rem",
                        fontWeight: "bold",
                        whiteSpace: "nowrap",
                        pointerEvents: "none",
                    }}
                >
                    {l.n}
                </div>
            ))}
        </div>
    );
};
