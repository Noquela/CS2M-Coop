# Demolição — dossiê de sync

> Fonte: `decomp/Game/Game/Tools/BulldozeToolSystem.cs` (1763 linhas) + toda a cadeia de
> consumo (`Generate*System` → `Apply*System` → `SubElementDeleteSystem` → `CleanUpSystem`) +
> a segunda porta de entrada (botão "delete" do painel de info, `ActionsSection.cs`). Mod:
> `CS2M/Sync/NetEditDetectorSystem.cs`, `NetEditApplySystem.cs`, `DeleteDetectorSystem.cs`,
> `RemoteEditApplySystem.cs`, `AreaEditSystems.cs`, `CascadeDeleteUtil.cs`,
> `NetBatchCaptureSystem.cs`/`NetBatchApplySystem.cs` (caminho experimental `CS2M_ATOMIC=1`).

## 1. Entradas do jogador

Existem **DUAS portas de entrada vanilla** para demolição — o dossiê original supôs que só
existia uma (o `BulldozeToolSystem`), mas o código mostra duas cadeias completamente distintas:

### 1.1 — Ferramenta de demolir (arrastar/clicar)
`BulldozeToolSystem` (`Tools/BulldozeToolSystem.cs:27`) é uma `ToolBaseSystem` com uma máquina de
estados própria (`enum State { Default, Applying, Waiting, Confirmed, Cancelled }`,
`Tools/BulldozeToolSystem.cs:36-43`):

- `OnUpdate` (`:1456-1538`) lê o raycast do mouse (`GetRaycastResult`, `:1593-1629`) e acumula
  `ControlPoint`s. Ao segurar o botão de aplicar (`applyAction.WasPressedThisFrame`, `:1494`) entra
  em `State.Applying`.
- **Modo de arrasto ao longo de uma rede** (`SnapJob`, `:67-336`): se o primeiro/último ponto é uma
  `Edge`/`Node`, `CreatePath` (`:174-311`) faz uma busca de caminho (min-heap por custo, sem
  aleatoriedade) sobre `ConnectedEdge` para achar a sequência de segmentos entre os dois cliques —
  isso é o que permite "arrastar" a demolição por vários segmentos de rua de uma vez.
- **Confirmação para prédios significativos** (`ConfirmationNeeded`, `:1540-1554`): se a seleção
  inclui um prédio com `Temp.m_Flags & TempFlags.Delete` cujo prefab NÃO é `SpawnableBuildingData`
  (ou É `SignatureBuildingData` — prédio único), o estado vai para `State.Waiting` e dispara
  `EventConfirmationRequested` (`:1513-1519`) — um diálogo de UI local. Só após `ConfirmAction(true)`
  (`:1556-1562`) o estado vira `Confirmed` e `Apply()` roda de fato. **Isto é puramente local**: o
  diálogo não precisa sincronizar nada porque só o resultado final (a entidade ganhando `Deleted`)
  é o que os detectores do mod observam — não há janela de corrida entre PCs aqui.
- `Apply()` (`:1564-1591`) seta `applyMode = ApplyMode.Apply` e chama `DestroyDefinitions` — que
  **não deleta nada sozinho**: ele descarta as entidades de definição de PREVIEW; a demolição real
  acontece via as `CreationDefinition` já emitidas por `UpdateDefinitions`→`CreateDefinitionsJob`
  no frame anterior (ver §2).

### 1.2 — Botão "delete" do painel de informação (SEM ferramenta)
`UI/InGame/ActionsSection.cs:191-205` registra um `TriggerBinding("delete", ...)` genérico para
**qualquer entidade selecionada** (prédio, linha de transporte, veículo, cidadão morto, etc.):
```csharp
m_EndFrameBarrier.CreateCommandBuffer().AddComponent<Deleted>(selectedEntity);
```
Isso **não passa pelo `BulldozeToolSystem`, não cria `CreationDefinition`, não usa `Temp`** — é um
`AddComponent<Deleted>` cru na entidade já real. Toda a cascata (filhos `Owner`, sub-rotas etc.)
depende inteiramente do `SubElementDeleteSystem` (§2) rodar depois no mesmo frame. **Linhas de
transporte são deletadas por este caminho**, não pelo bulldoze (confirma o comentário do mod em
`CS2M/Sync/DeleteDetectorSystem.cs:59`, agora verificado no decomp).

## 2. Fluxo de aplicação

O jogo tem duas fases distintas na sequência de um frame (`SystemUpdatePhase`,
`decomp/Game/Game/SystemUpdatePhase.cs:3-38`, ordem de execução real cross-referenciada via os
registros de fase em `Common/SystemOrder.cs`): **`PreTool → ToolUpdate → [Modification1..
ModificationEnd] → ClearTool → ApplyTool → PostTool`**, todas dentro do mesmo frame antes do
`CleanUpSystem` de fim de frame. Citações de fase, uma por sistema:

| Sistema | Fase | Citação |
|---|---|---|
| `BulldozeToolSystem` | `ToolUpdate` | `SystemOrder.cs:699` |
| `ToolOutputBarrier` (flush das `CreationDefinition`) | fim de `ToolUpdate` | `SystemOrder.cs:692-694` |
| `GenerateObjectsSystem` (consome `CreationDefinition` de prédio/prop/árvore) | `Modification1` | `SystemOrder.cs:95` |
| `GenerateAreasSystem` (consome `CreationDefinition` de área) | `Modification1` | `SystemOrder.cs:98` |
| `GenerateEdgesSystem`/`GenerateNodesSystem` (consome `CreationDefinition` de rede) | `Modification2`/`Modification1` | `SystemOrder.cs:111` / `:96` |
| `GenerateRoutesSystem` | `Modification2` | `SystemOrder.cs:112` |
| `NodeReductionSystem` (reduz nó grau-2 pós-delete, só entidades `Temp`) | `Modification2B` | `SystemOrder.cs:128` |
| `ToolApplySystem`/`ApplyZonesSystem` | `ApplyTool` | `SystemOrder.cs:711-712` |
| `ApplyObjectsSystem` (Temp→Deleted real, objetos) | `ApplyTool` | `SystemOrder.cs:713` |
| `ApplyNetSystem` (Temp→Deleted real, rede) | `ApplyTool` | `SystemOrder.cs:714` |
| `ApplyAreasSystem` | `ApplyTool` | `SystemOrder.cs:716` |
| `ApplyRoutesSystem` | `ApplyTool` | `SystemOrder.cs:718` |
| `SubElementDeleteSystem` (cascata Owner→filhos) | `PostTool` | `SystemOrder.cs:723` |
| `PrepareCleanUpSystem`/`CleanUpSystem` (destrói `Deleted`, limpa tags de fluxo) | fim de `MainLoop` | `SystemOrder.cs:50`; mecânica em `Common/CleanUpSystem.cs:52-53` |

### 2.1 — Caminho do bulldoze de rede/prédio/área (via `CreationDefinition`)
1. `CreateDefinitionsJob.Execute` (`BulldozeToolSystem.cs:428-488`) monta um `NativeHashSet<Entity>
   bulldozeEntities`: no modo de arrasto de rede usa os `ConnectedEdge` entre os `ControlPoint`s
   (`:441-465`); senão, cada `ControlPoint.m_OriginalEntity` direto (`:469-473`).
2. Para cada entidade-alvo, `Execute(...)` (`:490-675`) resolve **cascatas de dono/anexo antes de
   marcar a deleção**:
   - upgrade de serviço → sobe para o dono real (`:514-518`);
   - nó com redes conectadas de camada incompatível (`(componentData2.m_RequiredLayers &
     componentData3.m_RequiredLayers) == 0`) mantém o nó — só remove os edges compatíveis
     (`:590-638`), ou some com o nó inteiro se nada sobrou (`flag` == true, `:634-637`);
   - `Fixed` edges (rede "presa", ex. ferrovia) propagam a deleção node-a-node via
     `AddFixedEdges`/recursão (`:677-727`);
   - objetos anexados (`Attached`/`Attachment`) cujo pai fica "sozinho" viram deleção também
     (`AddAttachedParent`, `:940-999` — este é o único ramo que usa `CreationFlags.Align` em vez de
     `Delete`, ou seja, o pai **não é apagado**, só realinhado);
   - sub-áreas (`SubArea`) e sub-redes (`SubNet`) de um prédio cascadeiam recursivamente
     (`AddSubNets`, `:1002-1095`; sub-áreas em `AddEntity`, `:893-937`).
3. Cada alvo vira uma **entidade de definição nova** (`m_CommandBuffer.CreateEntity()`, `:731`) com
   `CreationDefinition.m_Flags |= CreationFlags.Delete` (`:743-746`) e, conforme o tipo original:
   - `Edge` → `NetCourse` copiado da `Curve`/pontas (`:752-779`);
   - `Node` → `NetCourse` degenerado (comprimento 0) na posição do nó (`:780-800`);
   - objeto com `Transform` (prédio/árvore/prop) → `ObjectDefinition` (`:801-862`);
   - `Area` (buffer `Game.Areas.Node`) → cópia do polígono (`:863-872`), com `CreationFlags.Hidden`
     extra se a área NÃO tem `Game.Areas.Lot` (`:907-910` — área "escondida" em vez de apagada de
     fato, usado para superfícies decorativas que o jogo prefere ocultar).
4. Essas entidades de definição são consumidas em `Modification1`/`Modification2` pelos
   `Generate*System` correspondentes, que **traduzem `CreationFlags.Delete` em `TempFlags.Delete`**
   sobre uma entidade `Temp` cujo `m_Original` aponta para a entidade real:
   - rede: `GenerateEdgesSystem.cs:1355-1358`;
   - objetos: `GenerateObjectsSystem.cs:995-997`;
   - áreas: `GenerateAreasSystem.cs:182-184` (e `Hidden`→`TempFlags.Hidden` em `:212-214`);
   - rotas: `GenerateRoutesSystem.cs:76`.
5. Em `ApplyTool`, o `Apply*System` correspondente lê a entidade `Temp`+`Delete` e faz o delete
   REAL: `AddComponent<Deleted>(temp.m_Original)` **e** `AddComponent<Deleted>(entity)` (a própria
   entidade `Temp` de preview também morre) — código idêntico em espírito nos três:
   - `ApplyNetSystem.HandleTempEntitiesJob.Delete` (`Tools/ApplyNetSystem.cs:561-568`);
   - `ApplyObjectsSystem.cs:342-350`;
   - `ApplyAreasSystem.cs:119,143` (mesmo padrão, com tratamento de `Hidden` em `:169-171`).
   `ApplyNetSystem` também tem `PatchTempReferencesJob`/`FixConnectedEdgesJob`
   (`ApplyNetSystem.cs:23-256`) que corrigem `ConnectedEdge`/`ConnectedNode`/`SubNet` para não
   sobrar referência pendurada para a entidade `Temp` que vai morrer.
6. **Redução de nó grau-2** (`NodeReductionSystem`, roda em `Modification2B`, ANTES de `ApplyTool`):
   opera só sobre nós que são `Temp` (`m_TempData[node]`, `Tools/NodeReductionSystem.cs:87-95`) —
   ou seja, só nós que fazem parte da MESMA operação de ferramenta em andamento. Se apagar uma rua
   deixa um nó com só 2 arestas retas, este sistema funde as duas em uma só (`TempFlags.Combine`,
   `:428,460`). **Isso só acontece no lado que rodou a ferramenta de verdade** (o jogador que
   demoliu) — ver §4 para a implicação cross-machine.
7. `SubElementDeleteSystem` (`Objects/SubElementDeleteSystem.cs:20-384`), em `PostTool`, varre
   entidades que JÁ têm `Deleted` neste frame e cascateia: filhos `SubArea`/`SubNet`/`SubRoute`
   ganham `Deleted` também (`:70-151`); `OwnedVehicle` é deletado via `VehicleUtils.DeleteVehicle`
   (`:152-164`); e para `SubNet` que **não** fica órfão (ainda tem outra aresta viva no nó), em vez
   de apagar ele marca `Updated` e remove o `Owner` (`:125-136` — o nó "sobrevive" como rede
   independente, não como sub-rede do prédio que sumiu).
8. `PrepareCleanUpSystem`/`CleanUpSystem` no fim do `MainLoop` destroem de fato todas as entidades
   com `Deleted` deste frame e removem as tags de fluxo das sobreviventes
   (`Common/CleanUpSystem.cs:47-56`).

### 2.2 — Caminho do botão de painel (linhas de transporte, sem `CreationDefinition`)
`ActionsSection.cs:203` marca `Deleted` direto na `Route`. Não há `Temp`/`Generate*System`
envolvido. A cascata para `RouteWaypoint`/segmentos depende só do passo 7 acima
(`SubElementDeleteSystem` lendo o buffer `SubRoute` da rota, `SubElementDeleteSystem.cs:140-151`) —
**mais um `ElementSystem`** (`Modification2B`, `SystemOrder.cs:132`) que o dossiê não abriu em
detalhe (ver §7, NÃO VERIFICADO).

## 3. Estado persistido tocado

A demolição em si não CRIA estado persistido novo — ela REMOVE entidades e seus componentes
`ISerializable`. O que precisa convergir é **quais entidades deixam de existir** e **o estado dos
sobreviventes que a cascata tocou**:

- a entidade demolida em si desaparece (Entity destruída — não há mais componente a comparar);
- `Edge`/`Node` vizinhos que sobrevivem: `Curve` e o próprio `Node.m_Position`/`m_Rotation` podem
  mudar (rebuild da geometria da junção — tampa de via, remoção de faixa);
- buffers `ConnectedEdge`/`ConnectedNode`/`Game.Net.SubNet` dos nós/prédios vizinhos (ajustados por
  `PatchTempReferencesJob`/`FixConnectedEdgesJob`, `ApplyNetSystem.cs:23-256`);
  `Game.Areas.SubArea`/`Game.Buildings.InstalledUpgrade` do dono, quando um upgrade/sub-área some;
- `Recent` (custo de reembolso, `ApplyNetSystem.cs:646-685` / `ApplyObjectsSystem`-equivalente) —
  **não crítico para convergência de mundo**, é só telemetria de UI/economia local;
- para linhas de transporte: o próprio componente `Route`/`RouteWaypoint`/`RouteSegment` some.

## 4. Perigos cross-machine

1. **`NodeReductionSystem` roda só no lado que executou a ferramenta** (`NodeReductionSystem.cs:87
   -95` — filtra por `Temp`, que só existe na sessão de ferramenta local). O receptor do comando de
   rede nunca roda o `BulldozeToolSystem` — ele só marca `Deleted` direto via `EntityManager`
   (`CS2M/Sync/NetEditApplySystem.cs:210-223` / `NetBatchApplySystem.cs:454-482`). Ou seja: se
   apagar uma rua deixa um nó grau-2 que o jogo reduziria (fundindo duas arestas em uma), **o lado
   que demoliu funde; o lado que recebeu o comando NÃO** — ele só marca os vizinhos `Updated`
   (`RebuildAfterDelete`, `NetEditApplySystem.cs:229-260`), sem rodar a fusão real. É uma
   aproximação manual de um sistema vanilla que o receptor não tem como acionar sem reimplementar a
   ferramenta inteira. **Confirmado como aproximação, não solução completa** — risco de geometria de
   junção divergente após demolições que deixam pontas retas.
2. **Identidade de nó (`CS2M_NodeSyncId`) só existe para nós plantados NESTA sessão**
   (`CS2M_NodeSyncId.cs:76-94`, `Ensure` só carimba quando o sender constrói o edge). Nós
   carregados do save (ou ainda não "tocados" por ninguém) têm `m_Id == 0` — o delete de rede então
   cai no fallback por posição, `FindEdge` (`NetEditApplySystem.cs:309-347`), que usa raio ~10 m por
   ponta (`float best = 200f` = soma de dois quadrados de ~10 m, comentário próprio do mod admite
   "duas ruas a poucos metros → pode cair na errada" tolerância ampliada de propósito). **Perigo
   real**: mapa com muita rede pré-existente (save antigo) + demolição perto de junções próximas
   pode apagar o segmento errado.
3. **Deleção de ÁREA autônoma (distrito/superfície pintada) não tem identidade nenhuma** — só
   prefab + centro do polígono, raio de tolerância `100f` (≈10 m ao quadrado,
   `CS2M/Sync/AreaEditSystems.cs:872` `ApplyDelete`/`FindAreaByCenter` `:806-868`). Duas áreas do
   MESMO prefab com centros a menos de 10 m uma da outra (comum em decks pequenos de decoração)
   colidem na resolução — sem qualquer id estável.
4. **Deleção de objeto NATIVO (sem `CS2M_SyncId`, do save) também é só prefab + posição**, raio
   "exato" 2 m / "solto" 1 m (`RemoteEditApplySystem.cs:152-195`, `FindNative`). Mesmo problema:
   dois props idênticos próximos podem trocar de alvo.
5. **`CreationFlags.Hidden` para sub-áreas sem `Lot`** (`BulldozeToolSystem.cs:907-910`,
   `GenerateAreasSystem.cs:212-214`) é um terceiro estado (nem viva, nem `Deleted`) que o mod não
   parece distinguir explicitamente do delete puro no lado do envio (`AreaEditSystems.cs` só manda
   `Delete=true` binário, sem carregar se era esconder-vs-apagar) — ver §6.
6. **RNG/seed**: a própria demolição não usa `Random`, mas `GenerateEdgesSystem`/`GenerateObjectsSystem`
   também processam CRIAÇÕES na mesma passada (o `PseudoRandomSeed`/seed de árvore/poste é só
   relevante para criação, não para delete — não é um perigo desta mecânica especificamente, mas
   compartilha o sistema).
7. **`RouteNumber` como chave de fallback para deletar linha de transporte**
   (`RouteSyncSystems.cs:150-184`, usado por `RemoteEditApplySystem.ApplyRouteDelete`) não é um id
   globalmente único — é o número da linha (ex. "Linha 3"). Se uma linha é apagada e outra do MESMO
   prefab reusar o número antes do comando de delete chegar (janela de rede lenta), o delete pode
   acertar a linha errada. Estreito, mas existe.
8. **Botão de painel (§1.2, §2.2) é um `AddComponent<Deleted>` cru sem NENHUMA das checagens de
   cascata de `CreateDefinitionsJob`** (owner walk, `Fixed`, `SubNet`/`SubArea` recursivo) — toda a
   responsabilidade de cascata fica só no `SubElementDeleteSystem` de `PostTool`. Isso é vanilla-
   correto no lado que clicou (o jogo cascade sozinho), mas no lado RECEPTOR o mod (`CascadeDeleteUtil`)
   reimplementa essa cascata NA MÃO varrendo TODAS as entidades com `Owner` do mundo a cada delete
   remoto (`CascadeDeleteUtil.cs:17-63`, `EntityQuery` sem filtro de prefab/tipo, 3 passadas) — custo
   O(entidades-com-Owner) por delete remoto, e é uma reimplementação paralela da lógica vanilla, não
   o vanilla em si (viola a letra da lei "nunca criar/objeto complexo na mão", embora aqui seja só
   tag `Deleted`, não criação de entidade — risco menor, mas ainda divergência de comportamento
   possível se a regra vanilla exata de cascata tiver um caso de borda o `IsUnder`/4-hops não cobre).

## 5. O que o CS2M faz hoje

Dois pares detector→apply cobrem os alvos, mais um caminho experimental:

- **Objetos/prédios/props/árvores + linhas de transporte** (`CS2M/Sync/DeleteDetectorSystem.cs`):
  - `_deletedQuery` (`:38-57`) pega qualquer coisa com `CS2M_SyncId` (objeto sincronizado antes)
    que ganhou `Deleted`, manda `DeleteCommand{SyncId}` (`:117-136`);
  - `_deletedNativeQuery` (`:79-99`) pega objeto SEM `CS2M_SyncId` (do save) com `Static`/`Building`
    — endereça por prefab+posição; **gateado ao bulldoze estar ativo para growables**
    (`DetectNativeDeletes`, `:212-278`, para não sincronizar demolição de abandonado pela SIM local);
  - `_deletedRouteQuery` (`:63-76`) pega `Route`+`TransportLine` deletada — SyncId ou
    prefab+`RouteNumber` (`DetectRouteDeletes`, `:142-210`).
  - Aplicado em `CS2M/Sync/RemoteEditApplySystem.cs:68-113` (`ApplyDelete`) — resolve por
    `CS2M_SyncId` ou `FindNative` (proximidade), tag `CS2M_RemotePlaced` (guarda de eco), e
    **cascata manual** via `CascadeDeleteUtil.DeleteWithChildren` (`CascadeDeleteUtil.cs:17-63`).
  - Linhas: `ApplyRouteDelete` (`RemoteEditApplySystem.cs:117-150`) via `RouteResolver`
    (`RouteSyncSystems.cs:148-185`).
- **Rede (rua/cano/trilho)** (`CS2M/Sync/NetEditDetectorSystem.cs:22-153`): detecta `Edge`+`Deleted`
  sem `Owner` (exclui sub-redes de prédio, que cascateiam sozinhas em cada PC), manda
  `NetDeleteCommand` com posição das DUAS pontas **e** `CS2M_NodeSyncId` de cada ponta quando
  disponível (`:130-143`). Aplica em `CS2M/Sync/NetEditApplySystem.cs:191-224`
  (`ApplyDelete`) — **identidade primeiro** (`FindEdgeById`, `:273-307`), fallback posição
  (`FindEdge`, `:309-347`), e faz a cascata aproximada de junção (`RebuildAfterDelete`, `:229-260`,
  §4.1).
- **Áreas (distrito/superfície/lote de trabalho)** (`CS2M/Sync/AreaEditSystems.cs`):
  `AreaEditDetectorSystem.DetectDeleted` (`:437-514`) manda `AreaEditCommand{Delete=true}` por
  prefab+centro; **filtra deleção de sub-área cujo dono ainda vive** (edição real) da **cascata do
  dono morrendo** (não resincroniza, `:454-478` — mesmo dono já sincroniza sua própria morte).
  Aplicado em `AreaEditApplySystem.ApplyDelete` (`:870-886`), resolução só por
  `FindAreaByCenter` (proximidade, sem id).
- **Caminho experimental `CS2M_ATOMIC=1`** (`NetBatchCaptureSystem.cs`/`NetBatchApplySystem.cs`):
  quando ligado, `NetEditDetectorSystem` se desliga (`NetEditDetectorSystem.cs:61-64`) e os deletes
  de aresta entram no MESMO `NetBatchCommand` da construção, endereçados **só por par de nó-id**
  (`NetBatchCaptureSystem.cs:316-341`), **sem fallback de posição na aplicação**
  (`NetBatchApplySystem.cs:454-482`, `FindEdgeById` só — se falhar, `SKIP noMatch`, não tenta
  proximidade). É mais estrito (nunca aplica no lugar errado) mas também mais frágil (nunca aplica
  se a identidade não resolver) — ainda não é o caminho padrão (memória do projeto:
  "AtomicBatch HÍBRIDO validado na tela... flag CS2M_ATOMIC=1").
- Guardas de eco: `CS2M_RemotePlaced` (vida inteira da entidade, objetos/áreas) e `CS2M_RemoteDeleted`
  (só no frame da morte, rede sob AtomicBatch — `CS2M/Sync/CS2M_RemoteDeleted.cs:1-17`) e
  `RemoteNetEcho.Mark`/`IsRecent` (hash de segmento, `NetEditApplySystem.cs:90,138,206`) evitam que
  um delete aplicado seja capturado de novo pelo detector local.

## 6. GAPS e recomendação

Checklist do que falta ou está frágil, em ordem de risco:

1. **Rede: sem fallback de identidade para NÓS pré-existentes (save) na resolução por posição.**
   `NetEditApplySystem.FindEdge` usa 10 m por ponta quando `CS2M_NodeSyncId` é 0 — em mapas com rede
   densa próxima a junções, isso pode apagar o segmento vizinho errado. Recomendação: ao primeiro
   toque de qualquer comando (upgrade/delete/criação) num nó de save, fazer "first-touch identity"
   (o mod já faz isso para objetos nativos em `MoveCommand`/`RemoteEditApplySystem.cs:253-258`, mas
   não parece existir o equivalente para nós de rede pré-existentes — ver NÃO VERIFICADO).
2. **`NodeReductionSystem` não roda no receptor** (§4.1) — geometria de junção pós-delete pode
   divergir sutilmente (ponta reta vs. nó grau-2 residual). Um StateHash de topologia de rede
   (contagem de nós por grau, não só posição) pegaria isso; hoje o hash de rede provavelmente só
   olha entidades vivas, não a FORMA da junção (ver NÃO VERIFICADO).
3. **Deleção de área autônoma e de objeto nativo são 100% proximidade, sem qualquer id.** Diferente
   da rede (que ganhou `CS2M_NodeSyncId`), áreas e objetos-de-save nunca recebem um id estável
   antes do primeiro delete — o delete É o primeiro (e único) contato. Não há como fazer
   "first-touch" para um delete (a entidade já era para morrer). Recomendação: se a demolição por
   posição falhar mais de uma vez de forma mensurável em playtest, considerar carimbar `CS2M_SyncId`
   em TODO objeto/área nativo relevante já em `PlacementDetectorSystem`/scan de mundo no load, não
   só quando movido/deletado.
4. **`CascadeDeleteUtil.DeleteWithChildren` varre TODAS as entidades com `Owner` do mundo a cada
   delete remoto** (`CascadeDeleteUtil.cs:28-32`, sem filtro por sub-árvore) — custo cresce com o
   tamanho da cidade; em demolições em lote (bulldoze arrastado por uma rua com muitos prédios
   ligados) isso é uma query completa repetida por prédio. Recomendação: uma passada única
   coletando todos os `Deleted` do frame primeiro, depois um single-pass de `Owner` (o que o
   `SubElementDeleteSystem` vanilla já faz de graça, `SubElementDeleteSystem.cs:36-165`) — avaliar
   se dá para confiar SÓ no vanilla aqui em vez de reimplementar.
5. **Confirmação de prédio significativo (`ConfirmationNeeded`, §1.1) é só local — correto**, mas
   não há evidência no mod de tratamento explícito do CASO em que o prédio já foi demolido
   remotamente ENQUANTO o diálogo de confirmação local está aberto (`State.Waiting`) — o `Apply()`
   eventual rodaria sobre uma entidade já morta. Prático mas não confirmado como testado (NÃO
   VERIFICADO).
6. **`CreationFlags.Hidden` (sub-área sem `Lot`) vs. delete puro** (§4.5): `AreaEditCommand` manda
   só `Delete=true` binário — não está claro que o mod distingue "esconder" de "apagar de verdade"
   ao replicar. Se a superfície escondida for reexibida depois (não é um delete permanente no
   vanilla), o mod pode ter apagado o que deveria só ter ficado invisível. Precisa de teste
   dedicado (NÃO VERIFICADO se este caso realmente aparece em jogo fora do editor).
7. **AtomicBatch (`CS2M_ATOMIC=1`) para delete de rede é estrito demais (sem fallback de posição)**
   — bom para nunca errar o alvo, mas se a identidade falhar (nó ainda não resolvido no receptor,
   ordem de mensagens), o delete simplesmente não aplica (`SKIP noMatch`,
   `NetBatchApplySystem.cs:460-461`) e o mundo diverge silenciosamente (o segmento fica vivo num
   PC e morto no outro) até o próximo `/resync`. Recomendação, se este caminho virar padrão:
   adicionar RETRY (como já existe para áreas, `AreaEditApplySystem._pendingAreas`,
   `AreaEditSystems.cs:574-668`) em vez de descartar de primeira.

## 7. NÃO VERIFICADO

- A ordem EXATA de execução entre as fases `ToolUpdate`/`Modification1..ModificationEnd`/
  `ClearTool`/`ApplyTool`/`PostTool` dentro de um frame não está declarada como uma sequência
  explícita em nenhum arquivo lido — foi **inferida** das relações produtor→consumidor
  (`CreationDefinition` fica pronta no fim de `ToolUpdate`; `Generate*System` está em
  `Modification1`/`2`; `Apply*System` está em `ApplyTool`; isso só faz sentido se `Modification*`
  roda ENTRE `ToolUpdate` e `ApplyTool`). Não achei o dispatcher que define a ordem linear de
  `SystemUpdatePhase` no frame.
- Não confirmei em detalhe o `ElementSystem` (`Modification2B`, `SystemOrder.cs:132`) que
  supostamente cascateia `RouteWaypoint`/segmentos de uma `Route` deletada pelo botão do painel
  (§2.2) — só vi a entrada no registro de fases, não o corpo do sistema.
  citação pendente: `Game.Routes.ElementSystem` (arquivo não aberto).
- Não confirmei se existe "first-touch identity" para NÓS DE REDE pré-existentes (save) equivalente
  ao que existe para objetos nativos (`RemoteEditApplySystem.cs:253-258`, `nativeFirstTouch`). Se
  não existir, o gap #1 da §6 é permanente; se existir em outro arquivo do mod não lido, o gap é
  menor do que documentado.
- Não abri o `Game.Routes.ElementSystem`/`RoutePathSystem`/`RoutesModifiedSystem` para confirmar
  como waypoints e segmentos de uma linha de transporte são exatamente recriados/removidos — o
  dossiê descreve o contrato observável (Route ganha Deleted → SubRoute cascade) mas não o
  mecanismo interno de regeneração de trajeto.
- Não confirmei se o `StateHash`/detector de divergência do CS2M inclui grau/topologia de nó (que
  pegaria o gap #2 da §6) ou só posição/contagem de entidades — não abri os arquivos de
  `StateHashSystems` para esta mecânica especificamente.
- Não testei em jogo (2 sims) nenhum dos cenários de demolição — todo o dossiê é leitura estática
  de código (jogo + mod), sem validação na tela como a "Lei do Bruno" exige para fechar um item.
