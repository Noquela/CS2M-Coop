import {bindValue, useValue} from "cs2/api";
import mod from "../../mod.json";

// JSON pushed by the C# SyncStatusSystem ~1 Hz:
//   {"state":"off"|"host"|"synced"|"drift", "text":"..."}
// Turns our StateHash divergence detection into a user-facing trust badge (top-center).
const syncStatus$ = bindValue<string>(mod.id, "SyncStatus", '{"state":"off"}');

interface SyncStatus {
    state?: string;
    text?: string;
}

export const SyncBadge = () => {
    const raw = useValue(syncStatus$);

    let s: SyncStatus = {};
    try {
        s = JSON.parse(raw) as SyncStatus;
    } catch {
        s = {};
    }

    if (!s.state || s.state === "off") {
        return null;
    }

    const drift = s.state === "drift";
    const color = drift ? "#e5687a" : "#3fbfa3";

    return (
        <div style={{position: "absolute", left: "50%", top: "8rem", transform: "translateX(-50%)", pointerEvents: "none", zIndex: 100}}>
            <div
                style={{
                    display: "flex",
                    alignItems: "center",
                    gap: "6rem",
                    backgroundColor: "rgba(18,22,29,0.88)",
                    color: "#ffffff",
                    border: `2rem solid ${color}`,
                    borderRadius: "6rem",
                    padding: "4rem 12rem",
                    fontSize: "14rem",
                    fontWeight: "bold",
                    whiteSpace: "nowrap",
                }}
            >
                <div style={{width: "9rem", height: "9rem", borderRadius: "50%", backgroundColor: color}}/>
                {s.text}
            </div>
        </div>
    );
};
