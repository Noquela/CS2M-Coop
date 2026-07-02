# CS2M-Coop — Documento de Estado (v1.0.49.0)

> **Este é o documento canônico do projeto.** Ele existe para que qualquer pessoa (ou sessão de IA
> com contexto compactado) reconstrua o estado inteiro do fork a partir daqui. Última revisão
> completa: 2026-07-02, release v1.0.49.0 (`51daac9`).

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
| Zip de release | `C:\Users\Bruno\Desktop\CS2M_v49.zip` (conteúdo da pasta do mod sem `.claude`) |

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
| Água/terreno | `WaterSourceData` direto; brush replay best-effort (drift → `/resync`) | fork inicial |
| Tiles do mapa | centro do tile → `RemoveComponent<Native>` (espelho vanilla) | v44 |
| Extensões de serviço | objeto com `Owner`; `ServiceUpgradeSystem` fia `InstalledUpgrade` sozinho | v44 |
| **Growables host-autoritativos** (EXPERIMENTAL) | host sinca spawns do sim; cliente desliga `ZoneSpawnSystem`; level-up = delete-por-id + spawn; spawn limpa o lote no receptor. `CS2M_GROWABLE_SYNC=0` desliga | v44 |
| Renomear (prédio/distrito/linha) | `NameSystem` por identidade | v48 |
| **Linhas de transporte** | criar/re-rotear/deletar/cor/número; waypoints completos; paradas por SyncId/posição; receptor constrói dos `RouteData` archetypes; eco por hash de waypoints; número aplicado com delay de 3 frames (InitializeSystem numera antes) | v49 |
| Clima + relógio | overrides do `ClimateSystem` + realinhamento `TimeData.m_FirstFrame` (~0,5 Hz) | v42 |
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

### 1) Protocolo (sem jogo, ~10 s) — `cd tests/protocol && dotnet run -c Release` → **31/31**.

### 2) Selftest in-game (1 instância, ~8 min) — 25 checks
```bash
cd "C:/JogosCrackeados/Cities.Skylines.II.v1.5.3f1/game"   # CWD = pasta do jogo (lei 8!)
export CS2M_AUTOPILOT=selftest CS2M_AP_TEST=1
./Cities2.exe -continuelastsave
# aguardar "scripted test DONE" no CS2M.log; resultados: grep "\[Auto\] RESULT"
```
Passos 0-23: role, money, xp, tree, building, move, zone, net(real-build: composição+conexão+
blocos), net-delete, net-upgrade(estrada!), delete, tax, budget, district, water, terrain, policy,
pause(freeze por frameIndex)+sabotagem+resume, devtree, env+native-delete, tile, **route**.
Validações NÃO-circulares (efeito real do mundo, nunca o valor escrito).

### 3) `/validate` no chat — mesma suíte em sessão aberta, PASS/FAIL no chat (modifica a cidade!).

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

## 4. Roadmap acordado (próximos, em ordem)

1. **Paradas de ônibus criadas durante o desenho da linha** — verificar se a parada nova existe no
   receptor; se não, sincar criação de `TransportStop` (fecha o transporte 100%).
2. **Ping no mapa** ("olha aqui") + **painel de jogadores** (online/cor/ping) — coop-feel.
3. **Incêndios/desastres host-autoritativos** — ignição sincada, cliente suprime aleatórias
   (mata a última fonte grande de drift de nativos).
4. **Auto-reconexão** com resync automático.
5. Menores: aviso "host salvou", chirper compartilhado, fantasma de câmera, otimizar buscas O(n)
   com o quadtree do jogo (`SearchSystem`), custo de tile espelhado no host.

## 5. Dívidas/notas conhecidas
- Cost de tiles não espelhado (cliente compra "de graça" no saldo do host) — anotado no código.
- Move de nativos: v48 (pré-move via Temp) — validar em campo.
- Growables host-auth: EXPERIMENTAL — se duplicar prédio/lote teimoso, `CS2M_GROWABLE_SYNC=0`.
- Selftest roda com save carregado via `-continuelastsave` (autosaves de runs sem mod são ok).
- ilspycmd: sempre `-t Tipo` (nunca `-o`); dumps de decompile antigos em pasta temp de sessão.

## 6. Ferramentas do jogo × cobertura (auditoria v49)
Object✅ Net✅ Zone✅ Terrain✅ Water✅ Bulldoze(obj/net/área)✅ Area(distrito/trabalho/superfície)✅
Upgrade(replace)✅ Route✅ Selection/Default n/a. UI: tax✅ budget✅ policy(3 escopos)✅ devtree✅
tiles✅ loans✅ rename✅ — pendente: paradas novas de linha (item 1 do roadmap).
