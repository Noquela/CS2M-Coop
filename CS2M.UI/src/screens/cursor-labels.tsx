import {bindValue, useValue} from "cs2/api";
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

export const CursorLabels = () => {
    const json = useValue(cursorLabels$);

    let labels: CursorLabel[] = [];
    try {
        labels = JSON.parse(json) as CursorLabel[];
    } catch {
        labels = [];
    }

    if (!labels || labels.length === 0) {
        return null;
    }

    return (
        <div style={{position: "fixed", left: "0", top: "0", right: "0", bottom: "0", pointerEvents: "none", zIndex: "1000"}}>
            {labels.map((l, i) => (
                <div
                    key={i}
                    style={{
                        position: "absolute",
                        left: `${(l.x * 100).toFixed(3)}%`,
                        top: `${((1 - l.y) * 100).toFixed(3)}%`,
                        transform: "translate(-50%, -160%)",
                        background: l.c,
                        color: "#ffffff",
                        padding: "0.25vh 0.7vh",
                        borderRadius: "0.5vh",
                        fontSize: "1.35vh",
                        fontWeight: "600",
                        lineHeight: "1.4vh",
                        whiteSpace: "nowrap",
                        textShadow: "0 0.1vh 0.2vh rgba(0,0,0,0.8)",
                        boxShadow: "0 0.2vh 0.6vh rgba(0,0,0,0.5)",
                        pointerEvents: "none",
                    }}
                >
                    {l.n}
                </div>
            ))}
        </div>
    );
};
