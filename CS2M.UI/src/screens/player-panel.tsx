import {bindValue, useValue} from "cs2/api";
import mod from "../../mod.json";

// JSON pushed by the C# PlayerPanelSystem ~1 Hz:
//   [{"n":"name (host)","p":-1|ms,"c":"#hex"}]  (p<0 = self, hide the ping)
const playerPanel$ = bindValue<string>(mod.id, "PlayerPanel", "[]");

interface Player {
    n: string;
    p: number;
    c: string;
}

export const PlayerPanel = () => {
    const raw = useValue(playerPanel$);

    let players: Player[] = [];
    try {
        players = JSON.parse(raw) as Player[];
    } catch {
        players = [];
    }

    if (!players || players.length === 0) {
        return null;
    }

    return (
        <div style={{position: "absolute", left: "12rem", top: "70rem", pointerEvents: "none", zIndex: 100, display: "flex", flexDirection: "column", gap: "3rem"}}>
            {players.map((pl, i) => (
                <div
                    key={i}
                    style={{
                        display: "flex",
                        alignItems: "center",
                        gap: "6rem",
                        backgroundColor: "rgba(18,22,29,0.82)",
                        borderRadius: "5rem",
                        padding: "3rem 9rem",
                    }}
                >
                    <div style={{width: "9rem", height: "9rem", borderRadius: "50%", backgroundColor: pl.c, flexShrink: 0}}/>
                    <span style={{color: "#ffffff", fontWeight: "bold", fontSize: "13rem", whiteSpace: "nowrap"}}>{pl.n}</span>
                    {pl.p >= 0 && <span style={{color: "#8a91a0", fontSize: "12rem", marginLeft: "4rem"}}>{pl.p}ms</span>}
                </div>
            ))}
        </div>
    );
};
