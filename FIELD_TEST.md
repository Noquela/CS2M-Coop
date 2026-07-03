# FIELD_TEST.md — roteiro do teste de campo dos 3 jogadores (v52)

> O objetivo deste teste é a ÚNICA coisa que ainda aumenta a certeza de que "tudo sincroniza": duas
> (três) simulações vivas interagindo de verdade. O selftest solo já valida 58 cenas; o que ele não
> pega é o que só aparece quando 3 pessoas mexem ao mesmo tempo. Com o v52 os radares estão armados —
> siga este roteiro para que qualquer bug residual apareça **com prova para reproduzir**.

---

## 0. Antes de começar (todos os 3)

1. **Mesma versão.** Os 3 instalam o **`CS2M_v52.zip`** (o precondition recusa conexão se alguém
   estiver em versão diferente — se der "version mismatch", é isso). Extrair para
   `...\LocalLow\Colossal Order\Cities Skylines II\Mods\CS2M` (substituindo o conteúdo).
2. **Ligar os radares.** Em vez de abrir o jogo direto, use o `CS2M_debug.bat` (está no Desktop do
   Bruno; conteúdo no §4 aqui embaixo) — coloque-o na pasta do jogo, ao lado do `Cities2.exe`, e
   abra por ele. Ele liga `CS2M_WIRETAP=1` (grava todo comando) e `CS2M_STATEHASH=1` (detector de
   divergência). **Os 3 fazem isso** — o wiretap só serve pra reproduzir se todos gravarem.
3. **Combinar quem é o HOST.** O host é a autoridade (demanda, dinheiro, fogo). Os radares
   `[Hash] DRIFT` aparecem no log de quem é CLIENTE; o `[Invariant] VIOLATION` no log de todos.
4. **Onde ficam os logs/gravações** (pasta LocalLow do CS2):
   - `Logs\CS2M.log` — o log do mod (procurar `[Hash] DRIFT`, `[Invariant] VIOLATION`).
   - `CS2M_wiretap_<data>_<hora>.jsonl` — a gravação de comandos daquela sessão.

---

## 1. Roteiro — reproduzir os bugs conhecidos de propósito

Faça cada bloco, depois **todos olham a mesma região** e confirmam que veem a MESMA coisa. Se
divergir, anote a hora (pro §3).

**A. Ruas e junções entre jogadores** (o bug clássico)
- Jogador 1 desenha uma avenida reta. Jogador 2, ao lado, desenha uma rua que **encosta no meio**
  da avenida do J1 (junção T). Jogador 3 cruza as duas (cruzamento X).
- Confirmar nos 3: a junção conecta de verdade (dá pra ver o tráfego/energia passar), não fica rua
  sobreposta nem "buraco". Colocar um prédio que puxe energia através da junção — funciona nos 3?

**B. Zonas** (o "não sincou 100%")
- J1 pinta residencial baixa ao longo de uma rua; J2 pinta comércio ao lado.
- Confirmar nos 3: as MESMAS células ficam pintadas das MESMAS cores; growables nascem iguais.

**C. Água e canos**
- J1 coloca uma torre d'água + canos; J2 coloca esgoto.
- Confirmar: a água flui pelos canos nos 3 (prédios ficam com ícone de água OK igual).

**D. Demolição**
- J2 demole um prédio grande e um pedaço de rua com prédios em cima.
- Confirmar nos 3: some tudo limpo — **nenhum pedaço de prédio fica no chão**, a rua some junto.

**E. Áreas de trabalho / extração**
- J3 cria uma área de trabalho (fazenda/mineração) e ajusta o polígono dela.
- Confirmar nos 3: a área aparece com o MESMO formato **na hora** (sem precisar de /resync).

**F. Demanda (RCI) e dinheiro**
- Todos olham a barra RCI ao mesmo tempo em vários momentos.
- Confirmar: a barra é IGUAL nos 3 (o host manda); o dinheiro bate.

---

## 2. Radares durante o jogo (o que catch automático)

Deixe um segundo monitor / alt-tab no `CS2M.log` (ou abra depois). Os sinais:

- **`[Hash] DRIFT strike=N [categoria XvsY] ...`** — o detector viu os dois mundos discordarem numa
  categoria (roads / zones / buildings / areas / synced / districts / water) DEPOIS que os dois lados
  pararam de mudar. `strike=2` = confirmado (aparece um aviso no chat pedindo /resync). **A categoria
  diz na hora onde está o bug.** Ex.: `[Hash] DRIFT strike=2 [roads 45vs44(hash)]` = uma rua não
  chegou/ficou diferente.
- **`[Invariant] VIOLATION summary dupEdges=X orphans=Y deadAttach=Z`** — problema estrutural: ruas
  duplicadas (sobreposição), órfãos (pedaço sem dono), attach com pai morto. **O que importa é o
  número CRESCER** durante uma ação (um save velho pode carregar lixo parado).
- No chat: se aparecer **"worlds drifting apart (...) — ask the host to type /resync"**, o detector
  já confirmou divergência; o host roda `/resync` pra reconciliar, mas **anote antes** (§3).

---

## 3. Como reportar um bug (pra dar pra reproduzir)

Quando algo divergir, isto é ouro:
1. **A hora** (olhe o relógio do jogo/sistema no momento — ex. 22:43).
2. **Os `CS2M_wiretap_*.jsonl` dos 3** daquela sessão (pasta LocalLow). São eles que permitem o diff:
   comparar o fluxo de comandos dos 3 no timestamp mostra exatamente qual comando se perdeu ou
   chegou diferente.
3. **O `CS2M.log` do host** (tem as linhas `[Hash] DRIFT` / `[Invariant]` / `[Net] SPLIT` etc).
4. Uma frase do que aconteceu ("J2 fez junção T na rua do J1 e ficou buraco só na tela do J3").

Com esses 4, dá pra reconstruir o bug fora do jogo e consertar de forma determinística — sem ficar
adivinhando.

---

## 4. `CS2M_debug.bat` (ligar os radares)

Salve este arquivo como `CS2M_debug.bat` **na pasta do jogo, ao lado do `Cities2.exe`**, e abra o
jogo por ele (duplo-clique). Portátil — usa a própria pasta, então serve pra qualquer um dos 3
independente do caminho de instalação:

```bat
@echo off
cd /d "%~dp0"
set CS2M_WIRETAP=1
set CS2M_STATEHASH=1
start "" "Cities2.exe"
```

Kill switches (se algum sistema atrapalhar): `set CS2M_STATEHASH=0` (desliga o detector),
`set CS2M_FIRE_SYNC=0` (fogo), `set CS2M_GROWABLE_SYNC=0` (growables). O wiretap é passivo e leve —
pode deixar ligado a sessão toda.

---

## 5. Depois do teste

- Junta os wiretaps + logs numa pasta e me passa (Bruno) — eu faço o diff e o diagnóstico.
- Se NADA divergiu a sessão inteira (sem `[Hash] DRIFT strike=2`, sem `[Invariant]` crescendo): é a
  prova de campo que faltava — aí sim "100% de certeza que tudo sincroniza".
