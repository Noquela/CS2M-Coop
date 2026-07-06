# Cidade via UI (distritos, milestones, map tiles, signature, dev tree, renomear, ações) — dossiê de sync

> Fonte: `decomp/Game/Game/UI/InGame/{DistrictsSection,MilestoneUISystem,MapTilesUISystem,SignatureBuildingUISystem,DevTreeUISystem,ActionsSection,TitleSection}.cs`
> + sistemas de simulação que essas telas acionam (`Game.Simulation.MilestoneSystem`, `Game.City.DevTreeSystem`,
> `Game.Simulation.MapTilePurchaseSystem`, `Game.Tools.SelectionToolSystem`) + mod em `CS2M/Sync/*`.

## 1. Entradas do jogador

Esta mecânica não é UMA ferramenta — é um conjunto de painéis de UI que disparam `TriggerBinding`s
diretamente sobre entidades/singletons, sem passar pelo pipeline padrão de ferramenta
(`CreationDefinition`/`Temp`/`Applied`). Cada painel tem sua própria entrada:

| Painel | Trigger | O que dispara |
|---|---|---|
| Distritos (painel do prédio) | `toggleSelectionTool` (`DistrictsSection.cs:100-112`) | ativa `SelectionToolSystem` com `selectionType=ServiceDistrict`, `selectionOwner=selectedEntity` |
| Distritos (painel do prédio) | `removeDistrict` (`DistrictsSection.cs:79-99`, método espelho `RemoveServiceDistrict` em `:206-226`) | remove item da lista de distritos atendidos, direto no buffer `ServiceDistrict` |
| Distritos (painel do prédio) | `toggleDistrictTool` (`DistrictsSection.cs:113-125`) | ativa `AreaToolSystem` com o prefab de distrito padrão — **isto é o desenho/edição de distrito, já coberto por `DistrictCommand`, ver §5** |
| Milestones | nenhuma ação do jogador que mute mundo — `clearUnlockedMilestone` (`MilestoneUISystem.cs:316-326`) só fecha o popup local | avanço é 100% emergente (XP), ver §2 |
| Dev Tree | `purchaseNode` (`DevTreeUISystem.cs:290-293`) | `m_DevTreeSystem.Purchase(node)` |
| Map Tiles | `purchaseMapTiles` (`MapTilesUISystem.cs:131-134`) | `m_MapTileSystem.PurchaseSelection()` |
| Signature Buildings | `removeUnlockedSignature` (`SignatureBuildingUISystem.cs:39-46`) | só faz `RemoveAt(0)` da fila LOCAL de popups — nenhuma mutação de mundo |
| Ações (painel de qualquer entidade) | `toggleMove` (`ActionsSection.cs:163-177`) | `m_ObjectToolSystem.StartMoving(selectedEntity)` — relocar |
| Ações | `delete` (`ActionsSection.cs:191-205`, espelho `OnDelete` em `:335-349`) | `AddComponent<Deleted>(selectedEntity)` direto, sem tool — demolir prédio/distrito/rota via painel |
| Ações | `toggle` (`ActionsSection.cs:206-216`) | liga/desliga a policy "Out of Service" (rota ou prédio) |
| Ações | `toggleEmptying` (`ActionsSection.cs:217-220`) | liga/desliga a policy "Empty" (esvaziar depósito de lixo/etc) |
| Ações | `toggleLotTool` (`ActionsSection.cs:221-233`) | ativa `AreaToolSystem` no lote editável do prédio — mesmo mecanismo do `AreaEditCommand` |
| Ações | `focus`/`follow`/`toggleTrafficRoutes` | câmera/UI, sem mutação de mundo persistida relevante (ver §4) |
| Título (painel de qualquer entidade) | `renameEntity` (`TitleSection.cs:40-44`) | `m_NameSystem.SetCustomName(selectedEntity, newName)` — renomear prédio/distrito/rota/cidade |

## 2. Fluxo de aplicação

### 2.1 Distritos — atendimento por serviço (`ServiceDistrict`)
Distinto de PINTAR um distrito (que é `AreaToolSystem`, já coberto — ver §5). Aqui o jogador
restringe QUAIS distritos um prédio de serviço atende:
1. `DistrictsSection.toggleSelectionTool` põe `SelectionToolSystem.selectionType = ServiceDistrict`
   e `selectionOwner = selectedEntity` (o prédio) — `DistrictsSection.cs:100-112`.
2. O jogador clica distritos no mapa; `SelectionToolSystem` mapeia `ServiceDistrict` para
   `AreaType.District` (`SelectionToolSystem.cs:716-724`) e, com o tool ativo, agenda
   `CopyServiceDistricts`/`UpdateServiceDistricts` a cada update (`SelectionToolSystem.cs:1021-1103`).
3. `CopyServiceDistrictsJob` (`SelectionToolSystem.cs:341-384`) copia o buffer `ServiceDistrict` do
   prédio-dono para o buffer de trabalho `SelectionElement` da entidade de seleção (staging).
4. `UpdateServiceDistrictsJob` (`SelectionToolSystem.cs:387-421`) faz o caminho inverso: reescreve o
   buffer `ServiceDistrict` do prédio a partir do `SelectionElement` staged, e marca o prédio
   `Updated` (`:418`).
5. Alternativa de remoção direta: `DistrictsSection.RemoveServiceDistrict`
   (`DistrictsSection.cs:206-226`) itera o buffer `ServiceDistrict` do `selectedEntity` e chama
   `buffer.RemoveAt(i)` sem passar pela ferramenta.
6. Consumidor: `Game.Simulation.ServiceCoverageSystem` lê `BufferLookup<ServiceDistrict>` para
   filtrar quais distritos aquele prédio de serviço cobre (`ServiceCoverageSystem.cs:189,217`) — é
   isso que decide, por exemplo, se uma escola aceita crianças de um distrito específico.

### 2.2 Milestones — avanço é emergente, não uma "entrada do jogador"
1. `Game.Simulation.MilestoneSystem.OnUpdate` (`MilestoneSystem.cs:66-84`) roda todo frame, compara
   `m_CitySystem.XP` (emergente, acumulado por eventos de simulação) contra o XP exigido pelo
   próximo `MilestoneData`.
2. Ao cruzar o limiar, incrementa o singleton `MilestoneLevel.m_AchievedMilestone`
   (`MilestoneSystem.cs:77-78`) e chama `NextMilestone` (`:86-108`), que cria um
   `MilestoneReachedEvent` + um evento `Unlock` (via `ModificationEndBarrier`, `:88-94`) e credita
   `PlayerMoney`/`Creditworthiness` do reward do milestone (`:95-100`).
3. O evento `Unlock` é consumido em outro lugar (não localizado neste dossiê — ver §7) para
   habilitar (`enabled=false` no componente `Locked`) os serviços/prefabs daquele milestone; a
   própria UI (`MilestoneUISystem.cs:502-517`) só reage a `MilestoneReachedEvent`/`Locked` mudando
   para atualizar bindings, não muta mundo.
4. `Game.City.DevTreeSystem.OnUpdate` (`DevTreeSystem.cs:128-143`) escuta o mesmo
   `MilestoneReachedEvent` via `AppendPointsJob` e credita `DevTreePoints.m_Points`
   (`DevTreeSystem.cs:31-43`).

### 2.3 Dev Tree — compra de nó
1. `DevTreeUISystem.purchaseNode` chama `m_DevTreeSystem.Purchase(node)` (`DevTreeUISystem.cs:290-293`).
2. `DevTreeSystem.Purchase(Entity node)` (`DevTreeSystem.cs:154-172`) valida custo ≤ pontos
   disponíveis, nó ainda `Locked` habilitado, serviço-pai desbloqueado e requisitos (nós
   pré-requisito) já desbloqueados; se ok, decrementa `points` (o singleton `DevTreePoints`,
   `:164-165`), cria uma entidade `Unlock(node)+Event` via `EndFrameBarrier`
   (`:166-168`) — o mesmo padrão de evento do milestone.
3. Esse evento `Unlock` é o que, em algum sistema consumidor não localizado aqui, desabilita
   `Locked` do nó (ver §7 — o mod assume isso e replica manualmente, ver §5).

### 2.4 Map Tiles — compra de tile
1. `MapTilesUISystem.purchaseMapTiles` chama `m_MapTileSystem.PurchaseSelection()`
   (`MapTilesUISystem.cs:131-134`).
2. `MapTilePurchaseSystem.PurchaseSelection()` (`MapTilePurchaseSystem.cs:311-344`): recalcula
   `UpdateStatus()`, debita `PlayerMoney.Subtract(cost)` direto no singleton da cidade
   (`:318-320`), depois para cada `SelectionElement` staged chama `UnlockTile(entityManager, area)`
   (`:332`).
3. `UnlockTile` (`MapTilePurchaseSystem.cs:346-353`) é a mutação real: remove `Native` do
   `MapTile`/`Area` e adiciona `Updated` — isso libera o tile pra construção (o mesmo padrão usado
   por `UnlockMapTiles()` no cheat "desbloquear tudo", `:297-309`).

### 2.5 Ações do painel — relocar / demolir / políticas
- **Relocar** (`toggleMove`): ativa `ObjectToolSystem.StartMoving(selectedEntity)`
  (`ActionsSection.cs:163-177`) — cai no fluxo padrão do `ObjectToolSystem` em modo `Move`
  (`ObjectToolSystem.Mode.Move`, referenciado em `ActionsSection.cs:156,243,264,281`), que ao soltar
  gera `Temp`→`Applied` como qualquer reposicionamento de objeto.
- **Demolir via painel** (`delete`): `AddComponent<Deleted>(selectedEntity)` direto via
  `m_EndFrameBarrier.CreateCommandBuffer()` (`ActionsSection.cs:191-205`) — SEM passar pelo
  `BulldozeToolSystem`. `deletable` (`ActionsSection.cs:397`) é true quando a entidade tem
  `Game.Areas.District`, OU `Game.Buildings.ServiceUpgrade` (extensão instalada), OU
  (`TransportLine` + `Route`) — ou seja, este botão é o único caminho pra apagar um distrito, uma
  extensão de prédio (asa de hospital etc.) ou uma linha de transporte pelo painel de informação.
- **Políticas** (`toggle`/`toggleEmptying`): `m_PoliciesUISystem.SetSelectedInfoPolicy(...)`
  (`ActionsSection.cs:206-220`) — liga/desliga a policy "Out of Service"/"Empty" na entidade
  selecionada (prédio, rota ou extensão).

### 2.6 Renomear (`TitleSection`)
`m_NameSystem.SetCustomName(selectedEntity, newName)` (`TitleSection.cs:40-44`) — funciona para
qualquer entidade selecionável: prédio, distrito, veículo, linha de transporte, cidadão, animal
(ver `GetVirtualKeyboardLocaleKey`, `TitleSection.cs:88-123`, que resolve o tipo pelo componente
presente). O nome da CIDADE em si NÃO é uma entidade — é a propriedade `cityName` de
`CityConfigurationSystem` (`CityConfigurationSystem.cs:70`), setada em outro fluxo (não localizado
neste dossiê, provavelmente um painel diferente do city info) mas serializada no save
(`CityConfigurationSystem.cs:20,269,322`).

### Fase / ordenação
Nenhum destes arquivos preservou atributos `[UpdateInGroup]`/`[UpdateBefore]`/`[UpdateAfter]` na
decompilação (`MilestoneSystem.cs`, `DevTreeSystem.cs`, `MapTilePurchaseSystem.cs`,
`SelectionToolSystem.cs` — grep por esses atributos não achou nada nestes arquivos). O que dá pra
afirmar com citação: são todos `GameSystemBase`/`UISystemBase`/`InfoSectionBase` (não
`ToolBaseSystem`), e a maioria faz a mutação DIRETO no `OnUpdate`/método público chamado pelo
binding — sem passar por `CreationDefinition`+`Temp`+`Applied` do pipeline de Modification1-5
descrito em `GAME_SYSTEMS.md:38-59`. Exceção: `toggleMove`/`toggleDistrictTool`/`toggleLotTool`
ativam ferramentas de verdade (`ObjectToolSystem`/`AreaToolSystem`) que SEGUEM o pipeline padrão de
fases. A fase exata de `MilestoneSystem`/`DevTreeSystem`/`MapTilePurchaseSystem` fica em
**NÃO VERIFICADO** (§7).

## 3. Estado persistido tocado

| Componente/campo | `ISerializable`? | Onde |
|---|---|---|
| `Game.Areas.ServiceDistrict` (buffer, por prédio) | sim, `ISerializable` | `Game/Areas/ServiceDistrict.cs:8-36` |
| `Game.City.MilestoneLevel.m_AchievedMilestone` | sim | `Game/City/MilestoneLevel.cs:6-19` |
| `Game.City.DevTreePoints.m_Points` | sim | `Game/City/DevTreePoints.cs:6-19` |
| `Game.Prefabs.Locked` (enableable, por prefab-instância de serviço/nó/zona/policy) | (não li o arquivo do componente neste dossiê — presumido `IEnableableComponent`; ver §7) | referenciado em `DevTreeUISystem.cs:43,220`, `MilestoneUISystem.cs:306` |
| `Game.Areas.District.m_OptionMask` | sim | `Game/Areas/District.cs:6-19` (já coberto por `DistrictCommand`, §5) |
| `Game.Areas.MapTile` (tag, `IEmptySerializable`) + ausência/presença de `Native` | sim (vazio) | `Game/Areas/MapTile.cs:8-10` |
| `Game.City.PlayerMoney` (débito da compra de tile/milestone reward) | fora de escopo deste dossiê — já é `HostAuthoritative` (`MoneySyncCommand`) |
| `Game.UI.CustomName` (rename) | usado por `RenameDetectorSystem` (mod), estrutura do componente não lida aqui |
| `CityConfigurationSystem.cityName` (nome da cidade) | sim, serializado pelo próprio sistema | `CityConfigurationSystem.cs:20,269,322` |
| `Game.Citizens.Followed.m_Priority/m_StartedFollowingAsChild` | sim | `Game/Citizens/Followed.cs:6-29` — tocado por `ActionsSection.follow` (`:178-190`); ver §6/§7 sobre se isso DEVERIA sincar |

## 4. Perigos cross-machine

1. **Bypass total do pipeline de `Temp`/`Applied`.** Toda a família "Ações do painel"
   (`delete`, `toggle`, `toggleEmptying`) e a compra de Dev Tree/Map Tile mutam componentes
   DIRETO (`AddComponent<Deleted>`, `SetSingleton`, `RemoveComponent<Native>`) sem passar por
   `CreationDefinition`. Isso significa que **não existe nenhum sinal de "isto é uma ação do
   jogador" que o detector genérico de ferramenta enxergue** — cada mecânica precisa do próprio
   detector poll-based (é exatamente o padrão que o mod já usa: `DevTreeDetectorSystem`,
   `TileDetectorSystem`, `RenameDetectorSystem` fazem diff por hash/snapshot a cada N frames, não
   escutam uma tag `Applied`). Qualquer mecânica nova nesta família herda o mesmo risco: se
   ninguém escrever um detector específico, a mutação é 100% silenciosa pro sync.
2. **Resolução por proximidade em pelo menos 3 pontos confirmados:**
   - `DistrictApplySystem.FindDistrictByCenter` — raio de 40 m (`bestD = 1600f`) por prefab+centroide
     (`CS2M/Sync/DistrictApplySystem.cs:140-194`);
   - `AreaEditApplySystem.FindAreaByCenter` — raio de 10 m (`bestD = maxDistSq`, chamado com 100f em
     `ApplyDelete`) por prefab+centroide, usado inclusive pra apagar distritos
     (`CS2M/Sync/AreaEditSystems.cs:804-868,870-886`, comentário explícito "districts included");
   - `TileApplySystem.ApplyOne` — raio de 10 m por posição de grid fixo
     (`CS2M/Sync/TileSyncSystems.cs:206-224`).
   Nenhum destes usa `CS2M_SyncId`; funcionam hoje porque distritos/tiles/áreas de trabalho são
   espaçados o bastante, mas é uma violação do princípio "nunca por proximidade" se dois distritos
   do mesmo prefab ficarem a menos de ~10-40 m um do outro (raro, mas possível em mapas pequenos ou
   com muitos distritos pequenos).
3. **`MilestoneLevel`/`DevTreePoints`/reward de milestone são calculados independentemente em CADA
   máquina**, não replicados por comando: o mod (`ProgressionApplySystem`) só espelha o `XP` bruto
   do host pro cliente e deixa o `MilestoneSystem` LOCAL de cada máquina recalcular o milestone e
   creditar `PlayerMoney`/`Creditworthiness` (`MilestoneSystem.cs:86-108`) — ver §5. Isso funciona
   SE o XP mirrorado chega e o `MilestoneData` (limiares, custo, reward) é idêntico nos dois PCs
   (prefab estático, não deveria divergir), mas há uma janela de corrida: se o cliente cruza o
   limiar localmente ANTES do próximo pacote de XP do host chegar (XP fica temporariamente
   dessincronizado entre pacotes, ~1.5s de intervalo — `ProgressionSenderSystem.cs:19`), o cliente
   pode creditar o reward de um milestone que o host ainda não creditou, ou vice-versa — o dinheiro
   diverge até o próximo pacote `MoneySyncCommand` (host-autoritativo) corrigir.
4. **`Locked` é um componente ENABLEABLE por-prefab-instância** (não um valor no fio) — tanto
   milestones quanto dev-tree nodes o usam (`DevTreeUISystem.cs:43,220`, `MilestoneUISystem.cs:306`).
   O mod não sincroniza esse componente diretamente; em vez disso replica o EVENTO (`Unlock`) que o
   desbloqueia (ver §5). Isso é correto SE (e só se) o consumidor do evento `Unlock` (não localizado
   neste dossiê — §7) roda de forma determinística a partir do prefab, sem depender de ordem de
   iteração ou índice — **não verificado**.
5. **`ServiceDistrict` referencia `Entity` de distrito diretamente no buffer** (`m_District`,
   `ServiceDistrict.cs:10`) — mesmo problema estrutural de qualquer buffer que guarda `Entity`: o
   `.Index` de uma entidade de distrito não é garantido igual entre máquinas
   (`GAME_SYSTEMS.md:78-90`), então mesmo que o CS2M viesse a sincronizar esse buffer, teria que
   traduzir por identidade estável (nome do prefab de distrito + centroide, como já faz
   `DistrictApplySystem`), nunca mandar o `Entity` cru.
6. **`Followed` é per-citizen e persiste no save** (`Followed.cs:6-29`, inclusive com dado
   `m_StartedFollowingAsChild` ligado à conquista "stalker"). Se dois jogadores seguirem cidadãos
   diferentes, cada máquina tem um citizen com `Followed` habilitado que o outro PC não tem — isso É
   uma divergência de estado persistido real, mesmo que pareça só câmera. Não investiguei se isso
   quebra alguma coisa prática (achievement de progresso, por exemplo) — ver §7.

## 5. O que o CS2M faz hoje

- **`DistrictCommand`** (`CS2M/Sync/DistrictDetectorSystem.cs` + `DistrictApplySystem.cs`): cobre
  pintar/redesenhar (`Replace=true`) o POLÍGONO do distrito e seu `m_OptionMask`, endereçado por
  prefab+centroide. Comentário explícito no apply: *"District names are UI-managed, not synced
  here"* (`DistrictApplySystem.cs:18`) — nome fica pro `RenameCommand`.
- **`AreaEditCommand`** (`CS2M/Sync/AreaEditSystems.cs`): cobre criação/edição/DELEÇÃO de áreas
  possuídas (campos de fazenda/floresta/extração) e de áreas standalone (superfícies pintadas), e
  também cobre a DELEÇÃO de distritos (mesma query, sem excluir `District` — comentário "districts
  included" em `:804-805`). Ou seja: apagar um distrito pelo botão `delete` do painel de Ações
  CAI nesta mesma detecção (`_deletedAreas`, `AreaEditSystems.cs:143-158`), não precisa de tratamento
  especial — **confirmado coberto**.
- **`TilePurchaseCommand`** (`CS2M/Sync/TileSyncSystems.cs`): detecta tile que perdeu `Native` por
  diff de conjunto a cada 2s, envia centro do(s) tile(s) comprado(s) + custo; aplica removendo
  `Native`/`Updated` no tile mais próximo (grid fixo, raio 10m) e debita `PlayerMoney` só no host
  (`TileApplySystem.cs:247-264`) — espelha exatamente `MapTilePurchaseSystem.PurchaseSelection`+`UnlockTile`.
- **`DevTreeCommand`** (`CS2M/Sync/DevTreeDetectorSystem.cs` + `DevTreeApplySystem.cs`): detecta nó
  que ficou desbloqueado (`Locked` desabilitado) por diff a cada 30 frames; aplica recriando o MESMO
  evento `Unlock+Event` que `DevTreeSystem.Purchase` cria (`DevTreeApplySystem.cs:66-69`) e espelha a
  dedução de pontos (`:71-80`) — pula as checagens de custo/requisito porque assume que o lado
  remetente já validou.
- **`ProgressionSyncCommand`** (`CS2M/Sync/ProgressionApplySystem.cs` + `ProgressionSenderSystem.cs`):
  HOST manda XP bruto (`XP.m_XP/m_MaximumPopulation/m_MaximumIncome/m_XPRewardRecord`) a cada ~1.5s
  (ou em mudança); cliente escreve esse XP no `City` local e deixa o `MilestoneSystem` local avançar
  sozinho — explicitamente NÃO injeta o evento de milestone pra evitar creditar pontos em dobro
  (comentário em `ProgressionApplySystem.cs:11-14`).
- **`RenameCommand`** (`CS2M/Sync/RenameSyncSystems.cs`): cobre prédio (`kind=1`, por `CS2M_SyncId`
  ou posição), distrito (`kind=2`, por centroide via `Geometry`), linha de transporte (`kind=3`, por
  `CS2M_SyncId` ou prefab+`RouteNumber`) e nome da CIDADE (`kind=4`, propriedade
  `CityConfigurationSystem.cityName`, diffada separadamente por não ser uma `CustomName` de
  entidade) — **as 4 formas de renomear expostas por `TitleSection` estão cobertas**.
- **Relocar (`toggleMove`)**: coberto por `MoveCommand`/`MoveDetectorSystem` — inclusive o caso
  específico de extensão/upgrade de serviço possuída (`Owner`-bearing), adicionado na v55
  (`CS2M/Sync/MoveDetectorSystem.cs:35`, comentário "installed service upgrades being relocated").
- **Demolir prédio/linha via painel (`delete`)**: `DeleteCommand`/`DeleteDetectorSystem` cobre
  objetos com `CS2M_SyncId` (`_deletedQuery`), objetos nativos por prefab+posição
  (`_deletedNativeQuery`) e linhas de transporte especificamente (`_deletedRouteQuery`,
  comentário explícito "the info panel deletes them with a bare AddComponent<Deleted>" —
  `DeleteDetectorSystem.cs:59-60`).
- **Políticas (`toggle`/`toggleEmptying`)**: `PolicyCommand`/`PolicyDetectorSystem` — diff do buffer
  `Policy` da cidade + eventos `Modify` pra prédio/distrito (comentário v46,
  `PolicyDetectorSystem.cs:34-35`).

## 6. GAPS e recomendação

1. **`ServiceDistrict` (atendimento por distrito de um prédio de serviço) NÃO tem NENHUMA
   cobertura no mod** — busquei "ServiceDistrict" em todo `CS2M/Sync` e não há nenhum resultado.
   Mecânica confirmada: `DistrictsSection.toggleSelectionTool`/`removeDistrict`
   (`DistrictsSection.cs:79-125,206-226`) + `SelectionToolSystem`
   `CopyServiceDistrictsJob`/`UpdateServiceDistrictsJob` (`SelectionToolSystem.cs:341-421`) mutam um
   buffer `ISerializable` real (`ServiceDistrict.cs:8`) que `ServiceCoverageSystem` usa pra decidir
   cobertura de serviço (`ServiceCoverageSystem.cs:189,217`). Sem sync, um jogador pode restringir um
   hospital/escola/delegacia a atender só certos distritos e o outro PC continua vendo aquele prédio
   atendendo a cidade toda (ou vice-versa) — divergência de gameplay real, não só visual.
   **Checklist pra cobrir:** novo `ServiceDistrictCommand` (WorldContract) que carregue
   `OwnerSyncId`/prefab+posição do prédio (mesmo padrão de `RenameCommand.kind=1`) + lista de
   distritos por prefab-de-distrito+centroide (nunca `Entity` cru — perigo §4.5); detector faz diff
   do buffer por prédio a cada N frames (mesmo padrão de `AreaEditDetectorSystem`); apply resolve
   prédio-dono e cada distrito por identidade estável, reescreve o buffer inteiro (replace, não
   incremental) pra evitar drift de ordem.
2. **Deletar uma EXTENSÃO de prédio (`ServiceUpgrade`) pelo botão `delete` do painel de Ações NÃO é
   coberto**, apesar de mover a MESMA extensão ser coberto. Ambas as queries de
   `DeleteDetectorSystem` (`_deletedQuery` em `:38-57` e `_deletedNativeQuery` em `:79-99`) têm
   `None: Owner` explícito — e uma extensão SEMPRE tem `Owner` apontando pro prédio principal
   (confirmado pelo próprio mod: `PlacementDetectorSystem.cs:80-97` e `MoveDetectorSystem.cs:216-223`
   filtram extensões justamente por "`Applied` com `Owner`" e "prefab com `ServiceUpgradeData`/
   `BuildingExtensionData`"). Resultado: `deletable` inclui `Game.Buildings.ServiceUpgrade`
   (`ActionsSection.cs:397`), o jogador pode clicar "Delete" numa asa de hospital, e isso NUNCA
   chega ao outro PC — a extensão continua lá pro outro jogador, prédio principal e sub-objeto
   ficam permanentemente divergentes (a extensão "fantasma" no PC B nunca mais sincroniza porque
   nenhum detector a rastreia).
   **Checklist pra cobrir:** adicionar uma query espelho de `_deletedQuery` SEM o `None: Owner`, mas
   restrita a `ServiceUpgradeData`/`BuildingExtensionData` (mesmo filtro de prefab que
   `PlacementDetectorSystem`/`MoveDetectorSystem` já usam) — endereçar por `CS2M_SyncId` da própria
   extensão se ela tiver, senão por prefab+posição como o resto do `_deletedNativeQuery`.
3. **Corrida de crédito de milestone** (§4.3): host e cliente calculam o reward/loan-limit do
   milestone LOCALMENTE a partir do XP mirrorado, sem comando dedicado — plausível-mas-não-crítico
   porque `PlayerMoney` é host-autoritativo e corrige em ~1s, mas gera uma janela onde
   `PlayerMoney`/`Creditworthiness` divergem entre o crédito local e a correção do próximo pacote.
   Se isso incomodar na tela (jogador vê o dinheiro "pular"), a correção é o host mandar o evento de
   milestone explicitamente (client NÃO teria seu próprio `MilestoneSystem` avançando) em vez de só
   mirrorar XP — troca uma corrida por uma dependência mais forte de latência de rede.
4. **`Locked`/evento `Unlock` do dev-tree e do milestone**: o mod assume — sem eu ter localizado o
   sistema consumidor real de `Unlock` neste dossiê (§7) — que recriar `Unlock+Event` do lado
   remoto produz o mesmo efeito determinístico (desabilitar `Locked`, aplicar efeitos) que o
   `DevTreeSystem.Purchase` original produziria. Isso é plausível (mesmo padrão de evento, resolvido
   por prefab-name) mas **não foi verificado seguindo o consumidor até o fim**.
5. **`Followed` (seguir cidadão pela câmera, `ActionsSection.follow`)** não é sincronizado — parece
   proposital (é câmera pessoal de cada jogador), mas é tecnicamente um componente `ISerializable`
   que grava no save (`Followed.cs:6-29`, inclusive achievement flag). Não incluí isso como GAP
   priorizado porque não achei evidência de que precise convergir — ver §7.

## 7. NÃO VERIFICADO

- **Qual sistema consome o evento `Unlock` (criado por `MilestoneSystem.NextMilestone`,
  `DevTreeSystem.Purchase` e o cheat `MilestoneSystem.UnlockAllMilestones`)** para de fato desabilitar
  o componente `Locked` da entidade referenciada — não localizei o `*UnlockSystem*` (ou nome
  equivalente) no decomp lido neste dossiê. Sem isso, não posso confirmar 100% que a
  reprodução do evento pelo `DevTreeApplySystem`/mod produz efeito idêntico ao original em TODOS os
  casos (só verifiquei que o padrão do evento — `Unlock(node)+Event`, `EndFrameBarrier` vs criação
  direta — é estruturalmente o mesmo).
- **Estrutura interna do componente `Game.Prefabs.Locked`** (é `IEnableableComponent`? tem payload
  além do enable-bit?) — usado em várias checagens (`HasEnabledComponent<Locked>`) mas não abri o
  arquivo do componente.
- **Estrutura interna de `Game.UI.CustomName`** (ou namespace exato) usada por `RenameDetectorSystem`
  — só confirmei o uso via `NameSystem.SetCustomName`/`TryGetCustomName`, não o componente em si.
- **Onde exatamente o jogador edita `CityConfigurationSystem.cityName`** pela UI (não é
  `TitleSection` — é uma entidade separada sem `TitleSection`/`InfoSectionBase`; não localizei o
  painel de "city info" que expõe esse campo, presumo existir uma tela dedicada não coberta pelas
  pistas fornecidas).
- **Fase (`SystemUpdatePhase`) exata de `MilestoneSystem`, `DevTreeSystem`, `MapTilePurchaseSystem`,
  `SelectionToolSystem`** — os atributos `[UpdateInGroup]`/`[UpdateBefore]`/`[UpdateAfter]` não
  sobreviveram nesta decompilação (grep direto nos arquivos não achou nada); só posso afirmar que
  são `GameSystemBase` e que mutam via `OnUpdate`/métodos públicos chamados pelo binding, sem citar
  a fase do pipeline `PreSimulation→...→ModificationEnd`.
- **Se `Followed` precisa convergir entre máquinas** (achievement "stalker", ou qualquer sistema de
  simulação que leia esse componente além da câmera) — só confirmei que ele existe, é
  `ISerializable` e é tocado por `ActionsSection.follow`; não segui os leitores desse componente.
- **Onde o jogador escolhe QUAL signature building construir** dentre os desbloqueados
  (`SignatureBuildingUISystem` só gerencia a fila local de popups, `:39-99`) — presumo que a
  colocação real caia no `ObjectPlaceCommand` normal, mas não confirmei o botão/fluxo exato que leva
  da lista de `unlockedSignatures` até a ferramenta de colocação.
