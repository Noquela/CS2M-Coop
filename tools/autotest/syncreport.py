#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
syncreport.py — relatorio de validacao autonoma do 2-sim, mecanica por mecanica.

Le os DOIS logs (host + client Sandboxie) apos um roteiro (-Test 1) e reporta, por
tool/mecanica: quantos o host ENVIOU vs o client APLICOU, mais SKIP/DROP e DRIFT.
Da o quadro "cada tool sincou?" que o Bruno pediu, sem olhar screenshot.
"""
import sys
import re

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

HOST = r"C:\Users\Bruno\AppData\LocalLow\Colossal Order\Cities Skylines II\Logs\CS2M.log"
BOX = r"C:\Sandbox\Bruno\CS2Coop\user\current\AppData\LocalLow\Colossal Order\Cities Skylines II\Logs\CS2M.log"

# mecanica -> (padrao no HOST = enviou, padrao no CLIENT = aplicou)
MECHANICS = [
    ("rua (place)",      r"\[Net\] .*APPLIED-DEF|TEST net SEND",      r"\[Net\] APPLIED-DEF"),
    ("rua delete",       r"TEST (net-)?delete|arm-delete|splitflow",  r"\[NetEdit\] APPLIED delete"),
    ("rua delete via=id",r"n/a",                                       r"delete resolve via=id"),
    ("rua delete via=pos",r"n/a",                                      r"delete resolve via=pos"),
    ("rua upgrade",      r"TEST upgrade SEND|node-upgrade",            r"\[NetEdit\] APPLIED (upgrade|node-upgrade)"),
    ("zona",             r"TEST zone SEND",                            r"\[Zone\] APPLIED"),
    ("zona SKIP-unknown",r"n/a",                                       r"\[Zone\] SKIP unknownZone"),
    ("zona DROP",        r"n/a",                                       r"\[Zone\] DROP"),
    ("agua",             r"TEST water SEND",                           r"\[Water\] APPLIED"),
    ("mover",            r"TEST move SEND",                            r"\[Move\] APPLIED|\[Place\] APPLIED"),
    ("distrito",         r"TEST district SEND",                        r"\[District\] APPLIED"),
    ("area/campo",       r"\[Area\] DETECT\+SEND",                     r"\[Area\] APPLIED"),
    ("terreno",          r"TEST terrain SEND",                         r"\[Terrain\] APPLIED"),
    ("politica",         r"TEST policy SEND",                          r"\[Policy\] APPLIED"),
    ("tile",             r"TEST tile SEND",                            r"\[Tile\] APPLIED|TilePurchase"),
    ("devtree",          r"TEST devtree SEND",                         r"\[DevTree\] APPLIED"),
    ("rota",             r"TEST route SEND",                           r"\[Route\] APPLIED|route.*APPLIED"),
    ("fogo",             r"TEST fire SEND",                            r"\[Fire\] APPLIED"),
    ("objeto/predio",    r"\[Place\] .*SEND|TEST .*INJECT",            r"\[Place\] APPLIED"),
]


def count(path, pattern):
    if pattern == "n/a":
        return None
    pat = re.compile(pattern)
    n = 0
    try:
        with open(path, "r", encoding="utf-8", errors="replace") as f:
            for line in f:
                if pat.search(line):
                    n += 1
    except FileNotFoundError:
        return -1
    return n


def drifts(path):
    pat = re.compile(r"Hash\] DRIFT.*?(\[[a-z]+(?:\(hash\))?\])")
    seen = {}
    try:
        with open(path, "r", encoding="utf-8", errors="replace") as f:
            for line in f:
                m = pat.search(line)
                if m:
                    seen[m.group(1)] = seen.get(m.group(1), 0) + 1
    except FileNotFoundError:
        pass
    return seen


def main():
    host = sys.argv[1] if len(sys.argv) > 1 else HOST
    box = sys.argv[2] if len(sys.argv) > 2 else BOX
    print("=" * 66)
    print("RELATORIO DE SYNC — 2-sim (host envia / client aplica)")
    print("=" * 66)
    print(f"{'mecanica':<22}{'host':>8}{'client':>9}   status")
    print("-" * 66)
    for name, hpat, cpat in MECHANICS:
        h = count(host, hpat)
        c = count(box, cpat)
        hs = "-" if h is None else ("?" if h == -1 else str(h))
        cs = "-" if c is None else ("?" if c == -1 else str(c))
        # status heuristico
        if c is not None and c != -1 and c > 0:
            status = "OK aplicou"
        elif c == 0 and h and h != -1 and h > 0:
            status = "!! host enviou, client NAO aplicou"
        else:
            status = ""
        print(f"{name:<22}{hs:>8}{cs:>9}   {status}")
    print("-" * 66)
    print("DRIFT no cliente (divergencia detectada):")
    d = drifts(box)
    if not d:
        print("  (nenhum)")
    for k, v in sorted(d.items()):
        print(f"  {k}: {v}x  <- rode statediff.py p/ localizar a entidade")
    print("=" * 66)


if __name__ == "__main__":
    main()
