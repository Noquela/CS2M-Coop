# Objetos/Prédios colocados (services, props, árvores) — dossiê de sync

> Fonte: `decomp/Game/Game/Tools/ObjectToolSystem.cs`, `ObjectToolBaseSystem.cs`, `ObjectDefinition.cs`,
> `CreationDefinition.cs`, `CreationFlags.cs`, `ApplyObjectsSystem.cs`, `GenerateObjectsSystem.cs`,
> `ToolUtils.cs`; `decomp/Game/Game/Objects/{Transform,Attached,Elevation,Tree,Plant,Object,Static}.cs`,
> `ObjectUtils.cs`; `decomp/Game/Game/Buildings/{Building,RoadConnectionSystem}.cs`;
> `decomp/Game/Game/Common/{RandomSeed,PseudoRandomSeed}.cs`; `decomp/Game/Game/Prefabs/PrefabRef.cs`;
> `decomp/Game/Game/UI/InGame/ActionsSection.cs`. Conferido contra o código do mod em
> `CS2M/Sync/{PlacementDetectorSystem,RemotePlacementApplySystem,GrowableSyncSystems,MoveDetectorSystem,
> DeleteDetectorSystem,RemoteEditApplySystem,CS2M_SyncIdSystem,SyncContract}.cs`,
> `CS2M/Commands/Data/Game/ObjectPlaceCommand.cs`, `CS2M/Commands/Handler/Game/ObjectPlaceHandler.cs`,
> `CS2M/Mod.cs`.

## 1. Entradas do jogador

**`ObjectToolSystem : ObjectToolBaseSystem : ToolBaseSystem`** (Tools/ObjectToolSystem.cs:33) é a
ferramenta que coloca prédios de serviço, props, árvores e "signature buildings". Tem 7 modos
(Tools/ObjectToolSystem.cs:35-44): `Create, Upgrade, Move, Brush, Stamp, Line, Curve`.

- **Colocação simples** (`Mode.Create`): a cada frame com o tool ativo, `OnUpdate` (Tools/ObjectToolSystem.cs:3217)
  despacha para `Apply(...)` quando `base.applyAction.WasPressedThisFrame()`/`WasReleasedThisFrame()`
  dispara (Tools/ObjectToolSystem.cs:3342-3396, e ramo genérico em 3878-3920). `Cancel(...)` roda no
  `cancelAction` (Tools/ObjectToolSystem.cs:3378-3384).
- **Relocar/mover um prédio já existente** (`Mode.Move`): o gatilho de UI é o botão "Move" do painel
  do objeto selecionado — `TriggerBinding(group, "toggleMove", ...)` chama
  `m_ObjectToolSystem.StartMoving(selectedEntity)` e troca `m_ToolSystem.activeTool` para o Object Tool
  (UI/InGame/ActionsSection.cs:163-176). `StartMoving` seta `m_MovingObject = movingObject` e
  `mode = Mode.Move` (Tools/ObjectToolSystem.cs:2993-3002); daí em diante o mesmo pipeline de
  `Create` roda, mas com `original = m_MovingObject` (ver §2).
- **Brush** (pintar várias árvores/props numa área, ex. floresta): `CreateBrushes`
  (Tools/ObjectToolBaseSystem.cs:783-905+) roda a cada frame de drag, gerando 0..N colocações por
  frame conforme densidade/opacidade da textura do brush.
- **Line/Curve** (fileira de postes/árvores ao longo de uma curva): `CreateCurve`
  (Tools/ObjectToolBaseSystem.cs:682-781) calcula N pontos espaçados por `m_Distance` ao longo de um
  Bezier e chama `UpdateObject` uma vez por ponto (linha 774).
- **Stamp** (Asset Stamp / conjuntos pré-fabricados): ramo `flag = m_PrefabAssetStampData.HasComponent(objectPrefab)`
  em `UpdateObject` (Tools/ObjectToolBaseSystem.cs:1092-1093, 1253-1267) — em vez de criar 1 definição,
  expande recursivamente os `Game.Prefabs.SubObject` do prefab (linha 1255-1264).
- Todo frame com o tool ativo (preview OU commit) roda `UpdateDefinitions()`
  (Tools/ObjectToolSystem.cs:4241-4326), que agenda `CreateDefinitions(...)` (linha 4319) — a
  diferença entre preview e commit é só o `ApplyMode` (`Clear` vs `Apply`, ex. linha 3903).
- No commit, `Randomize()` re-sorteia `m_RandomSeed = RandomSeed.Next()` (Tools/ObjectToolSystem.cs:3005-3007,
  chamado em 3853/3904/4002 etc.) — ver §4 para por que isso importa.

## 2. Fluxo de aplicação

### Passo 1 — CreateDefinitionsJob (definição, não a entidade real)

`CreateDefinitionsJob.Execute()` (Tools/ObjectToolBaseSystem.cs:446-643) NUNCA cria o objeto de
verdade — cria uma entidade de **definição** (`CreationDefinition` + `ObjectDefinition` + `Updated`)
via `EntityCommandBuffer`, chamando `UpdateObject(...)` (linha 1088) para o caso comum de colocação
nova (linha 626), ou recursivamente para sub-objetos de um Asset Stamp (linha 1255-1264) e para
"consertar" a topologia de rede vizinha quando o objeto se anexa a um edge/node
(`UpdateAttachedParent`, linhas 1283-1380 — emite `CreationDefinition{m_Flags=Align}` + `NetCourse`
para religar o pedaço de rua tocado).

`UpdateObject` (Tools/ObjectToolBaseSystem.cs:1088-1281) monta:
- `CreationDefinition` (Tools/CreationDefinition.cs:5-20): `m_Prefab/m_SubPrefab/m_Original/m_Owner/
  m_Attached` são todos `Entity` — **ids locais à máquina**, válidos só dentro do frame de definição —
  e `m_Flags` (`Tools/CreationFlags.cs:6-28`: `Permanent/Select/Delete/Attach/Upgrade/Relocate/Align/
  Parent/Dragging/Optional/Lowered/Duplicate/Repair/Stamping/...`).
- `ObjectDefinition` (Tools/ObjectDefinition.cs:6-31): `m_Position/m_Rotation` (transform de mundo —
  cross-machine estável), `m_Elevation`, `m_Age`, `m_Scale/m_Intensity`, e os índices de variação
  visual `m_ParentMesh/m_GroupIndex/m_Probability/m_PrefabSubIndex`.
- `relocate` (o parâmetro booleano de `UpdateObject`) é `true` sempre que existe um `original`
  (`bool flag = entity2 != Entity.Null` em `CreateDefinitionsJob.Execute`, Tools/ObjectToolBaseSystem.cs:455,
  passado como o argumento `relocate` na chamada de `UpdateObject` na linha 626) — é exatamente esse
  caminho que `Mode.Move` usa (com `m_MovingObject` como `m_Original` desde
  `CreateDefinitions(..., m_MovingObject, ...)`, Tools/ObjectToolSystem.cs:4319).

Diagrama (colocação nova, sem upgrade/anexo):

```
Jogador aperta Apply (applyAction)                              [Tools/ObjectToolSystem.cs:3878-3920]
  -> Randomize(): m_RandomSeed = RandomSeed.Next()               [ObjectToolSystem.cs:3005-3007]
  -> UpdateDefinitions(): CreateDefinitions(..., m_RandomSeed,..) [ObjectToolSystem.cs:4241-4319]
    -> CreateDefinitionsJob.Execute()                            [ObjectToolBaseSystem.cs:446-643]
       -> UpdateObject(objectPrefab, ..., transform, ...)        [ObjectToolBaseSystem.cs:1088]
          cria entidade DEFINIÇÃO com:
            CreationDefinition{ m_Prefab, m_RandomSeed=random.NextInt() }  [linha 1096-1101]
            ObjectDefinition{ m_Position, m_Rotation, m_Age=GetRandomAge(random,...) } [linha 1114-1163]
            Updated                                              [linha 1251]
```

### Passo 2 — GenerateObjectsSystem materializa o preview (Temp)

`GenerateObjectsSystem` (Tools/GenerateObjectsSystem.cs) lê as definições (`CreationData`, linhas
29-54) e roda `CreateObject` (linhas 960-1173+) toda vez que a definição existe (ou seja, todo frame
com o tool ativo) — isso produz uma entidade `Temp` + `Object`/`Static` (preview), OU atualiza a
entidade `oldEntity` já existente (linha 1100 em diante) quando há relocate/upgrade:

- `Unity.Mathematics.Random random = m_RandomSeed.GetRandom(jobIndex)` (linha 969) — **outro** random
  derivado do `RandomSeed` do job, usado para valores auxiliares (não o principal — o principal já
  veio pronto em `definitionData.m_RandomSeed`).
- Se o prefab tiver `TreeData` e não houver `Tree` prévio, `componentData2 =
  ObjectUtils.InitializeTreeState(objectDefinition.m_Age)` (linhas 973-978; `InitializeTreeState` em
  Objects/ObjectUtils.cs:419-447) — converte a idade (0..1) num par `(TreeState, m_Growth byte)`.
- `component2.m_Value`/`m_Cost` (construção/relocate/upgrade/reforma) calculados a partir de
  `PlaceableObjectData.m_ConstructionCost` ou `ServiceUpgradeData.m_UpgradeCost` (linhas 982-1086),
  usando `Recent` (se existir) para achar o custo/reembolso decaído no tempo (`ObjectUtils.Get*Cost`,
  chamadas em 1020/1046/1050/1062/1066).
- Se `flag = m_PrefabObjectData.TryGetComponent(...)` (o prefab tem `PseudoRandomSeed` no seu
  archetype), estampa `PseudoRandomSeed((ushort)definitionData.m_RandomSeed)` na entidade preview/old
  (linhas 1163-1173) — **é aqui que o seed vindo da definição vira o seed persistido do objeto**.

### Passo 3 (só no commit) — ApplyObjectsSystem promove Temp → estado real

`ApplyObjectsSystem` (query `ReadOnly<Temp>+ReadOnly<Object>`, Tools/ApplyObjectsSystem.cs:920,
`RequireForUpdate` linha 925) roda `HandleTempEntitiesJob` (linha 327+):

- Colocação nova (`temp.m_Original == Entity.Null`, sem `Delete/Cancel`): `Create(...)` (linhas
  669-685) remove os componentes de animação-temp, e SE `temp.m_Cost > 0` adiciona `Recent{
  m_ModificationFrame = simFrame, m_ModificationCost = temp.m_Cost }` (linhas 676-682); depois estampa
  `m_AppliedTypes = {Applied, Created, Updated}` (linha 684, conjunto montado em `OnCreate`,
  linha 922) — **`Applied` é a tag que o detector do mod usa** (§5). Dispara também
  `TriggerAction(TriggerType.ObjectCreated, ...)` (linhas 433-438) quando a entidade tem `PrefabRef`.
- Relocate/upgrade de um `original` já existente (`m_PrefabRefData.HasComponent(temp.m_Original)`,
  linha 354): copia `Attached/Elevation/LocalTransformCache/SubNets/SubAreas/SubLanes/SubObjects` do
  Temp para o `original` (linhas 365-411), atualiza o `Transform` só se mudou (`Update(..., transform)`,
  linhas 607-661) e marca o `original` com `Updated` (não `Applied` — linha 594).

### Fases (ECS)

O comentário do próprio mod (`CS2M/Mod.cs:123-134`) documenta a ordem observada em produção:
`GenerateObjectsSystem` roda em `Modification1`; o detector do CS2M roda logo antes de
`ModificationEnd` (mesmo slot que a `AnarchyPlopSystem` verificada, comentário em
`PlacementDetectorSystem.cs:28-29`). **NÃO VERIFICADO no decomp**: os atributos `[UpdateInGroup]/
[UpdateBefore]/[UpdateAfter]` de `ApplyObjectsSystem`/`GenerateObjectsSystem` em si não sobreviveram
à decompilação desta cópia — a ordem de fases acima vem da documentação do mod (observação em jogo),
não de uma citação direta de atributo no decomp.

### O que acontece depois de `Created` (derivado, cada PC recalcula sozinho)

`RoadConnectionSystem` reage a `Building` com `Updated`/`Deleted` (query em
Buildings/RoadConnectionSystem.cs:2182-2197) e recalcula `Building.m_RoadEdge`/`m_CurvePosition`
localmente a partir da geometria de rede próxima — **isso nunca é sincronizado pelo fio**; funciona
por construção só enquanto a malha viária já convergiu entre as máquinas (ver §6).
`ObjectToolBaseSystem.UpdateSubObjects/UpdateSubNets/UpdateSubAreas` (chamadas em
Tools/ObjectToolBaseSystem.cs:1274-1276) percorrem os buffers `Game.Prefabs.SubObject/SubNet/SubArea`
do prefab e emitem MAIS definições — cada PC deriva os próprios sub-objetos/sub-redes/sub-áreas do
mesmo prefab (ver GAME_SYSTEMS.md:153-157, já documentado como "não se sincroniza por design").

## 3. Estado persistido tocado

| Componente | ISerializable? | Campos relevantes | Citação |
|---|---|---|---|
| `Game.Objects.Transform` | sim | `m_Position` (float3), `m_Rotation` (quaternion) | Objects/Transform.cs:8-50 |
| `Game.Objects.Attached` | sim (mas só serializa `m_Parent`+`m_CurvePosition`, não `m_OldParent`) | `m_Parent` (Entity!), `m_CurvePosition` (float) | Objects/Attached.cs:6-32 |
| `Game.Objects.Elevation` | sim | `m_Elevation` (float), `m_Flags` (ElevationFlags) | Objects/Elevation.cs:6-33 |
| `Game.Common.PseudoRandomSeed` | sim | `m_Seed` (ushort) — alimenta cor/malha/estado de N sistemas (`kColorVariation, kMeshGroup, kBuildingState, kSubObject...`) | Common/PseudoRandomSeed.cs:7-80 |
| `Game.Objects.Tree` | sim | `m_State` (TreeState), `m_Growth` (byte) | Objects/Tree.cs:6-24 |
| `Game.Objects.Plant` | sim | `m_Pollution` (float) — não é crescimento, é poluição acumulada | Objects/Plant.cs:6-19 |
| `Game.Buildings.Building` | sim | `m_RoadEdge` (Entity!), `m_CurvePosition`, `m_OptionMask`, `BuildingFlags` | Buildings/Building.cs:6-38 |
| `Game.Tools.Recent` | sim | `m_ModificationFrame` (uint), `m_ModificationCost` (int) — base do reembolso de demolição/relocate/upgrade | Tools/Recent.cs:6-23 |
| `Game.Prefabs.PrefabRef` | sim | `m_Prefab` (Entity!) — precisa resolução por identidade estável (nome+tipo+hash), NUNCA por índice (GAME_SYSTEMS.md:78-89) | Prefabs/PrefabRef.cs |
| `Game.Objects.Object`/`Game.Objects.Static` | sim (vazios, `IEmptySerializable`) | marcadores de arquétipo | Objects/Object.cs:8, Objects/Static.cs:8 |

## 4. Perigos cross-machine

1. **`RandomSeed` é semeado por `DateTime.Now.Ticks`, por processo** — `RandomSeed.Next()`
   (Common/RandomSeed.cs:12-17) puxa de `m_Random = new Unity.Mathematics.Random((uint)DateTime.Now.Ticks)`
   (Common/RandomSeed.cs:8), um `static` por-máquina. Toda vez que o jogador aplica uma colocação,
   `Randomize()` chama esse `Next()` (Tools/ObjectToolSystem.cs:3005-3007) e o valor deriva:
   - `CreationDefinition.m_RandomSeed = random.NextInt()` (Tools/ObjectToolBaseSystem.cs:1091,1101) →
     `PseudoRandomSeed` persistido (Tools/GenerateObjectsSystem.cs:1163-1173) → cor/malha/variação
     visual (Common/PseudoRandomSeed.cs:9-49 lista as ~20 razões que o usam).
   - `ObjectDefinition.m_Age = ToolUtils.GetRandomAge(ref random, m_AgeMask)`
     (Tools/ObjectToolBaseSystem.cs:1156-1163; implementação em Tools/ToolUtils.cs:148-170) → estado
     inicial de árvore (`ObjectUtils.InitializeTreeState`, Objects/ObjectUtils.cs:419-447).
   - Quando o prefab tem `PlaceholderObjectElement` (bucket de variantes — ex. props de fazenda), QUAL
     sub-prefab entra é decidido por um sorteio ponderado contra
     `m_RandomSeed.GetRandom(1000000)` (Tools/ObjectToolBaseSystem.cs:609-625, repetido por ponto em
     `CreateCurve`, linhas 710-767) — **decidido ANTES da entidade existir**, então o
     nome do prefab que sai já é o resultado do sorteio local; não há re-sorteio a fazer no receptor
     (mitigado por construção, não precisa de campo extra).
2. **`Recent` não nasce na entidade colocada por comando remoto** — `ApplyObjectsSystem.Create()`
   só adiciona `Recent` quando `temp.m_Cost > 0` (Tools/ApplyObjectsSystem.cs:676-682); isso roda
   SÓ no pipeline vanilla local. `Objects/ObjectUtils.cs:356-371 GetRefundAmount` decai o reembolso
   de demolição/relocate/upgrade a partir de `Recent.m_ModificationFrame`/`m_ModificationCost` — sem
   `Recent`, o reembolso cai pro default (ver §6).
3. **`Attached.m_Parent`/`Building.m_RoadEdge` são `Entity`** (per-machine) — serializam para
   save/load (o jogo remapeia ids no load), mas não podem ir cru pela rede (Objects/Attached.cs:8,23;
   Buildings/Building.cs:8,18).
4. **`m_ParentMesh`/`m_GroupIndex`/`m_Probability`/`m_PrefabSubIndex`** só são setados a partir de
   `LocalTransformCache` **em modo editor** (`if (m_EditorMode...)`, Tools/ObjectToolBaseSystem.cs:1151-1162)
   — fora do editor, ficam nos defaults calculados pelo próprio `UpdateObject` a partir do prefab
   (não há sorteio extra de jogador aqui em modo de jogo normal).
5. **Ordem de iteração de `m_ObjectSearchTree` / brush cells** no `CreateBrushes`
   (Tools/ObjectToolBaseSystem.cs:783-905+) processa árvores da textura do brush célula-a-célula com
   um `Unity.Mathematics.Random random = m_RandomSeed.GetRandom(index)` **por-célula** (linha 119 do
   `BrushIterator`, dentro de `CreateDefinitionsJob`, Tools/ObjectToolBaseSystem.cs:119) — mesmo
   `RandomSeed` de origem não-determinística; cada célula pintada terá variação diferente
   se o RandomSeed não for sincronizado (mitigado da mesma forma que o item 1, via `ObjectPlaceCommand.RandomSeed`
   por objeto individual).
6. **`RoadConnectionSystem` recalcula `Building.m_RoadEdge` localmente** a partir da geometria de
   rede mais próxima (Buildings/RoadConnectionSystem.cs:2182-2197) — determinístico SÓ SE a malha
   viária já é idêntica nas duas máquinas; numa área com múltiplos edges quase equidistantes, um
   drift de rua (mesmo pequeno) pode fazer as duas máquinas escolherem edges de acesso diferentes.

## 5. O que o CS2M faz hoje

- **Comando**: `ObjectPlaceCommand` (CS2M/Commands/Data/Game/ObjectPlaceCommand.cs) carrega: id
  cross-PC (`SyncId`), identidade de prefab machine-independent (`PrefabType`+`PrefabName`+hash),
  `Transform` completo, `RandomSeed` (comentário na própria classe: "keeps color/mesh variation
  identical", linha 48), `Elevation`+`ElevationFlags`, dados de extensão de prédio (`OwnerSyncId/
  OwnerPrefabName/OwnerX/Y/Z`), `Source` (0=jogador, 1=sim do host) e, desde v55, `HasTree/TreeState/
  TreeGrowth` explícitos (linhas 71-78) — **fecha exatamente o perigo #1 (idade→estado de árvore)**
  citado acima, em vez de depender de recalcular a idade no receptor.
- **Detector (emissor)**: `PlacementDetectorSystem` (CS2M/Sync/PlacementDetectorSystem.cs) usa
  `Applied` como sinal autoritativo (linhas 48-78, cópia verificada da query da Anarchy), exclui
  `Owner` (sub-objeto derivado), animais/veículos/cidadãos e elementos de rota. Lê o seed direto do
  componente `PseudoRandomSeed` da entidade JÁ criada (`ReadSeed`, linhas 321-329) — não recalcula
  nada, só espelha o valor que o pipeline local já decidiu. Trata extensões de prédio (Owner + prefab
  com `ServiceUpgradeData`/`BuildingExtensionData`) num segundo query (`_appliedExtensions`, linhas
  83-97) filtrando explicitamente sub-objetos derivados (comentário v51, linhas 252-264).
- **Sim-driven growables**: `GrowableDetectorSystem` (CS2M/Sync/GrowableSyncSystems.cs:29-108) detecta
  `Created+Building` SEM `Applied` (prédios que a simulação de zona gerou sozinha) e os manda como
  `Source=1`; `GrowableSuppressSystem` (linhas 115-149) desliga `ZoneSpawnSystem`/`DestroyAbandonedSystem`
  no cliente para não crescer 2 cidades diferentes.
- **Aplicação (receptor)**: `RemotePlacementApplySystem` (CS2M/Sync/RemotePlacementApplySystem.cs)
  usa instanciação DIRETA do arquétipo do prefab (`ObjectData.m_Archetype`, linhas 107-136 — "Option B"
  desde v10, comentário linhas 18-32) em vez de reconstruir via `CreationDefinition`/`ObjectDefinition`
  + `GenerateObjectsSystem`/`ApplyObjectsSystem` como o jogo faria — seta manualmente
  `Transform/PrefabRef/PseudoRandomSeed/Tree/Elevation/Attached/Owner` (linhas 135-230), e SÓ para
  **sub-redes e sub-áreas** (transformador de energia, campo de fazenda) reconstrói via
  `CreationDefinition{Permanent}` (linhas 277-347, 531-650), replicando
  `Game.Simulation.BuildingConstructionSystem.CreateNets/CreateAreas` (comentário linhas 29-33, 272-276).
  Resolve o `Owner` de extensão por `SyncId` ou por prefab+posição mais próxima (`ResolveOwner`,
  linhas 477-525). Attach a edge (paradas/placas de beira-de-rua) resolve por **edge mais próximo do
  ponto-na-curva enviado** (`FindNearestEdge`, linhas 420-473) — não por identidade de edge.
  Cobra o custo de construção no HOST (`ChargeConstructionCost`, linhas 657-685) — mas **não** seta
  `Recent` no objeto criado (ver §6).
- **Move/Delete**: `MoveDetectorSystem` (CS2M/Sync/MoveDetectorSystem.cs) detecta mudança de
  `Transform` em entidades com `CS2M_SyncId` (linhas 49-69,143-188) E entidades nativas/upgrades
  sem id, via um cache "pré-move" (`_preMove`, linhas 41-42, 265-352) — usa esse cache como guarda de
  eco (um move remoto nunca passa pelo Temp do tool, então nunca entra no cache). `DeleteDetectorSystem`
  (CS2M/Sync/DeleteDetectorSystem.cs:22-108+) espelha o padrão para `Deleted`. `RemoteEditApplySystem`
  (CS2M/Sync/RemoteEditApplySystem.cs) aplica: delete = `AddComponent<Deleted>` + cascata
  (`CascadeDeleteUtil.DeleteWithChildren`, linha 105); move = `SetComponentData(Transform)` +
  `Updated`+`BatchesUpdated` (linhas 238-251). Resolve o alvo por `CS2M_SyncId`
  (`CS2M_SyncIdSystem.TryResolve`, CS2M/Sync/CS2M_SyncIdSystem.cs:72-84) e, na ausência de id
  (objetos nativos do save), por **prefab + posição mais próxima** dentro de um raio (`FindNative`,
  RemoteEditApplySystem.cs:154-195 — 2m "exato" mesmo prefab, 1m "solto" fallback).
- **Identidade cross-PC**: `CS2M_SyncIdSystem` (CS2M/Sync/CS2M_SyncIdSystem.cs) — nonce de 24 bits
  por processo + contador de 40 bits (linhas 23-34), nunca colide entre jogadores; `Map` é cache,
  a fonte de verdade é o componente `CS2M_SyncId` na própria entidade (comentário linhas 9-13).
- **Contrato**: `SyncContract.Manifest` classifica `ObjectPlaceCommand`/`MoveCommand` como
  `WorldContract` (CS2M/Sync/SyncContract.cs:61-62) e amarra a ferramenta `ObjectToolSystem` a eles
  (linha 114) — garantindo que `Verify()` reclamaria se um novo comando de objeto ficasse sem classe.
- **Fases registradas** (CS2M/Mod.cs:123-134, 275-280): detector antes de `ModificationEnd`; aplicação
  antes de `Modification1` (para que `Created/Updated` sobrevivam aos consumidores de sub-objeto/
  sub-rede no MESMO frame — comentário explícito linhas 124-134); `GrowableDetectorSystem` também
  antes de `ModificationEnd`, `GrowableSuppressSystem` em `Rendering`.

## 6. GAPS e recomendação

1. **`Recent` (custo/reembolso) não é replicado no objeto colocado remotamente.**
   `RemotePlacementApplySystem.ApplyOne` (RemotePlacementApplySystem.cs:78-270) nunca adiciona
   `Recent` ao objeto criado — só `ChargeConstructionCost` debita o dinheiro do HOST (linhas 265-269,
   657-685), que é um valor agregado (`PlayerMoney`), não o campo por-objeto que
   `GetRefundAmount` (Objects/ObjectUtils.cs:356-371) precisa para calcular o reembolso de uma
   demolição/relocate/upgrade subsequente. Efeito prático: se o jogador REMOTO demolir o prédio que
   acabou de chegar, o reembolso ali calculado localmente será 0 (sem `Recent` → decai pro último
   `return 0`, linha 370) em vez do valor "recém-construído, reembolso alto" que a máquina de origem
   veria. Isso é uma divergência de dinheiro **por-máquina**, mascarada só até o próximo tick do
   `MoneySyncApplySystem` (~1 Hz, CS2M/Mod.cs:137-138) resincronizar o saldo — mas nesse intervalo há
   uma janela de arbitragem de reembolso.
   **Recomendação**: `ObjectPlaceCommand` já carrega `Elevation`/`Tree*` como "extras de determinismo";
   adicionar `RecentFrame`(uint)/`RecentCost`(int) opcionais (0 quando não aplicável) e, no receptor,
   `AddComponentData(obj, new Recent{...})` quando `RecentCost != 0`, espelhando exatamente o que
   `ApplyObjectsSystem.Create` faz localmente (Tools/ApplyObjectsSystem.cs:676-682).
2. **Attach por proximidade, não por identidade de edge.** `FindNearestEdge`
   (RemotePlacementApplySystem.cs:420-473) resolve o edge de anexo (parada de ônibus, placa) pelo
   ponto-na-curva mais próximo — mesma classe de risco que o bug de junção de rua documentado (duas
   ruas quase coincidentes numa interseção densa podem levar a "colar" no edge errado). Não há
   `SyncId` de rede no protocolo de objeto hoje.
   **Recomendação**: se a rede já tiver um id estável no ponto de anexo (o mod usa `CS2M_NodeSyncId`
   em outro sistema, conforme a memória do projeto), reaproveitá-lo aqui em vez de raio/distância.
3. **Resolução de objetos NATIVOS (sem `CS2M_SyncId`) por proximidade.** `FindNative`
   (RemoteEditApplySystem.cs:154-195) usa "mesmo prefab a <2m, senão qualquer coisa a <1m" para
   mover/apagar prédios que já existiam no save antes da sessão coop. É uma limitação estrutural do
   jogo base (prédios carregados do save não têm nenhum id estável) — mas em áreas densas com vários
   objetos do MESMO prefab próximos (ex. fileira de árvores idênticas plantada por gerador de mapa),
   o candidato errado pode ser escolhido.
   **Recomendação**: no primeiro toque (delete/move) num nativo, ambos os lados já convergem para um
   `CS2M_SyncId` compartilhado (o código já faz isso — "first-touch identity", MoveDetectorSystem.cs:329-331,
   RemoteEditApplySystem.cs:253-257); o risco fica concentrado só no PRIMEIRO toque. Vale medir em
   selftest quantos falsos-positivos aparecem com objetos idênticos empilhados.
4. **`Building.m_RoadEdge` depende de convergência prévia da malha viária.** Não sincronizado por
   design (é re-derivado localmente por `RoadConnectionSystem`, Buildings/RoadConnectionSystem.cs:2182-2197)
   — correto em teoria, mas herda qualquer drift de rua ainda não resolvido (ver
   `bug-juncao-sync`/`architecture-decision` na memória do projeto: zonas e nós de rua já mostraram
   discrepância `hash-diff` pós-join). Não é um gap do sync de OBJETO em si — é uma dependência que
   vale documentar para não ser re-descoberta como "bug de prédio" quando na verdade é bug de rua.
5. **`TriggerType.ObjectCreated` não dispara no receptor.** O gatilho que
   `ApplyObjectsSystem.HandleTempEntitiesJob` enfileira para o pipeline vanilla local
   (Tools/ApplyObjectsSystem.cs:433-438) não é replicado pelo caminho de arquétipo direto do CS2M —
   sistemas que reagem a esse trigger (notificações, marcos, conquistas de "primeira construção") só
   disparam na máquina de origem. Impacto provável BAIXO (não é estado autorado, é notificação), mas
   vale registrar como assimetria conhecida.
6. **Checklist do que falta para "colocação de objeto" ser 100% correta byte-a-byte:**
   - [x] Identidade de prefab machine-independent (nome+tipo+hash) — feito.
   - [x] Transform completo (posição+rotação) — feito.
   - [x] `PseudoRandomSeed` (cor/malha) — feito.
   - [x] Idade/estado de árvore explícito (não recalculado por seed) — feito (v55).
   - [x] Elevação + flags — feito.
   - [x] Extensão de prédio (owner) — feito, com filtro anti-duplicata de sub-objeto derivado (v51).
   - [ ] `Recent` (custo para reembolso) — **faltando** (item 1).
   - [ ] Identidade de edge de anexo (em vez de nearest-point) — **faltando** (item 2).
   - [ ] `TriggerType.ObjectCreated` equivalente no receptor — **faltando**, impacto baixo (item 5).

## 7. NÃO VERIFICADO

- Os atributos `[UpdateInGroup]/[UpdateBefore]/[UpdateAfter]` reais de `ApplyObjectsSystem` e
  `GenerateObjectsSystem` no jogo vanilla — não sobreviveram nesta cópia do decomp; a ordem de fases
  citada em §2 vem dos comentários do próprio mod (observação em jogo via `Mod.cs`), não de uma
  citação direta de atributo do jogo.
- Se existe algum outro componente de "crescimento"/estado visual (além de `Tree`/`Plant`) usado por
  props não-árvore (ex. arbustos/hedges) que dependa de `ObjectDefinition.m_Age` e que o CS2M ainda
  não cubra — não explorei todos os prefabs de vegetação além dos dois componentes efetivamente
  encontrados no decomp (`Objects/Tree.cs`, `Objects/Plant.cs`); não encontrei um terceiro componente
  de crescimento genérico para outras vegetações.
- O efeito exato (fora do reembolso) de `Recent` ausente — não rastreei todo consumidor de `Recent`
  no jogo, só os 3 usos em `Objects/ObjectUtils.cs:322,336,353` (`GetRelocationCost/GetUpgradeCost/
  GetRebuildCost`) e o `GetRefundAmount` citado; pode haver mais leitores (ex. UI de valor de
  propriedade) não conferidos.
- Se `PlacementDetectorSystem` cobre corretamente TODOS os N objetos gerados numa única passada de
  Brush/Curve no mesmo frame sob carga (múltiplas dezenas de `Applied` no mesmo `_appliedQuery.ToEntityArray`
  chamada) sem drop por causa do guard `_recentlySent`/`_clearCounter` (PlacementDetectorSystem.cs:39-41,
  110-115) — a lógica lida com isso em teoria, mas não validei em jogo com um brush grande.
