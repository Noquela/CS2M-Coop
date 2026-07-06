# Rotas de transporte (linhas, paradas, agenda) — dossiê de sync

## 1. Entradas do jogador

O jogador desenha/edita uma linha com a `RouteToolSystem` (`decomp/Game/Game/Tools/RouteToolSystem.cs`),
uma máquina de estados com `State.Default/Create/Modify/Remove` (RouteToolSystem.cs:28-34) e um
`NativeList<ControlPoint> m_ControlPoints` acumulado clique a clique (RouteToolSystem.cs:1001,1785-1957).
Cada clique aplicado (`Apply`) ou solto (`Cancel`) roda dois jobs, sempre no mesmo frame do input:

- `SnapJob` (RouteToolSystem.cs:53-473): decide a que a ponta do traçado gruda — outro waypoint da
  mesma rota (raio `RouteData.m_SnapDistance`, RouteToolSystem.cs:183,197,213,287), uma parada
  compatível (`ValidateStop`, RouteToolSystem.cs:236-250, compara `TransportStopData.m_TransportType`)
  ou uma faixa de rede compatível (`CheckLaneType`, RouteToolSystem.cs:388-473, por `TransportType`/
  `RoadTypes`/`TrackTypes`/`SizeClass`).
- `CreateDefinitionsJob` (RouteToolSystem.cs:476-863): monta, a cada frame de update do tool, uma
  entidade descartável com `CreationDefinition` (RouteToolSystem.cs:564-566), `ColorDefinition`
  (RouteToolSystem.cs:567-568) e um buffer `WaypointDefinition` (RouteToolSystem.cs:597,653,740) —
  a lista ordenada de pontos da rota (posição + a que ela conecta, via `GetWaypointDefinition`,
  RouteToolSystem.cs:816-862). Termina com `AddComponent(e, default(Updated))`
  (RouteToolSystem.cs:780) — é o padrão "definição descartável" comentado em `GAME_SYSTEMS.md:107-113`.

Rotas de trabalho (`RouteType.WorkRoute`, ex.: rota de colheita de recurso/upgrade de serviço) passam
pela MESMA `RouteToolSystem`, só ajustando `serviceUpgrade`/`m_ServiceUpgradeOwner`
(RouteToolSystem.cs:1052,1460-1463,2152-2155,2184-2187) e o `RouteConnectionData` do prefab
(RouteToolSystem.cs:1441-1454,1537-1549) — não existe uma segunda tool.

Cor, agenda (dia/noite), preço de ticket e "fora de serviço" **não passam pela RouteToolSystem** —
são seções da UI de seleção que chamam `PoliciesUISystem.SetPolicy(selectedEntity, policyPrefab, ...)`
diretamente no `Route` selecionado:
- `TicketPriceSection.OnCreate` (`decomp/Game/Game/UI/InGame/TicketPriceSection.cs:37-40`).
- `ScheduleSection.OnCreate` (`decomp/Game/Game/UI/InGame/ScheduleSection.cs:37-54`).
- `VehicleCountSection.OnCreate` (`decomp/Game/Game/UI/InGame/VehicleCountSection.cs:248-254`).
- `TransportationOverviewUISystem.SetLineState` (out-of-service) chama
  `m_PoliciesUISystem.SetPolicy(entity, m_OutOfServicePolicy, !state)`
  (`decomp/Game/Game/UI/InGame/TransportationOverviewUISystem.cs:369-376`).
- `TransportationOverviewUISystem.SetLineSchedule` — mesmo padrão (linhas 378-399).
- Cor: `ColorSection`/`TransportationOverviewUISystem` criam um evento `ColorUpdated`
  (ver §2). Renomear a linha usa `SetLineName` → `m_NameSystem.SetCustomName(entity, name)`
  (`TransportationOverviewUISystem.cs:360-367`) — mecanismo de rename genérico, não é Policy.

## 2. Fluxo de aplicação

### 2.1 Criar/re-rotear (Create/Modify/Remove waypoint)

```
RouteToolSystem.Apply/Cancel (clique do jogador)
  → UpdateDefinitions() destrói a definição do frame anterior e cria uma nova
    (CreationDefinition + ColorDefinition + WaypointDefinition + Updated)  [RouteToolSystem.cs:2159-2193]
  → GenerateWaypointsSystem.OnUpdate (RequireForUpdate em CreationDefinition+WaypointDefinition+Updated,
    GenerateWaypointsSystem.cs:606-608): para cada definição, cria os Waypoint/Segment REAIS
    (CreateWaypoint/CreateSegment, GenerateWaypointsSystem.cs:423-518) — reaproveitando entidades
    antigas quando possível (GetMatchingSegment/GetPartialSegment, linhas 323-421) para não perder
    PathInformation/PathElement/PathTargets já calculados.
  → GenerateRoutesSystem.OnUpdate (mesma query base, GenerateRoutesSystem.cs:208): cria a entidade
    Route (CreateEntity(routeData.m_RouteArchetype), linha 87), preenche os buffers
    RouteWaypoint/RouteSegment lendo os Waypoint/Segment recém-criados por índice
    (FindSubElements, linhas 122-139 — casa por Waypoint.m_Index/Segment.m_Index, não por Entity),
    e marca RouteFlags.Complete se o laço fechou (linha 90-96).
  → ReferencesSystem (Routes/ReferencesSystem.cs, query = Route+RouteWaypoint+RouteSegment com
    Created OU Deleted, linhas 143-157): escreve Owner nos waypoints/segments apontando pra Route
    e mantém o buffer SubRoute do dono (upgrade de prédio), linhas 45-93.
  → WaypointConnectionSystem (Routes/WaypointConnectionSystem.cs, query = Updated (ou Deleted) +
    Waypoint/AccessLane/RouteLane/ConnectedRoute, linhas 2222-2244): acha as faixas de rede/objeto
    mais próximas e finaliza ConnectedRoute nas paradas.
  → RoutePathSystem (query m_UpdatedSegmentQuery = Updated+Segment+PathTargets, RoutePathSystem.cs:337):
    calcula o path de cada segmento.
  → InitializeSystem (Routes/InitializeSystem.cs, RequireForUpdate em Created+RouteNumber, linha 223):
    dá RouteNumber e sorteia VehicleModel pra rotas Created (ver §4).
  → TransportLineSystem (Simulation/TransportLineSystem.cs) passa a despachar veículos.
```

### 2.2 O caminho do MOD (receptor, sem o clique do jogador)

`RouteApplySystem` (`CS2M/Sync/RouteSyncSystems.cs:640-1116`) **pula toda a cadeia
GenerateWaypoints/GenerateRoutes/RouteToolSystem** e cria as entidades reais direto pelos arquétipos
já assados no prefab (`RouteData.m_RouteArchetype/m_WaypointArchetype/m_ConnectedArchetype/
m_SegmentArchetype`, RouteSyncSystems.cs:834-835,946-947,975-976):

1. `ApplyCreate` (RouteSyncSystems.cs:786-865): `CreateEntity()` + `SetArchetype(route, rd.m_RouteArchetype)`,
   seta `Route.m_Flags`, `PrefabRef`, `Game.Routes.Color`, `TransportLine` se o prefab tiver
   `TransportLineData` (linhas 836-842). **Não** tem ramo `else` pra `WorkRouteData` (ver §6).
2. `BuildElements` (RouteSyncSystems.cs:928-1002): cria um `Waypoint` por item do comando
   (arquétipo `m_ConnectedArchetype` se for parada, senão `m_WaypointArchetype`, linha 947),
   resolve a conexão da parada por `CS2M_SyncId` OU pela posição mais próxima dentro de 2,5 m
   (`ResolveConnection`, RouteSyncSystems.cs:1070-1102 — **resolução por proximidade**, ver §6),
   e cria um `Segment` por par de waypoints (linha 966-987). Escreve `RouteWaypoint`/`RouteSegment`
   direto no buffer da Route (linhas 989-1001) — não depende de `FindSubElements` por índice porque
   já cria na ordem certa.
3. Como o arquétipo assado (`RoutePrefab.LateInitialize`, `decomp/.../Prefabs/RoutePrefab.cs:75-88`)
   já inclui `Created`+`Updated` em TODOS os quatro arquétipos, a Route/Waypoint/Segment criados pelo
   `SetArchetype` **nascem com essas tags de graça** — por isso os sistemas vanilla de §2.1
   (ReferencesSystem, WaypointConnectionSystem, RoutePathSystem, InitializeSystem) processam essas
   entidades no MESMO frame como se tivessem vindo da ferramenta local. É essa a garantia por trás do
   comentário em RouteSyncSystems.cs:632-639.
4. `Rebuild` (reroute, RouteSyncSystems.cs:869-924): marca os waypoints/segments antigos `Deleted`
   (cascata pro `ElementSystem` do jogo) e chama `BuildElements` de novo, preservando a MESMA entidade
   Route (identidade, RouteNumber, Policy buffer intactos).
5. `ApplyColor`/`ApplyVisibility` (RouteSyncSystems.cs:1004-1044,711-733): escrevem
   `Game.Routes.Color`/`HiddenRoute` direto e — no caso da cor — recriam o evento
   `Event`+`ColorUpdated` que a UI vanilla dispara, pra qualquer renderer/observer reagir igual.
6. Agenda/ticket/vehicle-count/fora-de-serviço **não têm um apply de rota dedicado**: chegam como
   `PolicyCommand` (kind=3) e `PolicyApplySystem.ApplyOne` (`CS2M/Sync/PolicyApplySystem.cs:46-81`)
   levanta o MESMO evento `Event`+`Modify(target, policyPrefab, active, adjustment)` que a UI vanilla
   levantaria — o `PolicyModifiedSystem`/`RouteModifierInitializeSystem` do jogo faz o resto
   (ver §3 e §5).

## 3. Estado persistido tocado

Componentes `ISerializable` que este mecanismo cria/edita (todos citados na struct, `Serialize`/
`Deserialize` presentes):

| Componente | Papel | Onde |
|---|---|---|
| `Route` (`m_Flags: RouteFlags`, `m_OptionMask: uint`) | flags (Complete) + cache de opções derivadas de Policy | `Routes/Route.cs:6-27` |
| `RouteNumber` (`m_Number: int`) | "Bus Line 3" | `Routes/RouteNumber.cs:6-19` |
| `Game.Routes.Color` (`m_Color: Color32`) | cor da linha (+ veículos, ver §5) | `Routes/Color.cs:7-25` |
| `TransportLine` (`m_VehicleInterval`, `m_UnbunchingFactor`, `m_Flags`, `m_TicketPrice`) | parâmetros de despacho; interval/ticketPrice são CACHE recomputado (ver abaixo) | `Routes/TransportLine.cs:7-56` |
| `RouteWaypoint`/`RouteSegment` (buffers de `Entity`) | topologia da rota | `Routes/RouteWaypoint.cs`, `Routes/RouteSegment.cs` (mesmo padrão) |
| `Waypoint`/`Segment` (`m_Index: int`) | posição na sequência (chave de casamento em `FindSubElements`) | `Routes/Waypoint.cs`, `Routes/Segment.cs` |
| `Position` (waypoint), `Connected` (`m_Connected: Entity`) | posição do waypoint + a que objeto ele gruda (parada) | `Routes/Position.cs`, `Routes/Connected.cs` |
| `HiddenRoute` (tag vazia) | oculta a linha no Transportation Overview | `Routes/HiddenRoute.cs:7-10` |
| `VehicleModel` (buffer `m_PrimaryPrefab`/`m_SecondaryPrefab: Entity`) | modelo de veículo sorteado pra rota (ver §4 — usa `Random`) | `Routes/VehicleModel.cs:6-24` |
| `Policy` (buffer, `m_Policy: Entity`, `m_Adjustment: float`, `m_Flags`) | agenda/ticket/vehicle-count/fora-de-serviço vivem AQUI, não em campos dedicados | `Policies/Policy.cs:7` |
| `RouteModifier` (buffer) | CACHE recomputado de `Policy` (delta de intervalo/preço) — não precisa sync próprio | `Routes/RouteModifier.cs`, recomputado em `Policies/RouteModifierInitializeSystem.cs:44,49,83-95,97-...` |
| `RouteVehicle` (buffer) | veículos despachados AGORA — **`IEmptySerializable`**, não vai pro save, é emergente | `Routes/RouteVehicle.cs:8` |

`TransportLine.m_TicketPrice`/`m_VehicleInterval` e `Route.m_OptionMask` **não são estado autorado
independente** — são recomputados deterministicamente todo tick a partir do buffer `Policy` (estado
autorado real): `RouteModifierInitializeSystem.RefreshRouteOptions`
(`Policies/RouteModifierInitializeSystem.cs:83-95`) recalcula `m_OptionMask`, e
`TransportLineSystem` (`Simulation/TransportLineSystem.cs:180-201`) recalcula
`m_TicketPrice`/`m_VehicleInterval` a partir de `RouteUtils.ApplyModifier(..., modifiers, ...)`
(linha 184) — que por sua vez vem de `RefreshRouteModifiers` lendo o mesmo `Policy` buffer
(`RouteModifierInitializeSystem.cs:97-...`). **Conclusão prática:** sincronizar o buffer `Policy` é
suficiente; não precisa (e não deveria) mandar `m_OptionMask`/`m_TicketPrice`/`m_VehicleInterval` no
fio — cada máquina os re-deriva sozinha assim que o `Policy` bater.

## 4. Perigos cross-machine

- **`RouteNumber` é alocado por varredura local, não por acordo** — `AssignRouteNumbersJob.
  FindFreeRouteNumber` (`Routes/InitializeSystem.cs:55-104`) escaneia TODAS as rotas do MESMO prefab
  já existentes NESTA máquina (via `m_RouteQuery`, sem filtro de dono) e devolve o menor número livre
  (bitset, linhas 70-100). Depende de quantas/quais rotas do mesmo prefab essa máquina já tem — que
  pode divergir da outra máquina no instante da criação. Roda em QUALQUER rota `Created`
  (`InitializeSystem.cs:41,223`), inclusive nas criadas pelo `RouteApplySystem` (que nascem com
  `Created` de graça, §2.2 item 3) — ou seja, o número que o jogo dá no receptor quase certamente
  DIFERE do número do emissor.
- **`VehicleModel` é sorteado com `Random`** — `InitializeSystem.SelectVehicleJob` usa
  `RandomSeed.Next()` (`Routes/InitializeSystem.cs:244`, não semeado por rota nem cross-machine) pra
  escolher `m_PrimaryPrefab`/`m_SecondaryPrefab` de QUALQUER rota `Created` — inclusive a criada pelo
  `RouteApplySystem` no receptor. `VehicleModel` É `ISerializable` (vai pro save), então essa
  divergência de modelo de veículo entre as duas máquinas é persistida, não só cosmética-de-frame.
- **`WaypointDefinition`/`Segment` casam por posição+índice, nunca por Entity** —
  `GenerateWaypointsSystem.GetMatchingSegment/GetPartialSegment` (`Tools/GenerateWaypointsSystem.cs:
  323-421`) usam `SegmentKey(prefab, originalRoute, float4(position,0/1))` — ok porque posição é
  idêntica entre máquinas (`GAME_SYSTEMS.md:85-86`), mas frágil a arredondamento de float se um
  waypoint mover fracionariamente diferente nas duas sims antes de re-rotear.
- **Ordem de iteração de chunk em `FindSubElements`** (`Tools/GenerateRoutesSystem.cs:122-139`) —
  escreve `routeWaypoints[nativeArray2[j].m_Index]`/`routeSegments[nativeArray3[k].m_Index]` indexado
  por `Waypoint.m_Index`/`Segment.m_Index`, não pela ordem do chunk — por isso é seguro mesmo com
  chunks em ordem diferente nas duas máquinas (m_Index é o que veio do `WaypointDefinition`, idêntico
  se o comando de fio carregar a mesma lista ordenada — o que `RouteCreateCommand` faz, §5).
- **`PrefabRef.m_Prefab` nunca é comparável entre máquinas** (`GAME_SYSTEMS.md:80-86`) — todo o
  mecanismo de resolução de prefab do mod usa nome (`PrefabBase.name`/`PrefabID`), nunca o Entity —
  confirmado em `RouteSync.BuildCommand`/`TryGetRouteData`
  (`CS2M/Sync/RouteSyncSystems.cs:550-554,1046-1066`).
- **Conexão de parada (`Connected.m_Connected`) é um Entity local** — por isso o comando manda
  `WpConnId` (SyncId, se a parada for sincronizada) E `WpConnX`/`WpConnZ` (posição) como fallback
  (`RouteCreateCommand.cs:41-45`); o receptor tenta o id primeiro e só cai pra busca por proximidade
  de 2,5 m se a parada não tiver SyncId (`ResolveConnection`, `RouteSyncSystems.cs:1070-1102`) —
  ver §6, é resolução por proximidade real, não só um fallback teórico.
- **Hash de eco arredonda a 0,25 m e ignora Y** — `RouteSync.Hash`
  (`CS2M/Sync/RouteSyncSystems.cs:121-143`) faz `math.round(cmd.WpX[i] * 4f)` (mesmo pra Z, não pra Y)
  — um reroute que só muda a altura (elevação de trecho subterrâneo, por exemplo) não muda o hash e
  pode ficar preso pelo guard de eco.

## 5. O que o CS2M faz hoje

Cobertura ampla e majoritariamente correta, via 3 famílias de comando:

- **`RouteCreateCommand`** (`CS2M/Commands/Data/Game/RouteCreateCommand.cs`) — criar/re-rotear.
  Detectado em `RouteDetectorSystem.DetectCreated/DetectRerouted/DetectSaveRouteReroutes`
  (`CS2M/Sync/RouteSyncSystems.cs:409-481,372-407`), aplicado em `RouteApplySystem.ApplyCreate/Rebuild`
  (linhas 786-924). Identidade: `CS2M_SyncId` alocado no create (linha 427-429) e propagado; linhas
  vindas do SAVE (sem SyncId) resolvem por `prefabName + RouteNumber` (`RouteResolver.Resolve`,
  linhas 148-187) — ganham um SyncId no primeiro reroute (comentário linhas 250-254).
  Guard de eco por hash de conteúdo (`RouteSync.Snapshot`/`SnapshotByNumber`, linhas 32-38,120-143).
  Diferimento de aplicação se a parada referenciada ainda não existir no frame
  (`ShouldDefer`/`_deferredCreates`, linhas 685-693,764-806, até 5 tentativas).
- **`RouteColorCommand`** — cor da linha + veículos já dispatchados (`ApplyColor`,
  `RouteSyncSystems.cs:1004-1044`, replica o que `ColorSection`/`TransportationOverviewUISystem` fazem
  na UI vanilla, incluindo levantar `ColorUpdated`).
- **`RouteVisibilityCommand`** — `HiddenRoute` (`ApplyVisibility`, linhas 711-733), detectado por
  snapshot 1 Hz (`DetectVisibility`, linhas 327-364) já que o toggle não gera `Updated`/evento.
- **`DeleteCommand` (TargetKind=1)** — apagar a linha (`DeleteDetectorSystem.DetectRouteDeletes`,
  `CS2M/Sync/DeleteDetectorSystem.cs:144-210`; aplica em `RemoteEditApplySystem.ApplyRouteDelete`,
  `CS2M/Sync/RemoteEditApplySystem.cs:117-150`). Só a Route recebe `Deleted`; waypoints/segments
  cascateiam pelo `ElementSystem` do próprio jogo nas duas máquinas (comentário linha 142-144).
- **`PolicyCommand` (TargetKind=3)** — cobre GENERICAMENTE agenda (dia/noite), preço de ticket,
  contagem de veículos e fora-de-serviço, porque TODOS são só entradas no buffer `Policy` da Route
  (§1). Detectado em `PolicyDetectorSystem.DetectScopedPolicies`
  (`CS2M/Sync/PolicyDetectorSystem.cs:118-255`, ramo `kind=3` linhas 165-193, endereça por SyncId
  senão `prefabName + RouteNumber` em `TargetX`), aplicado em `PolicyApplySystem.ApplyOne`
  (`CS2M/Sync/PolicyApplySystem.cs:46-81`) levantando o mesmo evento `Modify` que a UI vanilla
  levantaria — o jogo recomputa `RouteModifier`/`m_OptionMask`/`m_TicketPrice` sozinho (§3).
- **`RenameCommand` (TargetKind=3)** — nome customizado da linha (`RenameSyncSystems.cs:165-179`
  detect, `:295-...` apply), mesmo padrão de resolução SyncId→prefab+RouteNumber.
- **`SyncContract.cs`** classifica `RouteCreateCommand`/`RouteColorCommand`/`RouteVisibilityCommand`
  como `WorldContract` e mapeia `RouteToolSystem` pra esse trio (`CS2M/Sync/SyncContract.cs:69-71,119`)
  — o `Verify()` reflexivo garante que nenhum comando novo de rota fique sem classificação.

## 6. GAPS e recomendação

1. **Rotas de trabalho (`RouteType.WorkRoute`) não são detectadas — criação, reroute E delete.**
   `RouteDetectorSystem._createdRoutes`/`_updatedRoutes`/`_updatedSaveRoutes`
   (`CS2M/Sync/RouteSyncSystems.cs:214-231,232-249,255-273`) e
   `DeleteDetectorSystem._deletedRouteQuery` (`CS2M/Sync/DeleteDetectorSystem.cs:63-76`) exigem
   `ComponentType.ReadOnly<TransportLine>()` no `All` — mas uma `WorkRoutePrefab`
   (`decomp/.../Prefabs/WorkRoutePrefab.cs:13-33`, categoria `[ComponentMenu("Routes/", ...)]` como
   `RoutePrefab`/`TransportLinePrefab`, com `PlaceableInfoviewItem` — é conteúdo posicionável pelo
   jogador) nunca ganha `TransportLine`, só `WorkRoute` (tag vazia,
   `WorkRoutePrefab.cs:40`/`Routes/WorkRoute.cs:8`). A MESMA `RouteToolSystem` desenha as duas
   (RouteToolSystem.cs:138-141,463-471,1537-1549), então um jogador pode desenhar/apagar uma rota de
   trabalho sem que o outro veja NADA — nem create, nem reroute, nem delete. `RouteApplySystem` do
   lado receptor até SABERIA aplicar (código genérico por `RouteData`, `TryGetRouteData`,
   `RouteSyncSystems.cs:1046-1066`) — só nunca recebe o comando porque o detector nunca o gera.
   A cor sincroniza por acidente (`_colorEvents` não exige `TransportLine`,
   `RouteSyncSystems.cs:274-285,483-529`), mas a rota em si não existe do outro lado pra colorir.
   **Checklist do fix:** trocar `TransportLine` por `Route` puro (sem exigir subtipo) nas 4 queries
   citadas, e em `ApplyCreate` (`RouteSyncSystems.cs:839-842`) adicionar o ramo `else if
   (EntityManager.HasComponent<WorkRouteData>(prefabEntity))` — hoje só existe o `if
   (HasComponent<TransportLineData>(...))`; sem o `else`, uma WorkRoute recriada no receptor fica sem
   nada explícito (o `WorkRoute` tag ainda vem de graça pelo arquétimo, mas vale documentar/testar).
2. **Conexão de parada por proximidade é resolução real, não só fallback teórico.**
   `ResolveConnection` (`CS2M/Sync/RouteSyncSystems.cs:1070-1102`) cai pra "entidade `ConnectedRoute`
   mais próxima dentro de 2,5 m" sempre que a parada referenciada não tem `CS2M_SyncId` — o que é o
   caso comum pra paradas nativas do save (mailbox, plataforma gerada pelo prédio) que nunca ganham
   SyncId. Isso é exatamente o padrão "resolução por proximidade" que a regra de arquitetura pede pra
   sinalizar: duas paradas equidistantes (por exemplo, duas pontas de uma estação simétrica) podem
   casar errado. Mitiga com o raio pequeno, mas não é identidade.
3. **`RouteNumber` nunca é enviado como parte do `create` original — só corrigido depois via
   `_pendingNumbers` com delay de 3 frames** (`RouteApplySystem.ApplyCreate`/`ProcessPendingNumbers`,
   `RouteSyncSystems.cs:735-757,863`). Funciona (comprova o próprio comentário do código,
   linha 735), mas por 3 frames a linha existe no receptor com o número ERRADO (o que a
   `InitializeSystem.AssignRouteNumbersJob` local deu) — qualquer UI/log que leia `RouteNumber` nesses
   3 frames vê o valor errado. Nenhum outro sistema do mod parece depender disso hoje, mas é uma
   janela documentável.
4. **`VehicleModel` diverge por `Random` sem ser corrigido** (§4) — o mod não tenta igualar o modelo
   de veículo sorteado entre as máquinas. Dado que veículos individuais já divergem por design
   (`GAME_SYSTEMS.md:31-34`), isso é provavelmente aceitável — mas como `VehicleModel` É
   `ISerializable`/persistido (não é só um efeito de frame como a posição do veículo), vale uma
   decisão explícita (aceitar ou fixar) em vez de silêncio.
5. **Hash de eco ignora a coordenada Y** (§4) — um reroute que só muda elevação (ex.: uma linha de
   metrô ganhando um trecho subterrâneo diferente) não dispara `DetectRerouted`/
   `DetectSaveRouteReroutes` de novo se X/Z não mudaram. Baixo risco (waypoints de rota raramente
   mudam só em Y), mas é uma lacuna real do guard.

## 7. NÃO VERIFICADO

- Não confirmei em código se existe, no jogo base, um ponto de UI/milestone que efetivamente deixa o
  jogador desenhar uma `WorkRoutePrefab` manualmente (só confirmei que o tipo/arquétipo/tool suportam
  — `RouteType.WorkRoute`, `WorkRoutePrefab.GetPrefabComponents` com `PlaceableInfoviewItem`,
  `RouteToolSystem` com branches de `WorkRoute`). Se for 100% simulação-interna (nunca via clique do
  jogador), o Gap nº1 acima teria prioridade bem menor.
  Timeboxed — não tive tempo pra achar o consumidor de `WorkRoutePrefab` (asset/milestone/tool button
  específico).
- Não abri `Simulation/TransportLineSystem.cs` inteiro nem `Policies/ModifiedSystem.cs`/
  `PolicyModifiedSystem` — confiei no trecho citado (linhas 150-210 do primeiro) pra confirmar a
  recomputação de `m_TicketPrice`/`m_VehicleInterval`; não verifiquei se há algum outro caminho de
  escrita direta nesses campos fora do já citado.
- Não verifiquei o `ElementSystem` (`Routes/ElementSystem.cs`, mencionado só de relance no grep inicial)
  pra confirmar em detalhe COMO ele cascateia `Deleted` de Route pra Waypoint/Segment — aceitei a
  afirmação do comentário do mod (`RouteSyncSystems.cs:142-144`) sem ler o arquivo.
- Não testei em jogo (2 sims) nenhum dos cenários deste dossiê — análise 100% estática por leitura de
  código (decomp + mod). O que está em `docs/atomicbatch-spec.md`/memória do projeto sobre rota não
  foi cruzado aqui além do que já citei de `GAME_SYSTEMS.md`.
- Não confirmei o conteúdo de `RouteModifier.cs`/`RouteModifierType.cs`/`RouteModifierData.cs` campo a
  campo (só usei o suficiente pra confirmar que é buffer derivado, não autorado).
