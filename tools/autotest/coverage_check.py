"""Verificador de completude do game-map (anti-gap).

Re-enumera do DECOMP os 3 conjuntos fechados (ToolSystems, tipos ISerializable,
TriggerBindings de UI) e confere que TODO item está coberto pelos outputs dos
agentes em docs/game-map/. Qualquer órfão = FAIL com nome na cara.

A completude não depende de ninguém lembrar de nada: se o jogo ganhar uma tool
nova num update, este script quebra sozinho.

Uso: python tools/autotest/coverage_check.py
"""
import json
import re
import sys
from pathlib import Path

sys.stdout.reconfigure(encoding="utf-8")

REPO = Path(__file__).resolve().parents[2]
DECOMP = REPO / "decomp" / "Game" / "Game"
GAMEMAP = REPO / "docs" / "game-map"

# tool concreta -> dossiê responsável (tool nova no jogo => KeyError proposital)
TOOL_TO_DOSSIER = {
    "AreaToolSystem": "area",
    "BulldozeToolSystem": "bulldoze",
    "DefaultToolSystem": "default-selection",
    "NetToolSystem": "net",
    "ObjectToolSystem": "object",
    "RouteToolSystem": "route",
    "SelectionToolSystem": "default-selection",
    "TerrainToolSystem": "terrain",
    "UpgradeToolSystem": "upgrade",
    "WaterToolSystem": "water",
    "ZoneToolSystem": "zone",
}
DOSSIER_KEYS = sorted(set(TOOL_TO_DOSSIER.values()) | {"ui-economy", "ui-city", "ui-sweep"})
MIN_CITATIONS = 10  # tripwire anti-placeholder: dossiê real cita muito mais que isso

fails = []
warns = []


def enumerate_tools():
    """Toda classe concreta que herda ToolBaseSystem/ObjectToolBaseSystem."""
    tools = set()
    for f in (DECOMP / "Tools").glob("*.cs"):
        m = re.search(r"public (abstract )?class (\w+) : (ToolBaseSystem|ObjectToolBaseSystem)\b",
                      f.read_text(encoding="utf-8", errors="replace"))
        if m and not m.group(1):  # ignora abstratas
            tools.add(m.group(2))
    tools.discard("ToolSystem")  # o gerente, não uma tool
    return tools


def enumerate_serialized():
    """Todo tipo (struct/class) que IMPLEMENTA ISerializable (base-list, não constraint
    genérica `where T : ISerializable`), nome qualificado pelo namespace do arquivo."""
    types = set()
    for f in DECOMP.rglob("*.cs"):
        text = f.read_text(encoding="utf-8", errors="replace")
        if "ISerializable" not in text:
            continue
        ns_m = re.search(r"^namespace ([\w.]+)", text, re.M)
        ns = ns_m.group(1) if ns_m else "?"
        for m in re.finditer(r"(?:struct|class) (\w+)(?:<[^>]*>)?\s*:\s*([^{;]*?)\{", text, re.S):
            bases = m.group(2).split(" where ")[0]
            if re.search(r"\bISerializable\b", bases):
                types.add(f"{ns}.{m.group(1)}")
    return types


def _norm(name):
    """+ aninhado vira ., genéricos <T> caem."""
    return re.sub(r"<[^>]*>", "", name).replace("+", ".")


def covers(name, pool):
    """name está coberto por algum item do pool? Exato, ou mesmo nome-base com um
    caminho de namespace prefixo do outro (lida com Parent+Nested e sub-namespace),
    o que NÃO casa Net.BuildOrder com Zones.BuildOrder (nenhum é prefixo do outro)."""
    if name in pool:
        return True
    parts = name.split(".")
    bare, ns = parts[-1], parts[:-1]
    for c in pool:
        cp = c.split(".")
        if cp[-1] != bare:
            continue
        cns = cp[:-1]
        short, long_ = (cns, ns) if len(cns) <= len(ns) else (ns, cns)
        if long_[: len(short)] == short:
            return True
    return False


def enumerate_triggers():
    """Todo TriggerBinding registrado na UI: (grupo, nome)."""
    trigs = set()
    for f in (DECOMP / "UI").rglob("*.cs"):
        text = f.read_text(encoding="utf-8", errors="replace")
        for m in re.finditer(r'new TriggerBinding[^(]*\(\s*"([^"]+)"\s*,\s*"([^"]+)"', text):
            trigs.add((m.group(1), m.group(2)))
    return trigs


# ---- 1. tools -> dossiês -------------------------------------------------
tools = enumerate_tools()
for t in sorted(tools):
    if t not in TOOL_TO_DOSSIER:
        fails.append(f"TOOL SEM DOSSIÊ MAPEADO: {t} (jogo atualizou? mapear em TOOL_TO_DOSSIER)")

for key in DOSSIER_KEYS:
    p = GAMEMAP / "dossiers" / f"{key}.md"
    if key == "ui-sweep":
        p = GAMEMAP / "ui-triggers.md"
    if not p.exists():
        fails.append(f"DOSSIÊ FALTANDO: {p.relative_to(REPO)}")
        continue
    text = p.read_text(encoding="utf-8", errors="replace")
    cites = len(re.findall(r"\.cs(?::|\s*linha\s*)\d+", text))
    if cites < MIN_CITATIONS:
        fails.append(f"DOSSIÊ SUSPEITO (só {cites} citações arquivo:linha): {p.name}")

# ---- 2. tipos serializados -> classificação ------------------------------
serialized = enumerate_serialized()
classified = {}
gaps_authored = []
state_dir = GAMEMAP / "state"
if not state_dir.exists():
    fails.append("PASTA docs/game-map/state/ NÃO EXISTE (classificadores não rodaram)")
else:
    for jf in sorted(state_dir.glob("*.json")):
        try:
            rows = json.loads(jf.read_text(encoding="utf-8"))
        except json.JSONDecodeError as e:
            fails.append(f"JSON INVÁLIDO: {jf.name}: {e}")
            continue
        for row in rows:
            classified[row["type"]] = row
            if row.get("class") == "AUTHORED" and str(row.get("syncPath", "NONE")).upper() in ("NONE", ""):
                gaps_authored.append(f'{row["type"]} — {row.get("note", "")}')

    classified_norm = {_norm(t) for t in classified}
    for t in sorted(serialized):
        if not covers(t, classified_norm):
            fails.append(f"TIPO SERIALIZADO SEM CLASSIFICAÇÃO: {t}")
    for t in sorted(classified):
        if not covers(_norm(t), serialized):
            warns.append(f"classificado mas não achei no decomp (nome divergente?): {t}")
    bad_class = [t for t, r in classified.items()
                 if r.get("class") not in ("AUTHORED", "DERIVED", "EMERGENT", "STATIC", "META")]
    for t in bad_class:
        fails.append(f"CLASSE INVÁLIDA em {t}: {classified[t].get('class')}")

# ---- 3. triggers de UI -> tabela do sweep ---------------------------------
triggers = enumerate_triggers()
sweep = GAMEMAP / "ui-triggers.md"
if sweep.exists():
    sweep_text = sweep.read_text(encoding="utf-8", errors="replace")
    for group, name in sorted(triggers):
        if name not in sweep_text:
            fails.append(f"TRIGGER FORA DA TABELA ui-triggers.md: {group}/{name}")
else:
    fails.append("ui-triggers.md NÃO EXISTE (sweep não rodou)")

# ---- relatório -------------------------------------------------------------
print(f"conjuntos enumerados do decomp: {len(tools)} tools | "
      f"{len(serialized)} tipos serializados | {len(triggers)} triggers de UI")
print(f"classificados: {len(classified)} | dossiês esperados: {len(DOSSIER_KEYS)}")
if gaps_authored:
    print(f"\n== GAPS (AUTHORED sem syncPath) — o trabalho a fazer, não erro do mapa ==")
    for g in gaps_authored:
        print(f"  GAP {g}")
for w in warns:
    print(f"  AVISO {w}")
if fails:
    print(f"\n== FALHAS DE COMPLETUDE ({len(fails)}) ==")
    for f_ in fails:
        print(f"  FAIL {f_}")
    sys.exit(1)
print("\nCOMPLETUDE OK — todo item dos 3 conjuntos fechados tem dono.")
