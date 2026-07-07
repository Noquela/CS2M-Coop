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
    "BlockDump": ("zones", "bloco x/z : WxH[:oORDEM] = células run-length por NOME[~FLAGS] de zona"),
    "BldgDump": ("buildings", "posicao x/z : nome do prefab"),
}


def last_dump(path, tag, role):
    """Retorna (count, [tokens]) da ULTIMA dump [tag:role] no arquivo, ou None.

    Dumps grandes (400+ entidades, hoje só BldgDump) vêm quebrados em várias linhas
    'part=k count=N ...' em vez de uma linha 'count=N ...' única — junta as partes
    de uma mesma rodada (part=0,1,2,... emitidas de volta pra volta no mesmo tick)
    antes de comparar. A ULTIMA rodada completa (tokens acumulados >= count) vence.
    """
    single_pat = re.compile(r"\[" + re.escape(tag) + ":" + role + r"\]\s+count=(\d+)\s*(.*)")
    part_pat = re.compile(r"\[" + re.escape(tag) + ":" + role + r"\]\s+part=(\d+)\s+count=(\d+)\s*(.*)")
    found = None
    cur_parts = {}
    cur_count = None
    try:
        with open(path, "r", encoding="utf-8", errors="replace") as f:
            for line in f:
                mp = part_pat.search(line)
                if mp:
                    part_idx = int(mp.group(1))
                    count = int(mp.group(2))
                    toks = mp.group(3).split()
                    if part_idx == 0:
                        cur_parts = {}
                        cur_count = count
                    cur_parts[part_idx] = toks
                    all_toks = []
                    for k in sorted(cur_parts):
                        all_toks.extend(cur_parts[k])
                    if cur_count is not None and len(all_toks) >= cur_count:
                        found = (cur_count, all_toks)
                    continue
                m = single_pat.search(line)
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


# v56: BlockDump ganhou um campo opcional ":o<m_Order>" no cabecalho (BuildOrder do bloco — o
# desempate de sobreposicao de zonas, Game.Zones.BlockSystem.m_Order) logo antes do "=" das celulas.
# Retrocompativel: dumps antigos (sem ":oN") nao batem o regex -> order=None, stripped=tok intacto,
# e o chamador cai no caminho generico de sempre.
_BLOCK_ORDER_RE = re.compile(r":o(\d+)=")


def block_strip_order(tok):
    """Retorna (ordem_ou_None, token_com_:oN_removido) — usado pra achar um bloco cujo UNICO diff
    entre host/client e o BuildOrder (mesma posicao/tamanho/celulas, prioridade de sobreposicao
    diferente), caso em que vale a pena um alerta proprio em vez do "FORMA DIFERE" generico."""
    m = _BLOCK_ORDER_RE.search(tok)
    if not m:
        return None, tok
    order = m.group(1)
    stripped = tok[:m.start()] + "=" + tok[m.end():]
    return order, stripped


# CellFlags emergentes (decomp Zones/CellFlags.cs): Occupied=0x20 (prédio físico intersecta a célula,
# via CellOccupyJobs) e Overridden=0x10 (prédio força a zona). Growables nascem por demanda/timing LOCAL
# — o contrato de sync deixa divergir de propósito — então esses 2 bits diferem legitimamente numa célula
# com prédio crescido. Mascará-los antes de comparar evita marcar essa divergência emergente como bug.
_EMERGENT_MASK = 0x30  # Occupied | Overridden
_CELL_FLAG_RE = re.compile(r"~([0-9A-Fa-f]+)")


def block_strip_emergent(tok):
    """Zera os bits emergentes de cada sufixo ~hex de célula; remove o ~ quando o resto fica 0. Dois
    blocos que só diferem por growable colapsam no mesmo token. Emergente = Occupied/Overridden SEMPRE;
    E quando a célula está Occupied (prédio cresceu ali, sincado), o bit Visible (0x8) tambconfigém é
    derivado/transiente do render do predio — as duas maquinas podem discordar 1 bit sem que o predio
    divirja (host ~28 vs client ~20: ambas Occupied, só o Visible flutua). Então mascara Visible SÓ
    quando Occupied está presente."""
    def repl(m):
        raw = int(m.group(1), 16)
        # +Visible(0x8) SEMPRE. Decomp (CellCheckHelpers.cs:475-482): Visible = !(Blocked|Redundant),
        # puro-derivado — não carrega sinal autorado independente. A máscara antiga só tirava Visible
        # QUANDO Occupied estava setado na MESMA célula, o que criava uma ASSIMETRIA: growable presente
        # num lado só (host ~28 Occupied+Visible vs client ~8 Visible) não colapsava e virava falso
        # "FORMA DIFERE". Tirando Visible dos dois lados, ~28/~20/~8 viram todos iguais (growable-timing
        # benigno), enquanto Blocked(0x1)/Shared(0x2) seguem visíveis — divergência REAL de buildability
        # (host Visible vs client Blocked = rua divergente) continua pegando.
        mask = _EMERGENT_MASK | 0x8
        v = raw & ~mask
        return "" if v == 0 else f"~{v:X}"
    return _CELL_FLAG_RE.sub(repl, tok)


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

    # Para areas/blocos: separa "mudou de forma/pintura" (mesma posição, token diferente) de
    # "some/sobra de verdade". Em BlockDump: FALTANDO = o próprio bloco derivado da rua não
    # existe no client (cascata BuildOrder); FORMA DIFERE = bloco existe mas células/pintura
    # divergem (índice/paint).
    real_divergence = False  # ORDEM/growable-only NAO contam — sao benignos (tiebreak / sim emergente)
    if tag in ("AreaDump", "BlockDump"):
        h_by_c = {area_center(t): t for t in only_host}
        c_by_c = {area_center(t): t for t in only_client}
        shape = sorted(set(h_by_c) & set(c_by_c))
        for ctr in shape:
            if tag == "BlockDump":
                h_order, h_rest = block_strip_order(h_by_c[ctr])
                c_order, c_rest = block_strip_order(c_by_c[ctr])
                if h_order is not None and c_order is not None and h_order != c_order and h_rest == c_rest:
                    # Mesmo bloco (pos/tamanho/celulas identicos) — SO o BuildOrder difere.
                    print(f"  ~ ORDEM DIFERE @ {ctr}:  host=o{h_order} client=o{c_order}")
                    continue
                # Se o UNICO diff sao os bits emergentes (growable Occupied/Overridden), NAO e bug:
                # crescimento de predio e sim local por design. Mascara e reporta como informativo.
                if block_strip_emergent(h_rest) == block_strip_emergent(c_rest):
                    print(f"  . growable-only @ {ctr} (Occupied/Overridden divergem — emergente, esperado)")
                    continue
            print(f"  ~ FORMA DIFERE @ {ctr}:  host={h_by_c[ctr]}  client={c_by_c[ctr]}")
            real_divergence = True
        gone = [h_by_c[k] for k in h_by_c if k not in c_by_c]
        extra = [c_by_c[k] for k in c_by_c if k not in h_by_c]
        for t in sorted(gone):
            print(f"  - FALTANDO no client:  {t}")
            real_divergence = True
        for t in sorted(extra):
            print(f"  + FANTASMA no client:  {t}")
            real_divergence = True
    else:
        for t in sorted(only_host):
            print(f"  - FALTANDO no client:  {t}")
            real_divergence = True
        for t in sorted(only_client):
            print(f"  + FANTASMA no client:  {t}")
            real_divergence = True

    if not real_divergence:
        print("  [OK] só diferenças benignas (ordem/growable emergente) — sincronia real intacta")
        return True
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
