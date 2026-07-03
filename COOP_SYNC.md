# CS2M-Coop — Documento de Estado (v1.0.50.0)

> **Este é o documento canônico do projeto.** Ele existe para que qualquer pessoa (ou sessão de IA
> com contexto compactado) reconstrua o estado inteiro do fork a partir daqui. Última revisão
> completa: 2026-07-02, release v1.0.50.0 (roadmap completo: paradas, ping/painel, incêndios
> host-auth, auto-reconexão, custo de tile, aviso de save, supressão weather/condemned/abandoned).

---

## 0. O que é / onde está

Fork de [CitiesSkylinesMultiplayer/CS2M](https://github.com/CitiesSkylinesMultiplayer/CS2M) que
adiciona sincronização de gameplay completa para **Cities: Skylines II 1.5.3f1**. MIT.

| Recurso | Localização |
|---|---|
| Repo canônico local | `C:\Users\Bruno\CS2M-Coop` (clone estável; NÃO usar cópias em pastas temp) |
| GitHub (fonte da verdade) | `https://github.com/Noquela/CS2M-Coop` (branch `main`) |
| Mod deployado (PC do Bruno) | `C:\Users\Bruno\AppData\LocalLow\Colossal Order\Cities Skylines II\Mods\CS2M` |
| Jogo | `C:\JogosCrackeados\Cities.Skylines.II.v1.5.3f1\game` (crack RUNE: **1 instância só**; single-instance NÃO deve ser burlado) |
| Log do mod | `...\Cities Skylines II\Logs\CS2M.log` (alto volume atrás de `CS2M_VERBOSE=1`) |
| Zip de release | `C:\Users\Bruno\Desktop\CS2M_v50.zip` (conteúdo da pasta do mod sem `.claude`) |

**Regra de ouro do multiplayer**: todos os jogadores na MESMA versão (precondition bloqueia).
Bruno joga com 2 amigos (3 jogadores — suportado desde a v43).

---

## 1. Matriz de features (o que sincroniza, como, desde quando)

Padrão geral: **Detector** (UpdateBefore ModificationEnd; tag transitória `Applied`/`Created`/
`Deleted` ou diff de snapshot) → **Command** POCO (MessagePack attributeless) → handler enfileira →
**Apply** no main thread (fase certa!). Identidade cross-PC: `CS2M_SyncId` (objetos criados em
sessão), prefab+posição (nativos), prefab+número (linhas).

| Feature | Como | Versão |
|---|---|---|
| Objetos (prédios/props/árvores) | archetype direto + SyncId; sub-objects via fase; **sub-nets** e **sub-áreas** replicando `BuildingConstructionSystem.CreateNets/CreateAreas` | v37/v38/v44/v45 |
| Redes (rua/trilho/cano/energia) | **injeção de definição `CreationFlags.Permanent` antes de Mod1** — o jogo constrói tudo (geometria, lanes, blocos); snap em nós ≤0,5 m; guard de duplicata por nós+prefab | v38 |
| Delete/upgrade de rede | endpoints XZ; **apply antes de Mod1** (fix do crash nativo) | v37/v41 |
| Supressão de split-cascade | pedaços de split (endpoints sobre curva deletada no mesmo frame + mais curtos) não re-sincam | v39 |
| Zonas | diff de células por bloco + **ZoneEcho TTL** + retry ~15 s p/ blocos atrasados | v37/v39/v42 |
| Delete/move de objetos | por SyncId; **nativos por prefab+posição** (delete: qualquer caminho p/ serviços/árvores — v47; growables só via bulldozer); **move de nativos** captura transform pré-move do `Temp.m_Original` | v37/v42/v47/v48 |
| Dinheiro | host-autoritativo ~1 Hz; **host debita custo de construção remota**; empréstimos reconciliados | v37/v38/v48 |
| XP/marcos | XP host-autoritativo; marcos avançam no cliente | v37 |
| Dev tree | evento `Unlock` espelhado + débito de pontos | v41 |
| Impostos/orçamento | APIs públicas (`TaxSystem`, `CityServiceBudgetSystem`) | v37 |
| Políticas | cidade: diff snapshot (apply em **Mod3**); **prédio/distrito: eventos `Modify` da própria UI** com alvo resolvido | v37/v46 |
| Distritos | archetype de `AreaData` + polígono | v42 |
| Superfícies/pavimento + delete de áreas | polígono + prefab/centro; delete por prefab+centro | v46 |
| Áreas de trabalho (fazenda/mina) | padrão no place (réplica `CreateAreas`) + edição via `AreaEditCommand` | v45 |
| Água/terreno | `WaterSourceData` direto — **v50: Y ancorado ao terreno LOCAL do receptor + delete de fonte sincado** (fonte deletada vivia p/ sempre nos outros PCs = "água do nada" em campo); brush replay best-effort — **v50: cap de 3 strokes/frame + descarte de backlog >30** (fila acumulada no pause despejava tudo num frame = "torre de terreno" em campo); drift residual → `/resync` | fork inicial/v50 |
| Tiles do mapa | centro do tile → `RemoveComponent<Native>` (espelho vanilla) | v44 |
| Extensões de serviço | objeto com `Owner`; `ServiceUpgradeSystem` fia `InstalledUpgrade` sozinho | v44 |
| **Growables host-autoritativos** (EXPERIMENTAL) | host sinca spawns do sim; cliente desliga `ZoneSpawnSystem`; level-up = delete-por-id + spawn; spawn limpa o lote no receptor. `CS2M_GROWABLE_SYNC=0` desliga | v44 |
| Renomear (prédio/distrito/linha) | `NameSystem` por identidade | v48 |
| **Linhas de transporte** | criar/re-rotear/deletar/cor/número; waypoints completos; paradas por SyncId/posição; receptor constrói dos `RouteData` archetypes; eco por hash de waypoints; número aplicado com delay de 3 frames (InitializeSystem numera antes) | v49 |
| Clima + relógio | overrides do `ClimateSystem` + realinhamento `TimeData.m_FirstFrame` (~0,5 Hz) | v42 |
| **Paradas/objetos roadside** (abrigo de ônibus, taxi stand, mailbox…) | detector deixou de excluir stop-objects; hint = ponto na curva do edge parent (reusa OwnerX/Y/Z, protocolo intacto); apply resolve o edge (dist 3D ≤16 m) e seta `Attached` — `AttachSystem.UpdateAttachedReferences` registra o `SubObject` no edge; `UpdateBefore<RemotePlacementApply, RouteApply>` garante parada-antes-da-linha no mesmo frame | v50 |
| **Incêndios host-autoritativos** (`CS2M_FIRE_SYNC=0` desliga) | host detecta transições `OnFire`/`Destroyed` (snapshot-diff 0,5 s; baseline silencioso p/ rubble de save) → `FireSyncCommand` kind=start/end/collapse; cliente espelha add/remove `OnFire`+`BatchesUpdated` e no colapso injeta **evento `Destroy` real** (DestroySystem local faz o teardown vanilla); cliente suprime `FireHazard`/`FireSimulation`/`FireRescueDispatch` | v50 |
| **Danos da sim host-auth (garimpo)** | cliente suprime também `WeatherDamage`/`WeatherHazard` (raios) e `CondemnedBuilding`/`DestroyAbandoned` (demolições da sim); host com growable-sync manda deletes de growables NATIVOS demolidos pela sim (gate de bulldozer não se aplica ao host) | v50 |
| **Ping no mapa** (`/ping` no chat) | `MapPingCommand` relayado; marcador pulsante 8 s no overlay (cor do jogador) + linha no chat; pinga onde o cursor local aponta | v50 |
| **Painel de jogadores** | host broadcasta `PlayerStatsCommand` ~1 Hz (nome + latência LiteNetLib por peer); faixa no topo do chat (bolinha na cor do cursor + nome + ms) + `/players` imprime no chat | v50 |
| **Auto-reconexão** | queda de sessão estabelecida (não intencional) → re-join automático a cada 5 s por até 2 min com a MESMA config; re-join = world transfer = resync completo; cancela em preconditions error ou disconnect manual (`UserDisconnect`) | v50 |
| **Custo de tile espelhado** | comprador sampleia `MapTilePurchaseSystem.cost` da seleção viva por frame e manda `Cost` no comando; host debita do saldo compartilhado | v50 |
| **Aviso "salvou o jogo"** | `GameManager.onGameSaveLoad` (start=false, success) → `ChatMessageCommand` relayado "💾 X saved the game (nome)" | v50 |
| Velocidade/pause | host-autoritativo, **reforço por frame** (a UI vanilla reescreve `selectedSpeed`) | fork/v38 |
| Cursor + nome | círculo overlay + label cohtml (slot `Game`, absolute 100%, rem; hide/stale 3 s) | fork/v38 |
| 3+ jogadores | **relay estrela no host** (RelayOnServer implementado; upstream só tinha a flag) + `SenderId = peer.Id+1` | v43 |
| `/resync` | host re-transmite o mundo (fluxo de join reusado) — reconcilia TUDO | fork/v38 (CurrentRole fix) |
| Detector de divergência | fingerprint (edges/synced/distritos/água) 0,1 Hz; 2 strikes → aviso no chat | v42 |
| `/validate` | suíte inteira dentro da sessão aberta, PASS/FAIL no chat | v40 |

### Não sincado por design
População/cidadãos/veículos/tráfego individuais (emergente, não-determinístico) — mitigado por
growables host-auth + `/resync`. Rádio/foto/câmera são locais.

---

## 2. As LEIS da arquitetura (lições pagas caro — não violar)

1. **Lei da fase de criação/deleção**: TUDO que cria entidades reais (ou marca `Deleted`) deve rodar
   **antes de `Modification1`**. Consumidores nativos rodam Mod1–Mod4 e `CleanUpSystem` apaga
   `Created/Updated/Applied` (e destrói `Deleted`) no FIM DO MESMO frame. Violação = "casca"
   invisível (rua sem malha/bloco) ou **crash nativo atrasado** (delete sem limpeza → referências
   penduradas). Fases atuais: applies de criação/edição/rota/área antes de Mod1; Policy em Mod3;
   demais em Mod5; senders/pause/cursor em Rendering.
2. **Definições `Permanent`**: caminho programático vanilla (ZoneSpawnSystem/BuildingConstruction)
   = `CreationDefinition{Permanent}` + curso/nós + `Updated`, destruídas no OnUpdate seguinte.
   NUNCA injetar definição em Mod5 (a query dos Generate exige `Updated`, que morre no frame).
3. **Eco**: cada feature tem guard próprio — `CS2M_RemotePlaced` (entidades), `RemoteNetEcho`
   (hash XZ-only! o jogo re-ajusta Y ao terreno), `ZoneEcho` TTL (o jogo recalcula células vizinhas
   1 frame depois), snapshot-refresh (tax/budget/policy), hash de waypoints (rotas), chaves de
   delete (rotas). Buffer ECS: **structural change invalida `DynamicBuffer` — `AddComponent` ANTES
   de `GetBuffer`** (bug do ping-pong de zona).
4. **Identidade**: nunca mandar `Entity` no fio. SyncId (nonce<<40|contador) p/ criados em sessão;
   prefab+posição p/ nativos; prefab+`RouteNumber` p/ linhas de save.
5. **Papel**: fonte confiável é `LocalPlayer.PlayerType`; `Command.CurrentRole` é espelhado no
   `PlayerTypeChanged` (upstream nunca atribuía — matou todos os senders na v37).
6. **Relay**: host carimba `SenderId = peer.Id + 1` e re-serializa para os DEMAIS peers quando
   `handler.RelayOnServer` (Preconditions/WorldTransfer/Resync = false).
7. **MessagePack**: modelo = `BetterGraphOf(CommandBase, [CS2M.API, CS2M])` — ordem de registro
   define IDs; ambos os lados usam o mesmo walk. Shims: `MessagePack.UnityShims` referenciado com
   `Aliases="msgpackshims"` (colide com `UnityEngine.Color32`); `CommandInternal`/`ApiCommand` usam
   `extern alias msgpackshims`.
8. **NUNCA lançar `Cities2.exe` com DLL do mod no CWD** (Mono sonda o CWD → registro de mods quebra
   com `NotSupportedException`). Lançar sempre da pasta do jogo.
9. **Comando ruim não pode matar sistema**: todo drain de fila tem `try/catch` `[Guard]`.
10. **NUNCA usar `updateSystem.UpdateBefore<A, B>(phase)` (overload de 2 tipos)**: ele REGISTRA o
    sistema A uma 2ª vez (ancorado em B) → A atualiza 2×/frame. Com estado por frame
    (`_pendingDefinitions`!) a 2ª execução destrói as definições injetadas ANTES dos consumidores
    → foi o crash do selftest v50. Ordem entre nossos applies = resolver com defer/retry interno.
11. **Rotas sincadas: SEM `Applied`, com `RouteBufferIndex = -1`**: o `RouteBufferSystem`
    (render) só inicializa buffers de chunks `Created && !Applied`; com Applied a rota fica com o
    index default 0 apontando pro buffer de OUTRA linha → NRE/corrupção no renderer (Critical no
    Player.log que passava despercebido desde a v49 — o jogo cata e segue, mas mata o render).
    Sempre checar o Player.log por "CRITICAL" além do CS2M.log.
12. **Alvos de teste do selftest**: prédio plopado em solo sem zona é CONDENADO e demolido pela
    sim ~40-60 s depois — testes tardios (fire) usam prédios NATIVOS do save.
13. **Sub-elementos NUNCA re-sincam a própria morte** (campo v50.2): quando um prédio morre, suas
    sub-nets e sub-áreas cascateiam `Deleted` — detectores de delete DEVEM excluir `Owner` (nets)
    ou exigir owner VIVO (áreas: delete de work-area com owner vivo é edição real e sinca). Sem
    isso, a sim do host demolindo abandonados re-enviou 297 "deletes de rua" + 734 de área
    endereçados por posição, que rasgaram ruas/canos/campos REAIS nos outros PCs (espiral: água
    quebrada → mais abandono → mais demolição).
14. **Rebuilds de interseção duplicam** (campo v50.2): o vanilla deleta+recria edges vizinhos ao
    mexer num nó; a supressão v39 não pega todos → o receptor tem guard `CoveredByExistingEdge`
    (início+meio+fim do novo sobre curva existente do mesmo prefab ≤1,5 m → SKIP covered).
15. **Attach só com hint explícito** (campo v50.2): growables compartilham a flag RoadSide com
    paradas — o gate por flag attachou 30 casas/lojas a ruas (demolir a rua demoliria o prédio).
    O detector só preenche OwnerX/Z quando o original TINHA `Attached`; o apply só attacha quando
    o hint veio (e nunca em Source==1/growable).

---

## 3. Validação (o tripé + como rodar)

### Build (da raiz do repo)
```bash
GAME="C:/JogosCrackeados/Cities.Skylines.II.v1.5.3f1/game/Cities2_Data"
TC="$GAME/Content/Game/.ModdingToolchain"
export CSII_TOOLPATH="$TC" CSII_MANAGEDPATH="$GAME/Managed" \
  CSII_MSCORLIBPATH="$GAME/Managed/mscorlib.dll" CSII_ASSEMBLYSEARCHPATH="$GAME/Managed" \
  CSII_USERDATAPATH="C:/Users/Bruno/AppData/LocalLow/Colossal Order/Cities Skylines II" \
  CSII_LOCALMODSPATH="C:/Users/Bruno/AppData/LocalLow/Colossal Order/Cities Skylines II/Mods" \
  CSII_UNITYMODPROJECTPATH="$TC/UnityModsProject" CSII_MODPOSTPROCESSORPATH="$TC/ModPostProcessor" \
  CSII_MODPUBLISHERPATH="$TC/ModPublisher" CSII_ENTITIESVERSION="1.3.10" CSII_UNITYVERSION="6000.3.2f1"
dotnet build CS2M/CS2M.csproj -c Release -p:AssemblyVersion=1.0.N.0 -p:FileVersion=1.0.N.0 -p:Version=1.0.N.0
# deploy: copiar CS2M.dll / CS2M.API.dll / CS2M.BaseGame.dll de CS2M/bin/Release/net48 → Mods/CS2M
# UI (mjs/css): cd CS2M.UI && npm run build   (sai direto na pasta do mod)
# bump: manter <Version> no csproj igual ao release (builds manuais identificáveis)
```

### 1) Protocolo (sem jogo, ~10 s) — `cd tests/protocol && dotnet run -c Release` → **35/35**.

### 2) Selftest in-game (1 instância, ~9 min) — 28 checks
```bash
cd "C:/JogosCrackeados/Cities.Skylines.II.v1.5.3f1/game"   # CWD = pasta do jogo (lei 8!)
export CS2M_AUTOPILOT=selftest CS2M_AP_TEST=1
./Cities2.exe -continuelastsave
# aguardar "scripted test DONE" no CS2M.log; resultados: grep "\[Auto\] RESULT"
```
Passos 0-26: role, money, xp, tree, building, move, zone, net(real-build: composição+conexão+
blocos), net-delete, net-upgrade(estrada!), delete, tax, budget, district, water, terrain, policy,
pause(freeze por frameIndex)+sabotagem+resume, devtree, env+native-delete, tile, **route**,
**stop-attach** (parada roadside atada ao edge), **fire-start** + **fire-collapse** (OnFire
espelhado; colapso via evento Destroy → teardown vanilla).
Validações NÃO-circulares (efeito real do mundo, nunca o valor escrito).

### 3) `/validate` no chat — mesma suíte em sessão aberta, PASS/FAIL no chat (modifica a cidade!).
(v50 fix: o /validate estava PRESO no passo 19 desde a v40 — pulava devtree/env/tile/route em
silêncio; agora acompanha a suíte completa.)

### 4) cs2m-bot (`tools/bot`, net48) — cliente headless com o protocolo REAL
```bash
cd tools/bot && dotnet build -c Release
# host: CS2M_AUTOPILOT=host CS2M_AP_TEST=0 ./Cities2.exe -continuelastsave  (da pasta do jogo)
bin/Release/net48/cs2m-bot.exe --ip 127.0.0.1 --port 1111 --user Bot --mode act|listen \
  [--latency 150 --loss 5]
```
`act` = constrói árvore+rua+deleta+chat; `listen` = assere relay de OUTRO cliente (sender≠0).
Auto-calibra preconditions pelo eco de erro do servidor. Bateria v49: regressão 25/25 + stress
4-jogadores (2 acts simultâneos, 1 com lag 150 ms/5% loss, 1 listen) = tudo PASS, zero guards.

---

## 4. Roadmap — TODOS OS ITENS ACORDADOS ENTREGUES NA v50 ✅

1. ✅ Paradas/objetos roadside (o "gap das paradas" real: linha em rua virgem já sincava desde a
   v49 — waypoint sem conexão É a parada lógica; o que faltava eram os stop-OBJECTS colocáveis).
2. ✅ Ping no mapa (`/ping`) + painel de jogadores (faixa no chat + `/players`).
3. ✅ Incêndios host-autoritativos (+ garimpo: weather damage/raios, condemned e abandoned
   teardown também viraram decisão do host).
4. ✅ Auto-reconexão com resync automático (re-join = world transfer).
5. ✅ Aviso "salvou o jogo"; ✅ custo de tile espelhado.

Avaliados e conscientemente NÃO feitos:
- Chirper compartilhado (ruído alto, valor baixo), fantasma de câmera (cursor já cobre 90%).
- Quadtree nas buscas O(n): as buscas rodam só em apply de clique humano (~ms por evento em
  cidade grande) — risco de bug > ganho. Reavaliar só se aparecer hitch real em campo.

## 5. Dívidas/notas conhecidas
- **v50 não validada em campo ainda** — validar com os 3 jogadores: paradas roadside, fogo
  host-auth (por padrão ON; `CS2M_FIRE_SYNC=0` desliga), auto-reconexão (derrubar wifi de um
  cliente no meio do jogo), painel/ping.
- Move de nativos: v48 (pré-move via Temp) — validar em campo.
- Growables host-auth: EXPERIMENTAL — se duplicar prédio/lote teimoso, `CS2M_GROWABLE_SYNC=0`.
- Bombeiros do CLIENTE não despacham (dispatch suprimido; fogo é 100% do host) — cosmético.
- Selftest roda com save carregado via `-continuelastsave` (autosaves de runs sem mod são ok).
- ilspycmd: sempre `-t Tipo` (nunca `-o`); dumps de decompile antigos em pasta temp de sessão.

## 6. Ferramentas do jogo × cobertura (auditoria v50)
Object✅ Net✅ Zone✅ Terrain✅ Water✅ Bulldoze(obj/net/área)✅ Area(distrito/trabalho/superfície)✅
Upgrade(replace)✅ Route✅ Stops/roadside✅ Selection/Default n/a. UI: tax✅ budget✅
policy(3 escopos)✅ devtree✅ tiles✅(+custo) loans✅ rename✅ ping✅ painel✅ — sem pendências
conhecidas de ferramenta/UI. Sim-driven: growables✅(exp) fire✅ weather-damage✅ condemned✅
abandoned✅ — não-sincável por design: cidadãos/veículos/tráfego individuais.
