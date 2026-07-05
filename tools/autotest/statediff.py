#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
statediff.py — Fase 0b: localizacao AUTOMATICA de divergencia de sync.

Le os per-entity dumps que o mod escreve (CS2M_NODEDUMP=1) nos DOIS logs do 2-sim
(host + client Sandboxie) e faz o SET-DIFF por subsistema: aponta a ENTIDADE EXATA
que so existe num lado (fantasma / faltando) ou que tem forma diferente. Fim de
olhar screenshot e adivinhar.

Cada dump e uma linha unica, ex:
  [AreaDump:HOST]   count=607 0/0:n4:o1 12,3/45,6:n8:o0 ...
  [AreaDump:CLIENT] count=603 0/0:n4:o1 ...
Os tokens ja sao chaves estaveis por-entidade e ordenados; a cultura pt-BR usa
VIRGULA decimal nos dois lados, entao a comparacao e string-exata (multiset).

Uso:
  python statediff.py                       # usa os caminhos padrao do 2-sim
  python statediff.py HOSTLOG CLIENTLOG      # caminhos explicitos
  python statediff.py --subsys areas         # so um subsistema (nodes|edges|areas)
"""
import sys
import re
from collections import Counter

# Console do Windows costuma ser cp1252 e explode em caractere fora do Latin-1.
try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

HOST_DEFAULT = r"C:\Users\Bruno\AppData\LocalLow\Colossal Order\Cities Skylines II\Logs\CS2M.log"
CLIENT_DEFAULT = r"C:\Sandbox\Bruno\CS2Coop\user\current\AppData\LocalLow\Colossal Order\Cities Skylines II\Logs\CS2M.log"

# tag no log -> nome amigavel + como ler o token
SUBSYS = {
    "NodeDump": ("nodes", "posicao x/z : grau (junção vs ponta-morta)"),
    "EdgeDump": ("edges", "par de extremidades a-b"),
    "AreaDump": ("areas", "centro cx/cz : nós : owned"),
}


def last_dump(path, tag, role):
    """Retorna (count, [tokens]) da ULTIMA linha [tag:role] no arquivo, ou None."""
    pat = re.compile(r"\[" + re.escape(tag) + ":" + role + r"\]\s+count=(\d+)\s*(.*)")
    found = None
    try:
        with open(path, "r", encoding="utf-8", errors="replace") as f:
            for line in f:
                m = pat.search(line)
                if m:
                    count = int(m.group(1))
                    toks = m.group(2).split()
                    found = (count, toks)
    except FileNotFoundError:
        print(f"  ! log nao encontrado: {path}")
        return None
    return found


def area_center(tok):
    """Para areas, extrai o centro (cx/cz) sem o :nX:oY, pra casar entidade que so mudou de FORMA."""
    return tok.split(":", 1)[0]


def diff_subsys(tag, host_log, client_log):
    friendly, meaning = SUBSYS[tag]
    h = last_dump(host_log, tag, "HOST")
    c = last_dump(client_log, tag, "CLIENT")
    print(f"\n=== {friendly.upper()}  ({meaning}) ===")
    if h is None or c is None:
        print(f"  (sem dump {tag} em um dos lados — rode com CS2M_NODEDUMP=1 e provoque a ação)")
        return None

    hc, ht = h
    cc, ct = c
    print(f"  HOST count={hc}   CLIENT count={cc}   delta={hc - cc:+d}")

    hset, cset = Counter(ht), Counter(ct)
    only_host = list((hset - cset).elements())   # host tem, client NAO -> client faltando
    only_client = list((cset - hset).elements())  # client tem, host NAO -> fantasma no client

    if not only_host and not only_client:
        print("  [OK] IDENTICO - nenhuma entidade diverge neste subsistema")
        return True

    # Para areas: separa "mudou de forma" (mesmo centro, token diferente) de "some/sobra de verdade".
    if tag == "AreaDump":
        h_by_c = {area_center(t): t for t in only_host}
        c_by_c = {area_center(t): t for t in only_client}
        shape = sorted(set(h_by_c) & set(c_by_c))
        for ctr in shape:
            print(f"  ~ FORMA DIFERE @ {ctr}:  host={h_by_c[ctr]}  client={c_by_c[ctr]}")
        gone = [h_by_c[k] for k in h_by_c if k not in c_by_c]
        extra = [c_by_c[k] for k in c_by_c if k not in h_by_c]
        for t in sorted(gone):
            print(f"  - FALTANDO no client:  {t}")
        for t in sorted(extra):
            print(f"  + FANTASMA no client:  {t}")
    else:
        for t in sorted(only_host):
            print(f"  - FALTANDO no client:  {t}")
        for t in sorted(only_client):
            print(f"  + FANTASMA no client:  {t}")

    return False


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    subsys_filter = None
    for a in sys.argv[1:]:
        if a.startswith("--subsys"):
            subsys_filter = a.split("=", 1)[1] if "=" in a else None

    host_log = args[0] if len(args) >= 1 else HOST_DEFAULT
    client_log = args[1] if len(args) >= 2 else CLIENT_DEFAULT

    print("STATEDIFF — localizador de divergência (Fase 0b)")
    print(f"  HOST   : {host_log}")
    print(f"  CLIENT : {client_log}")

    tags = [t for t in SUBSYS if (subsys_filter is None or SUBSYS[t][0] == subsys_filter)]
    results = {}
    for tag in tags:
        results[SUBSYS[tag][0]] = diff_subsys(tag, host_log, client_log)

    print("\n--- RESUMO ---")
    any_diverge = False
    for name, ok in results.items():
        if ok is True:
            print(f"  {name}: IDÊNTICO")
        elif ok is False:
            print(f"  {name}: DIVERGE  <-- alvo localizado acima")
            any_diverge = True
        else:
            print(f"  {name}: sem dados")
    sys.exit(2 if any_diverge else 0)


if __name__ == "__main__":
    main()
