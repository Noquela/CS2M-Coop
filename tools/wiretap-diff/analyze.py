#!/usr/bin/env python3
"""
CS2M wiretap diff — localiza desyncs comparando as gravacoes (.jsonl) dos jogadores.

Cada jogador roda com CS2M_WIRETAP=1 e gera um CS2M_wiretap_<data>.jsonl na pasta LocalLow.
Este script cruza 2+ desses arquivos e aponta os comandos de ACAO que uns jogadores viram e
outros nao — que e' exatamente onde o mundo divergiu (o comando perdido).

Uso:
    python analyze.py host.jsonl clientA.jsonl clientB.jsonl
    python analyze.py *.jsonl --type Net      # foca em comandos de rede
    python analyze.py *.jsonl --all           # inclui os periodicos (cursor, speed, stats...)

Limitacao conhecida: o wiretap v52 nao dumpa o conteudo de arrays (aparecem como "System.Int32[]"),
entao comandos distinguidos SO por array (ex.: varias pinturas no MESMO bloco que diferem so nas
celulas) colapsam numa assinatura. Ainda pega "o comando daquele bloco/rota se perdeu", que e' o
caso comum. Precisao por-celula exigiria dump de array no wiretap (mudanca no mod).
"""
import sys
import json
import argparse
from collections import Counter

# Tipos periodicos/idempotentes que inundam o log e nao sao bug por-acao — filtrados por padrao.
NOISY = {
    "PlayerStatsCommand", "MapPingCommand", "PlayerCursorCommand", "SpeedCommand",
    "StateHashCommand", "DemandSyncCommand", "EnvSyncCommand",
}
# Chaves de metadado do wiretap (nao fazem parte do payload do comando).
META = {"seq", "t", "dir", "sender", "peer", "_more", "type"}


def signature(rec):
    """(tipo, corpo-canonico dos campos de payload) — estavel entre maquinas."""
    typ = rec.get("type", "?")
    body = ";".join(f"{k}={rec[k]}" for k in sorted(rec) if k not in META)
    return typ, body


def load(path):
    recs = []
    try:
        with open(path, encoding="utf-8") as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                try:
                    recs.append(json.loads(line))
                except json.JSONDecodeError:
                    pass  # linha truncada (crash no meio da escrita) — ignora
    except OSError as e:
        print(f"!! nao consegui ler {path}: {e}", file=sys.stderr)
    return recs


def parse_time(s):
    """'HH:MM:SS.fff' -> segundos do dia (float). None se nao parsear."""
    if not s:
        return None
    try:
        h, m, sec = s.split(":")
        return int(h) * 3600 + int(m) * 60 + float(sec)
    except (ValueError, AttributeError):
        return None


def within(rec, target, window):
    t = parse_time(rec.get("t"))
    return t is not None and abs(t - target) <= window


def print_timeline(labels, recs_by):
    """Intercala os registros de todos os arquivos por horario — ver a ordem no momento do bug."""
    rows = []
    for label in labels:
        who = label.replace("CS2M_wiretap_", "").replace(".jsonl", "")[-8:]
        for r in recs_by[label]:
            t = parse_time(r.get("t"))
            typ = r.get("type", "?")
            if t is None or typ == "WireTapStart":
                continue
            body = ";".join(f"{k}={r[k]}" for k in sorted(r) if k not in META)
            rows.append((t, r.get("t"), who, r.get("dir", "?"), typ, body[:110]))
    rows.sort(key=lambda x: x[0])
    print(f"\n=== TIMELINE ({len(rows)} comandos na janela, por tempo) ===")
    for _, ts, who, d, typ, body in rows:
        print(f"  {ts}  [{who:>8}] {d:<3} {typ:<22} {body}")


def main():
    ap = argparse.ArgumentParser(description="Diff de gravacoes wiretap do CS2M")
    ap.add_argument("files", nargs="+", help="2+ arquivos CS2M_wiretap_*.jsonl")
    ap.add_argument("--all", action="store_true", help="inclui tipos periodicos (ruidosos)")
    ap.add_argument("--type", help="filtra por substring do tipo (ex.: Net, Place, Zone)")
    ap.add_argument("--limit", type=int, default=200, help="max de divergencias listadas")
    ap.add_argument("--around", help="foca em comandos perto deste horario HH:MM:SS (ex.: 22:43:10)")
    ap.add_argument("--window", type=float, default=120.0,
                    help="janela em segundos ao redor de --around (padrao 120)")
    ap.add_argument("--timeline", action="store_true",
                    help="com --around, imprime os comandos dos jogadores intercalados por tempo")
    args = ap.parse_args()

    target = parse_time(args.around) if args.around else None
    if args.around and target is None:
        print(f"!! --around invalido: '{args.around}' (use HH:MM:SS)", file=sys.stderr)
        return

    labels = []
    recs_by = {}
    sigs_by = {}
    for p in args.files:
        label = p.replace("\\", "/").split("/")[-1]
        labels.append(label)
        recs = load(p)
        if target is not None:
            recs = [r for r in recs if within(r, target, args.window)]
        recs_by[label] = recs
        s = set()
        for r in recs:
            typ, body = signature(r)
            if typ == "WireTapStart":
                continue
            if not args.all and typ in NOISY:
                continue
            if args.type and args.type.lower() not in typ.lower():
                continue
            s.add((typ, body))
        sigs_by[label] = s

    scope_note = f"   (janela: {args.around} +-{args.window:.0f}s)" if target is not None else ""
    print(f"=== RESUMO POR ARQUIVO ==={scope_note}")
    for label in labels:
        recs = recs_by[label]
        dirs = Counter(r.get("dir") for r in recs)
        types = Counter(r.get("type") for r in recs if r.get("type") != "WireTapStart")
        print(f"\n[{label}]  {len(recs)} registros   OUT={dirs.get('OUT', 0)}  IN={dirs.get('IN', 0)}")
        top = ", ".join(f"{t}:{c}" for t, c in types.most_common(8))
        print(f"    tipos: {top}")

    if args.timeline and target is not None:
        print_timeline(labels, recs_by)

    if len(labels) < 2:
        print("\n(so 1 arquivo — passe 2+ pra comparar. Resumo acima e' so a leitura desse arquivo.)")
        return

    all_sigs = set().union(*sigs_by.values())
    diverg = []
    for s in all_sigs:
        present = [l for l in labels if s in sigs_by[l]]
        missing = [l for l in labels if s not in sigs_by[l]]
        if missing:
            diverg.append((s, present, missing))

    scope = f" (tipo~='{args.type}')" if args.type else ""
    print(f"\n=== DIVERGENCIAS{scope}: comando de acao visto por uns, nao por outros — {len(diverg)} ===")
    if not diverg:
        print("NENHUMA — todos os jogadores viram os mesmos comandos de acao. ✅")
        print("(se ainda assim algo divergiu na tela, o bug e' no APPLY, nao no transporte —")
        print(" cheque [Hash] DRIFT / [Invariant] no CS2M.log do host.)")
    else:
        diverg.sort(key=lambda x: (x[0][0], x[0][1]))
        for (typ, body), present, missing in diverg[: args.limit]:
            print(f"\n  {typ}")
            print(f"    {body[:200]}")
            print(f"    TEM:   {present}")
            print(f"    FALTA: {missing}   <-- provavel ponto do desync")
        if len(diverg) > args.limit:
            print(f"\n  ... +{len(diverg) - args.limit} mais (use --limit)")

    print("\nDica: --type Net|Place|Delete|Zone|Area|Water pra focar; --all inclui os periodicos.")


if __name__ == "__main__":
    main()
