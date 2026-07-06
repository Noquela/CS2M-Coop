# DefaultTool + SelectionTool — dossiê de sync

> Fonte: `decomp/Game/Game/Tools/DefaultToolSystem.cs`, `decomp/Game/Game/Tools/SelectionToolSystem.cs`,
> mais os sistemas que consomem o que elas produzem (`GenerateObjectsSystem`, `GenerateEdgesSystem`,
> `GenerateNodesSystem`, `Simulation/MapTilePurchaseSystem.cs`). Conferido contra o código do mod em
> `CS2M/Sync/` e `CS2M/Commands/`.

## 1. Entradas do jogador

**DefaultToolSystem** (`kToolID = "Default Tool"`, DefaultToolSystem.cs:630) é a ferramenta ativa
"padrão" (o cursor normal fora de qualquer tool de construção):

- A cada frame, em `State.Default`, faz um raycast e gera uma "definição de preview" para o que
  está sob o cursor — mesmo sem clicar (`Update()`, case `State.Default`, DefaultToolSystem.cs:955-965).
- Clique (`base.applyAction.WasPressedThisFrame()`, DefaultToolSystem.cs:887) chama `Apply(...)`.
  Em `State.Default`, isso NÃO arma dragging por padrão: só entra em `MouseDownPrepare` e already
  seleciona o entity temporário (`Apply()` case `State.Default`, DefaultToolSystem.cs:933-939).
- Se o mouse se move >1m antes de soltar, tenta armar drag (`Update()` case `State.MouseDown`,
  DefaultToolSystem.cs:976-984 → `StartDragging`, DefaultToolSystem.cs:1014-1059) — **mas isso só
  vira drag de verdade se `allowManipulation == true`** (ver seção 2).
- Cancelar (botão direito / `cancelAction`) chama `Cancel()` (DefaultToolSystem.cs:910-927): em
  `State.Default` apenas zera `m_ToolSystem.selected` (deseleciona); em `State.Dragging` desfaz o
  drag sem aplicar nada.

**SelectionToolSystem** (`kToolID = "Selection Tool"`, SelectionToolSystem.cs:485) não é a
ferramenta ativa por padrão — ela só assume o `activeTool` quando uma tela de UI pede
explicitamente, com um `selectionType` específico:

- `SelectionType.ServiceDistrict`: acionado pelo painel "Districts" de um prédio de serviço
  selecionado — `DistrictsSection.cs:108-110` seta `selectionType = ServiceDistrict`,
  `selectionOwner = selectedEntity` (o prédio) e troca `m_ToolSystem.activeTool` para a Selection
  Tool.
- `SelectionType.MapTiles`: acionado pela tela de compra de terreno —
  `MapTilePurchaseSystem.selecting` (setter, MapTilePurchaseSystem.cs:102-114) seta
  `selectionType = MapTiles`, `selectionOwner = Entity.Null` e ativa a tool; chamado por
  `MapTilesUISystem.cs:123,205`.
- Uma vez ativa, o jogador desenha uma marquee (arrasta do clique inicial `m_StartPoint` até o
  ponto atual `m_RaycastPoint`, `GetSelectionQuad`, SelectionToolSystem.cs:1105-1150); soltar o
  botão principal soma à seleção (`Apply()`, SelectionToolSystem.cs:861-913), o botão secundário
  remove (`Cancel()`, SelectionToolSystem.cs:810-859).

## 2. Fluxo de aplicação

### DefaultToolSystem — hover/seleção (o caminho normal)

1. Raycast (`InitializeRaycast`, DefaultToolSystem.cs:760-817) mira em `StaticObjects|MovingObjects
   |Labels|Icons` (+`Areas` se não underground) no estado Default; muda para `Terrain|Net` nos
   estados de drag (linhas 771-777).
2. `UpdateDefinitions` (DefaultToolSystem.cs:1083-1121): primeiro destrói as definições do frame
   anterior (`DestroyDefinitions`, ToolBaseSystem.cs:516), depois agenda `CreateDefinitionsJob`
   (DefaultToolSystem.cs:42-360) para a entidade sob o cursor. Esse job cria **uma** entidade de
   definição via `EntityCommandBuffer` com `CreationDefinition{ m_Flags = CreationFlags.Select }`
   (linha 183) + `Updated` (linha 185), e um componente de forma conforme o tipo do alvo:
   - `Edge` → `NetCourse` (o Bezier do segmento, linhas 190-210);
   - `Game.Net.Node` → `NetCourse` degenerado (ponto único, linhas 211-231);
   - qualquer entidade com `Transform` (prédio/objeto) → `ObjectDefinition` (linhas 232-320), e
     recursivamente registra sub-áreas (`m_SubAreas`, linha 304-319) e o prédio-pai se for um
     `ServiceUpgrade` (linhas 130-144) ou um objeto anexado (`Attached`, linhas 145-150);
   - área (`Game.Areas.Node` buffer) → copia o buffer `Node` (linhas 321-327);
   - rota (`RouteWaypoint` buffer) → copia para `WaypointDefinition` (linhas 328-345);
   - ícone/notificação → `IconDefinition` (linhas 346-350);
   - agregado (`AggregateElement` buffer) → copia (linhas 351-357).
3. Sempre que uma nova definição de hover é criada, `base.applyMode = ApplyMode.Clear`
   (DefaultToolSystem.cs:963). `ToolOutputSystem.OnUpdate` (ToolOutputSystem.cs:19-31) lê
   `m_ToolSystem.applyMode` e despacha `m_UpdateSystem.Update(SystemUpdatePhase.ClearTool)`
   (linha 25) — uma fase própria (`SystemUpdatePhase.cs:24`), distinta de `Modification1..5`.
4. Nessa fase `ClearTool` rodam os `Generate*System` (que também são os consumidores normais de
   construção): `GenerateObjectsSystem`, `GenerateEdgesSystem`, `GenerateNodesSystem`,
   `GenerateAreasSystem`, `GenerateRoutesSystem`, `GenerateAggregatesSystem`,
   `GenerateNotificationsSystem`, `GenerateWaterSourcesSystem`, `GenerateBrushesSystem`. Todos eles
   tratam `CreationFlags.Select` como um caso especial que **não cria/move/deleta nada real** — só
   carimba `Temp{ m_Original = entity, m_Flags |= TempFlags.Select }` na própria entidade de
   definição (confirmado em `GenerateObjectsSystem.cs:1003-1005` para objetos e
   `GenerateEdgesSystem.cs:1360-1363` para edges — mesmo padrão nos dois). `Temp` é uma tag de
   fluxo que vive só o frame corrente (regra do `CleanupSystem`) e serve pra desenhar o contorno de
   destaque + alimentar o painel de info — nunca é estado persistido.
5. No clique (`Apply()` case `State.Default`, sem drag), `base.applyMode` continua `ApplyMode.None`
   (linha 938) — **a fase `ApplyTool` nunca roda** para um clique simples. `SelectTempEntity`
   (DefaultToolSystem.cs:1123-1174) varre as entidades `Temp` recém-criadas com `SelectEntityJob`
   (linhas 362-488) pra decidir qual `Temp.m_Original` vira `m_ToolSystem.selected` — isso só abre
   o painel de informação certo; `selected`/`selectedIndex` são propriedades C# simples do
   `ToolSystem` (`ToolSystem.cs:101,113`), não componente ECS, não `ISerializable`.
6. O ÚNICO ramo que chega a setar `ApplyMode.Apply` é `Apply()` case `State.Dragging`
   (DefaultToolSystem.cs:940-943), alcançável só via `StartDragging` bem-sucedido
   (linhas 1014-1059), que exige `allowManipulation == true` **e** a entidade arrastada ter
   `Moving` ou `Game.Objects.Marker` (linha 1033). **Busquei `.allowManipulation =` em toda a
   `decomp/Game/Game` e não há nenhum call-site que sete essa propriedade como `true`** — só a
   declaração do próprio getter/setter (linha 670). Ou seja: no jogo normal (fora de um possível
   Editor, ver seção 7), esse ramo é código morto — a Default Tool nunca de fato move nada.
   Se algum dia for ativado, o que ele faz é escrever `Transform.m_Position` direto na entidade
   real a partir do `RaycastHit.m_HitPosition` (linhas 997-998, 1002), sem passar por
   `CreationDefinition` nenhuma — o tipo de escrita direta que a arquitetura do CS2M proíbe.

### SelectionToolSystem — marquee de área

1. `UpdateDefinitions` (SelectionToolSystem.cs:946-981) monta o quad da marquee e agenda
   `FindEntitiesJob` (linhas 34-155, busca por interseção na quad-tree de áreas do `SearchSystem`)
   seguido de `CreateDefinitionsJob` (linhas 157-194, `IJobParallelForDefer`) que cria, por área
   encontrada, `CreationDefinition{ Select }` + cópia do buffer `Node` — mesma fase `ClearTool` /
   mesmo padrão `Temp{Select}` descrito acima (`applyMode = ApplyMode.Clear` em vários pontos:
   linhas 878, 888, 926, 936).
2. Ao soltar o botão (`Apply()`/`Cancel()` terminais), `ToggleTempEntity` (linhas 1004-1019) roda
   `ToggleEntityJob` (linhas 196-263) — **primeira mutação real**: adiciona/remove um
   `SelectionElement(temp.m_Original)` no `DynamicBuffer<SelectionElement>` da entidade de trabalho
   própria da tool, `m_SelectionEntity` (criada em `OnUpdate`, linha 735; destruída em
   `OnStopRunning`, linha 587-591).
3. `UpdateSelection` (linhas 1038-1053) então, conforme `selectionType`:
   - **`ServiceDistrict`** → `UpdateServiceDistrictsJob` (linhas 386-421): copia o buffer de
     trabalho `SelectionElement` para o `DynamicBuffer<ServiceDistrict>` **real**, no
     `owner.m_Owner` (o prédio de serviço selecionado no painel), e marca esse owner `Updated`
     (linha 418). **Esta é a mutação persistida de verdade** desta ferramenta.
   - **`MapTiles` + modo Editor** → `UpdateStartTilesJob` (linhas 300-338): escreve a lista de
     tiles iniciais do `MapTileSystem` — dado de bootstrap do Editor, não save-game normal.
   - **`MapTiles` em jogo normal** → `CopySelection`/`UpdateSelection` simplesmente retornam
     `inputDeps` sem mudar nada (linhas 1027-1035, 1044-1049). A mutação de verdade acontece
     **fora** das duas tool systems: `MapTilePurchaseSystem.PurchaseSelection()`
     (Simulation/MapTilePurchaseSystem.cs:311-344), chamada por um botão de UI
     (`UI/InGame/MapTilesUISystem.cs:133,216`), lê o mesmo buffer `SelectionElement` de
     `m_SelectionEntity` (via `TryGetSelections`, linhas 355-367) e: (a) debita
     `PlayerMoney` da cidade (linhas 318-320); (b) para cada tile selecionado, chama
     `UnlockTile` (linhas 332, 346-353) que remove o componente `Native` e adiciona `Updated`.

## 3. Estado persistido tocado

| Componente | ISerializable? | Quem escreve | Onde |
|---|---|---|---|
| `Game.Areas.ServiceDistrict` (buffer, no prédio-owner) | Sim (`Serialize` escreve `m_District` cru — ServiceDistrict.cs:27-30) | `SelectionToolSystem.UpdateServiceDistrictsJob` | SelectionToolSystem.cs:386-421 |
| `Game.Common.Native` (tag, removido) | `IEmptySerializable` (Native.cs:8) | `MapTilePurchaseSystem.UnlockTile` | MapTilePurchaseSystem.cs:346-353 |
| `Game.City.PlayerMoney` (int) | Sim (PlayerMoney.cs:43-47) | `MapTilePurchaseSystem.PurchaseSelection` | MapTilePurchaseSystem.cs:318-320 |
| `Game.Tools.SelectionElement` / `SelectionInfo` | **Não** — nenhum `Serialize`/`ISerializable` em SelectionElement.cs / SelectionInfo.cs | `SelectionToolSystem` (scratch) | — |
| `Game.Tools.Debug` (tag) | Não (`IComponentData` puro, Debug.cs:7) | `DefaultToolSystem.SelectEntityJob`, só se `debugSelect` (dev) | DefaultToolSystem.cs:476-486 |
| `ToolSystem.selected`/`selectedIndex` | Não é ECS (propriedade C#) | `DefaultToolSystem.SelectTempEntity` | ToolSystem.cs:101,113 |

Conclusão da seção: **DefaultToolSystem não persiste nada em jogo normal** — seleção pura. A
única superfície que precisa convergir entre máquinas vinda desta dupla de ferramentas é
`ServiceDistrict` (via SelectionToolSystem) e, por composição externa, `Native`+`PlayerMoney` (via
`MapTilePurchaseSystem`, que a SelectionToolSystem alimenta mas não escreve).

## 4. Perigos cross-machine

- **`ServiceDistrict.m_District` é uma `Entity` crua no wire de serialização**
  (ServiceDistrict.cs:29, `writer.Write(m_District)`). Igual ao perigo já documentado pra
  `PrefabRef.m_Prefab` no GAME_SYSTEMS.md — índice de Entity não é garantido igual entre dois
  clientes carregando "o mesmo" save. Se o CS2M um dia sincronizar isso copiando o `Entity` cru,
  vai apontar pro distrito errado (ou pra nada) no receptor.
- **Ordenação por `Entity.Index` é usada como chave de dedup local**, não de rede:
  `EntityComparer` (SelectionToolSystem.cs:76-83) ordena por `x.Index - y.Index` e dedup em
  `FindEntitiesJob.Execute` (linha 137-153) — puramente local/rendering, mas mostra que o próprio
  código vanilla já assume `Entity.Index` como chave só-local; não deve virar chave de sync.
- **Iteração de query sem ordem garantida** ao computar custo de tile: quando `includeSelection:
  false`, `CalculateOwnedTilesCost` (MapTilePurchaseSystem.cs:383-423) itera
  `m_OwnedTileQuery.ToEntityArray` (linha 399) — ordem de chunk/archetype não tem garantia de ser
  idêntica entre duas máquinas com histórico de criação/deleção diferente; o resultado é uma soma
  de floats depois arredondada (`Mathf.RoundToInt`, linha 117) — risco baixo mas não descartado
  (ver NÃO VERIFICADO).
- **Tiles de mapa têm posição fixa e única** (`Geometry.m_CenterPosition`, grade do mapa) — isso
  é o que torna legítimo o casamento por posição que o CS2M já faz (`TileSyncSystems.cs`); não é
  o mesmo antipadrão "resolver por proximidade" already flagged pra junções de rua (posições de nó
  podem coincidir/ser ajustadas; centros de tile, não).
- **`MapTilePurchaseSystem.PurchaseSelection` debita `PlayerMoney` local no clique** (linhas
  318-320) — quem clicou já mudou seu próprio dinheiro local antes de qualquer rede saber; sem
  reconciliação host-autoritativa, dois jogadores comprando ao mesmo tempo divergem a economia (o
  CS2M já mitiga isso — ver seção 5 — mas vale registrar que é o comportamento vanilla).
- **Drag path morto de `DefaultToolSystem`** (seção 2): se um dia ativado, escreve
  `Transform.m_Position` direto (linhas 997-998, 1002) sem definição — exatamente o tipo de
  escrita-na-mão que a lei de arquitetura do projeto proíbe.

## 5. O que o CS2M faz hoje

- **Compra de tile de mapa — coberto e funcionando**: `CS2M/Sync/TileSyncSystems.cs`.
  `TileDetectorSystem` (linhas 57-151) faz *diff* do conjunto de tiles possuídos a cada 120 frames
  (~2s), casando por `Geometry.m_CenterPosition` (não por Entity), e envia
  `TilePurchaseCommand{ Xs, Zs, Cost }` (`CS2M/Commands/Data/Game/TilePurchaseCommand.cs`) — `Cost`
  é amostrado ao vivo de `MapTilePurchaseSystem.cost` enquanto a seleção está ativa (linhas 93-99,
  já que a compra limpa a seleção antes do diff rodar). `TileApplySystem` (linhas 157-268) recebe
  via `TilePurchaseHandler` (`CS2M/Commands/Handler/Game/TilePurchaseHandler.cs`), casa o tile mais
  próximo (raio 10m, linha 207) e replica exatamente `MapTilePurchaseSystem.UnlockTile`
  (remove `Native` + `Updated`, linhas 234-239); no host, debita `PlayerMoney` pelo `cmd.Cost`
  recebido pela rede (linhas 249-264, comentário "host-authoritative economy — debit what the
  buyer paid").
- **Coverage de distrito de serviço (`ServiceDistrict`) — NÃO coberto**: busquei
  `ServiceDistrict|DistrictsSection|SelectionType.ServiceDistrict|CoveragePreview` em toda a
  `CS2M/` e não há nenhum resultado. A árvore de sync de distrito que existe
  (`CS2M/Sync/DistrictApplySystem.cs`, `DistrictDetectorSystem.cs`,
  `CS2M/Commands/Data/Game/DistrictCommand.cs`) trata da **forma/polígono** do distrito
  (pintar/redesenhar `Game.Areas.District`), um mecanismo totalmente diferente da atribuição
  "quais distritos este prédio de serviço cobre" que vive em `ServiceDistrict` no owner.
- **Seleção simples (`m_ToolSystem.selected`) — corretamente não sincronizada**: é estado de UI
  local, não estado autorado; não há (nem deveria haver) código do mod tocando nisso.

## 6. GAPS e recomendação

1. **GAP real e não coberto: atribuição de distritos de serviço (`ServiceDistrict` buffer).**
   Quando o Jogador A abre o painel "Districts" de um prédio de serviço
   (`SelectionType.ServiceDistrict`, `DistrictsSection.cs:108-110`) e adiciona/remove um distrito,
   `UpdateServiceDistrictsJob` (SelectionToolSystem.cs:386-421) reescreve o buffer real no prédio —
   e nada avisa o Jogador B. A cobertura de serviço diverge silenciosamente (afeta pathfind de
   cobertura: `Simulation/ServiceCoverageSystem.cs` e os `*PathfindSetup.cs` que leem
   `ServiceDistrict`, confirmados na busca por referências). Checklist pra um sync correto:
   - Comando novo (`ServiceDistrictCommand` ou similar) carregando: id/posição estável do prédio
     dono + lista de distritos por **identidade posicional** (centróide), no mesmo padrão que
     `DistrictCommand.CenterX/CenterZ` já usa pra re-achar um distrito
     (`CS2M/Commands/Data/Game/DistrictCommand.cs:20-28`) — **nunca** serializar `Entity` cru
     (perigo da seção 4).
   - Receptor reescreve o `DynamicBuffer<ServiceDistrict>` do prédio-alvo do mesmo jeito que
     `UpdateServiceDistrictsJob` faz (limpar + `Add(new ServiceDistrict(distritoResolvido))` +
     `Updated`), nunca criar a entidade do zero.
   - Precisa de um detector (como o `TileDetectorSystem`) OU interceptar diretamente
     `UpdateServiceDistrictsJob`/o clique do painel — decisão de arquitetura em aberto.
2. **Observação sobre a compra de tile (não é bug, é design a revisar): o host confia no `Cost`
   que veio pela rede** (`TileApplySystem.cs` linhas 249-264) em vez de recalcular
   `MapTilePurchaseSystem.cost` localmente antes de debitar. Um cliente adulterado/desincronizado
   poderia reportar um `Cost` arbitrário. Não é urgente (mesmo padrão adotado pra construção,
   segundo o comentário no código), mas vale registrar como superfície de confiança no cliente.
3. **Vigiar o drag morto do `DefaultToolSystem`.** Hoje inofensivo (`allowManipulation` nunca é
   `true` em `Game.dll`), mas se uma atualização do jogo, DLC, ou um futuro modo Editor co-op
   ligar essa flag, o caminho de drag escreve `Transform` direto sem definição — precisa de
   comando próprio antes de ser seguro em coop, não pode ser deixado "passar direto".

## 7. NÃO VERIFICADO

- Se alguma assembly do modo Editor do jogo (não decompilada aqui — só `Game.dll` está em
  `decomp/Game/`, sem `Editor.dll`) seta `allowManipulation = true` em `DefaultToolSystem` e ativa
  de fato o caminho de arrastar (`State.Dragging`) dentro do Editor de mapas.
- O atributo/registro exato de fase (`UpdateInGroup`/`UpdateBefore`/`UpdateAfter`) de
  `DefaultToolSystem`, `SelectionToolSystem` e de cada `Generate*System` — não achei chamada
  explícita de registro nem atributo no código decompilado (pode ter sido descartado pelo
  decompilador ou ser registrado por uma tabela orientada a dados fora do C# capturado aqui).
  Confirmei que as fases `ToolUpdate`/`ClearTool`/`ApplyTool` existem como valores de
  `SystemUpdatePhase` (SystemUpdatePhase.cs:21-25) e que `ToolOutputSystem` despacha
  `ClearTool`/`ApplyTool` a partir de `ToolSystem.applyMode` (ToolOutputSystem.cs:22-30), mas não
  qual fase literal cada sistema produtor/consumidor está registrado para rodar.
- Se a ordem de iteração de `EntityQuery.ToEntityArray` (usada em
  `MapTilePurchaseSystem.CalculateOwnedTilesCost`, linha 399) é garantidamente idêntica entre dois
  mundos ECS inicializados independentemente em máquinas diferentes — não achei garantia nem
  contraprova no código decompilado; fica como risco de baixa probabilidade, não confirmado nem
  descartado.
- Não tracei exaustivamente todo binding de UI que possa ativar `SelectionToolSystem` além de
  `DistrictsSection.cs` (ServiceDistrict) e `MapTilesUISystem.cs`/`MapPanelSystem.cs`
  (MapTiles) — a busca por `SelectionType.ServiceDistrict` e `PurchaseSelection`/`selecting` achou
  esses arquivos, mas pode haver outro caller de `selectionType`/`selectionOwner` não coberto.
