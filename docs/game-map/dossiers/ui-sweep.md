# Varredura dos 249 TriggerBindings de UI — dossiê de sync

> Esta não é "uma mecânica", é uma varredura de TODOS os `AddBinding(new TriggerBinding...)`
> sob `decomp/Game/Game/UI/` (249 ocorrências, 61 arquivos — confirmado por
> `grep -rc 'AddBinding(new TriggerBinding' decomp/Game/Game/UI`). A tabela completa,
> linha a linha, está em `docs/game-map/ui-triggers.md`. Este dossiê sintetiza o que a
> varredura revela: o que muta mundo, os perigos cross-machine do padrão, o que o CS2M já
> cobre e os gaps concretos.

## 1. Entradas do jogador

Todo `TriggerBinding` é criado dentro de uma subclasse de `UISystemBase`
(`Game/UI/UISystemBase.cs:11`, que estende `GameSystemBase`) ou de `CompositeBinding`
standalone (ex. `AppBindings`, `AudioBindings`, `ParadoxBindings` — não são `GameSystemBase`,
são objetos plain registrados manualmente pelo `UIManager`). O registro acontece via
`AddBinding(IBinding)` (`Game/UI/UISystemBase.cs:48-52`), que só guarda o binding numa lista
local e o repassa para `GameManager.instance.userInterface.bindings.AddBinding(binding)` — um
registro global mantido pelo assembly `Colossal.UI.Binding` (fora do escopo decompilado em
`decomp/Game/Colossal`; a classe `TriggerBinding` em si **não foi encontrada** no decomp
disponível — ver seção 7).

O disparo em si vem do frontend TS/React (`CS2M.UI`, e o próprio jogo) chamando
`trigger(group, name, ...args)`, que a ponte UI resolve para o `TriggerBinding` cujo
`(group, name)` bate, e executa o delegate C# síncrono. O sistema-dono só recebe o `OnCreate`
que registra o binding se estiver habilitado para o `GameMode` atual — `UISystemBase.gameMode`
(`Game/UI/UISystemBase.cs:19`, default `GameMode.All`) é comparado a `mode` em
`OnGamePreload` (`Game/UI/UISystemBase.cs:60-64`: `base.Enabled = (gameMode & mode) != 0`).
É esse mecanismo que confirma, por exemplo, que `EditorBottomBarUISystem`
(`Editor/EditorBottomBarUISystem.cs:27`, `GameMode.Editor`) só existe fora de uma cidade em
coop, enquanto `EditorPanelUISystem` (`Editor/EditorPanelUISystem.cs:27`,
`GameMode.GameOrEditor`) surpreendentemente roda **também** durante o jogo normal (seus 4
triggers são só chrome de painel — cancel/close/resize — então isso não muda a classificação,
mas invalida a suposição ingênua de "tudo em `UI/Editor/` é editor-only").

## 2. Fluxo de aplicação (metodologia + exemplos representativos)

Não há UM fluxo — são 249. A classificação (tabela completa em `ui-triggers.md`) seguiu esta
regra, aplicada lendo o corpo de cada delegate:

- **MUTA-MUNDO**: o delegate escreve, via `EntityCommandBuffer.SetComponent`/`AddComponent`,
  `DynamicBuffer<T>.Add/RemoveAt`, ou um setter de sistema gerenciado, um dado que sobrevive
  save/load (`ISerializable`) ou que é estado de sessão compartilhado (velocidade de sim).
- **SÓ-LEITURA**: o delegate só calcula/retorna dado de preview (ex.: oferta de empréstimo).
- **LOCAL-COSMETIC**: câmera, foto, áudio/rádio, tutorial, navegação de painel/menu, ou
  configuração da FERRAMENTA ativa (que só produz uma mutação real quando uma ação de
  colocação SEPARADA, fora deste binding, é aplicada pelo próprio `*ToolSystem`).

Três exemplos completos, passo a passo:

**a) `taxation.setTaxRate` (`InGame/TaxationUISystem.cs:189-197`)** — MUTA-MUNDO:
```
UI chama trigger("Taxation", "setTaxRate", rate)
  -> delegate em TaxationUISystem.cs:189
  -> m_TaxSystem.Readers.Complete()      (barreira de job)
  -> m_TaxSystem.TaxRate = rate          (TaxationUISystem.cs:192, campo do TaxSystem)
  -> m_TaxRate.Update() / m_AreaTaxRates.UpdateAll() / ...  (refresh de bindings de leitura)
```
`TaxSystem.TaxRate` é lido/persistido pelo sistema de impostos da cidade (fora do escopo
desta varredura, mas é autored state clássico — cidade sem imposto sincronizado diverge em
receita para sempre).

**b) `chirper.addLike` (`InGame/ChirperUISystem.cs:132-139`)** — MUTA-MUNDO:
```
UI chama trigger("chirper", "addLike", entity)
  -> componentData2 = EntityManager.GetComponentData<Game.Triggers.Chirp>(entity)
  -> componentData2.m_Flags |= ChirpFlags.Liked         (ChirperUISystem.cs:135)
  -> EndFrameBarrier.CreateCommandBuffer().SetComponent(entity, componentData2)
  -> .AddComponent(entity, default(Updated))            (ChirperUISystem.cs:136-138)
```
`Game.Triggers.Chirp` é `ISerializable` com versionamento de save
(`Triggers/Chirp.cs:6,52,58,64`) — está no arquivo de save, não é só UI.

**c) `transportationOverview.hideLine` (`InGame/TransportationOverviewUISystem.cs:199-211`)**
— MUTA-MUNDO:
```
UI chama trigger("transportationOverview", "hideLine", entity, showOthers)
  -> EntityCommandBuffer entityCommandBuffer3 = m_EndFrameBarrier.CreateCommandBuffer()
  -> se showOthers: entityCommandBuffer3.RemoveComponent<HiddenRoute>(m_LineQuery, ...) (linha 206)
  -> entityCommandBuffer3.AddComponent<HiddenRoute>(entity)                            (linha 208)
  -> RequestUpdate()
```
Este é exatamente o par (`showLine`/`hideLine`) que a própria CS2M já nomeia
`RouteVisibilityCommand` no manifesto (seção 5).

Contraexemplo **LOCAL-COSMETIC** para deixar clara a fronteira: `toolbar.selectAsset`
(`InGame/ToolbarUISystem.cs:474-506`) só troca `m_SelectedAssetBinding`/tema/pack/categoria —
nenhum `EntityCommandBuffer`, nenhum `SetComponent`. É a escolha de QUAL prefab o jogador vai
construir a seguir; a mutação real só acontece quando o `*ToolSystem` aplica a colocação (fora
do escopo dos 249 triggers, coberto por `ObjectPlaceCommand`/`NetPlaceCommand` no CS2M).

## 3. Estado persistido tocado

Componentes/campos `ISerializable` (ou equivalentes de sessão compartilhada) identificados
através dos 43 bindings MUTA-MUNDO:

| Componente/campo | Onde é definido | Tocado por |
|---|---|---|
| `Game.Routes.Color` | (via `ColorSection.cs:37,42`, `TransportationOverviewUISystem.cs:157,162`) | #120, #226 |
| `Game.Triggers.Chirp.m_Flags` | `Triggers/Chirp.cs:6` (`ISerializable`, confirmado) | #48, #49 |
| `Game.Areas.ServiceDistrict` (buffer) | `Areas/ServiceDistrict.cs:8` (`IBufferElementData, ISerializable`) | #121 |
| `Game.Routes.VehicleModel` (buffer) | `Routes/VehicleModel.cs:7` (`IBufferElementData, ISerializable`) | #178, #179 |
| `HiddenRoute` (tag component) | usado em `TransportationOverviewUISystem.cs:193-238` | #229, #230, #232 |
| Política (`Policy`/`PolicyModifier` via `PoliciesUISystem`) | fora do escopo desta varredura | #20,21,146,158,159,170,171(¹),190(¹),191,228,231,243,247 |
| `TaxSystem.TaxRate` e afins | fora do escopo | #187,188,189 |
| `ServiceFee` buffer (via `ServiceFeeSystem.SetFee`) | fora do escopo | #172 |
| `LoanSystem` (via `ChangeLoan`) | fora do escopo | #148 |
| `CityConfigurationSystem.cityName` | campo gerenciado, não-ECS | #192 |
| `DevTreeSystem` (unlock de nó) | fora do escopo | #125 |
| `MapTileSystem` (seleção comprada) | fora do escopo | #151 |
| `NameSystem` custom name | fora do escopo | #190, #227 |
| `SimulationSystem.selectedSpeed` (campo, não componente) | fora do escopo | #185, #186 |
| entidade + `Deleted` tag | genérico | #19, #224, #244 |

(¹) #171/#173 usam orçamento (`CityServiceBudgetSystem`), não política, mas mesma família de
"config de serviço autorada".

## 4. Perigos cross-machine

- **Entity cru dentro de buffer persistido.** `ServiceDistrict.m_District` é um `Entity`
  (`Areas/ServiceDistrict.cs:10`) e `VehicleModel.m_PrimaryPrefab`/`m_SecondaryPrefab` também
  (`Routes/VehicleModel.cs:9-11`). `Entity{Index,Version}` é layout de memória por-processo —
  exatamente o padrão que a arquitetura do CS2M já proíbe ("identidade explícita, nunca
  proximidade/índice cru"). Qualquer comando novo para #121/#178/#179 (seção 6) TEM que
  resolver por SyncId/posição/hash de prefab, nunca serializar o `Entity` puro.
- **A maioria dos 249 bindings usa `Entity` como parâmetro do próprio `TriggerBinding`**
  (ex. `TriggerBinding<Entity>` em `TransportationOverviewUISystem.cs:145`,
  `SelectedInfoUISystem.cs:196`, `CameraUISystem.cs:21`, dezenas de outros). Para os
  LOCAL-COSMETIC isso é seguro (nunca cruza a rede). É um risco latente só se algum código
  futuro decidir reenviar esse mesmo argumento a outra máquina sem tradução — vale como
  alerta de padrão, não como bug hoje.
- **`Chirp` tem versionamento de deserialização** (`Triggers/Chirp.cs:52` `Version.chirpLikes`,
  `:58` `Version.randomChirpLikes`, `:64` `Version.continuousChirpLikes`) — confirma que
  `m_Flags`/`m_Likes` fazem parte do formato binário do save. Curtidas divergentes por máquina
  não são só cosméticas: é campo de save real podendo aparecer num diff de estado.
- **Campo gerenciado, não componente ECS, mas precisa convergir.**
  `SimulationSystem.selectedSpeed` (`InGame/TimeUISystem.cs:121,132`) e
  `CityConfigurationSystem.cityName` (`InGame/ToolbarBottomUISystem.cs:95`) não são
  `SetComponent` num `EntityCommandBuffer` — são atribuição direta de campo num sistema
  gerenciado. Isso os torna invisíveis a qualquer estratégia de sync que só observe
  `Created/Updated/Deleted` em componentes ECS; cada um precisa do próprio mecanismo de
  detecção (é exatamente o que o CS2M já faz para cityName via polling — seção 5).
- **`PolicyCommand` resolve por `TargetKind` (0-3) e por posição/`SyncId`**
  (`CS2M/Commands/Data/Game/PolicyCommand.cs:8-27`), não por `Entity` — é o padrão correto,
  citado aqui como contraste positivo aos dois hazards de `Entity` cru acima.

## 5. O que o CS2M faz hoje

Fonte: `CS2M/Sync/SyncContract.cs` (o "nothing slips through the sync" manifest) +
inspeção dos arquivos `CS2M/Commands/Data/Game/*.cs` e um handler.

- **Política (setPolicy/setCityPolicy/toggle/toggleEmptying/setSchedule/setTicketPrice/
  setVehicleCount/setActive/setColor-não-esse-é-outro)**: cobertos por `PolicyCommand`
  (`SyncContract.cs:73`), cujo próprio doc-comment lista explicitamente
  "day/night schedule, out-of-service, vehicle count, ticket price are all route policies"
  e também "empty landfill" (`CS2M/Commands/Data/Game/PolicyCommand.cs:9,11-12`) — batendo
  direto com os bindings #20, #21, #146, #158, #159, #170, #191, #228, #231, #243.
- **Renome (renameEntity/rename)**: `RenameCommand` (`SyncContract.cs:78`), `TargetKind`
  1=building/2=district/3=route (`CS2M/Commands/Data/Game/RenameCommand.cs:12`) cobre #190/#227.
- **Nome da cidade (`toolbarBottom.setCityName`, #192)**: **coberto**, mas por um mecanismo
  diferente — não é a UI trigando o comando direto, é um sistema de detecção por polling
  (`CS2M/Sync/RenameSyncSystems.cs:135-145`) que compara `CityConfigurationSystem.cityName`
  quadro a quadro e manda `RenameCommand{TargetKind = 4}` quando muda
  (`RenameSyncSystems.cs:144`, recebido em `RenameSyncSystems.cs:276`). Confirmado exercitado
  pelo próprio selftest do mod (`CS2M/Sync/AutopilotSystem.cs:2878-2904`). **O comentário em
  `RenameCommand.cs` está desatualizado** (só documenta 1/2/3) — vale corrigir o doc-comment,
  mas a cobertura funcional existe.
- **Cor de linha (`setColor` #120/#226)**: `RouteColorCommand` (`SyncContract.cs:70`).
- **Visibilidade de linha (`showLine`/`hideLine`/`resetVisibility` #229/#230/#232)**:
  `RouteVisibilityCommand` (`SyncContract.cs:71`) — nome bate exatamente com o par
  Hidden/Show da seção 2c.
- **Deletar (`delete` #19/#224/#244)**: `DeleteCommand` (`SyncContract.cs:64`).
- **Compra de tile (`purchaseMapTiles` #151)**: `TilePurchaseCommand` (`SyncContract.cs:59`).
- **Árvore de progressão (`purchaseNode` #125)**: `DevTreeCommand` (`SyncContract.cs:80`).
- **Empréstimo (`acceptLoanOffer` #148)**: `LoanCommand` (`SyncContract.cs:77`).
- **Orçamento/tarifa de serviço (#171/#172/#173)**: `BudgetCommand`/`FeeCommand`
  (`SyncContract.cs:75-76`).
- **Impostos (#187/#188/#189)**: `TaxSyncCommand` (`SyncContract.cs:74`).
- **Velocidade/pausa de simulação (#185/#186)**: `SpeedCommand`, classificado
  `HostAuthoritative` (`SyncContract.cs:86`) — por design só o host propaga; **não verifiquei**
  se o handler do lado do CLIENTE bloqueia a chamada local do trigger antes de gerar drift
  (seção 7).
- **Distrito (área pintada, `DistrictCommand`, `SyncContract.cs:58`)**: cobre pintar/redesenhar
  a ÁREA do distrito (`CS2M/Commands/Data/Game/DistrictCommand.cs:6-9`), confirmado pelo
  handler que só enfileira em `RemoteDistrictQueue`
  (`CS2M/Commands/Handler/Game/DistrictSyncHandler.cs:14-18`) — **não** cobre o vínculo
  prédio→distrito de serviço (#121, seção 6).

## 6. GAPS e recomendação

Checklist concreto do que falta, confirmado por grep no código do mod inteiro
(`grep -r "Chirp|VehicleModel|ServiceDistrict" CS2M/` → **zero ocorrências** fora desta
varredura):

1. **`chirper.addLike`/`removeLike` (#48/#49, `InGame/ChirperUISystem.cs:132,140`)** — sem
   `ChirpCommand`/handler. `Game.Triggers.Chirp` é `ISerializable` (seção 3/4). Impacto baixo
   (curtida de chirp não afeta simulação), mas é campo de save real: ou (a) adicionar um
   comando mínimo resolvendo o chirp por `(m_Sender, m_CreationFrame)` como identidade estável,
   ou (b) excluir explicitamente `Chirp.m_Flags`/`m_Likes` de qualquer state-hash/diff para não
   gerar falso-positivo de divergência.
2. **`districtsSection.removeDistrict` (#121, `InGame/DistrictsSection.cs:79-98`)** — remove um
   item de `DynamicBuffer<ServiceDistrict>` do PRÉDIO selecionado; `DistrictCommand` só cobre a
   ÁREA do distrito, não este vínculo. Recomendação: comando novo
   `ServiceDistrictLinkCommand{TargetBuilding (SyncId/posição, como PolicyCommand),
   TargetDistrict (centroide, como DistrictCommand já resolve), Add|Remove}`.
3. **`selectVehicles`/`deselectVehicles` (#178/#179, `InGame/SelectVehiclesSection.cs:218-296`)**
   — escreve `DynamicBuffer<VehicleModel>` do depósito (prefab de veículo primário/secundário
   por slot); nenhum comando toca `VehicleModel`. Recomendação: comando
   `DepotVehicleModelCommand{TargetDepot (SyncId/posição), SlotIndex, PrimaryPrefabHash,
   SecondaryPrefabHash}` — **resolvendo os prefabs por hash/nome, nunca pelo `Entity` cru**
   guardado hoje em `VehicleModel.m_PrimaryPrefab`/`m_SecondaryPrefab` (hazard da seção 4).
4. **Não é gap, mas merece nota**: `toolbarBottom.setCityName` (#192) já está coberto (seção 5)
   — evita redescobrir isso como gap numa auditoria futura.
5. **A verificar** (não é gap confirmado, é lacuna de verificação): se o `SpeedCommand`
   realmente impede um CLIENTE de aplicar `setSimulationPaused`/`setSimulationSpeed`
   (#185/#186) localmente antes do host mandar a autoridade — o manifesto diz
   `HostAuthoritative` mas eu não abri o handler para confirmar a supressão do lado cliente.

## 7. NÃO VERIFICADO

- Internals de `Colossal.UI.Binding.TriggerBinding`/`CompositeBinding` e da ponte JS
  `trigger()` — o assembly não está presente em `decomp/Game/Colossal` nem em
  `decomp/Game/Game`; só confirmei o registro via `IBinding`/`UISystemBase.AddBinding`
  (`Game/UI/UISystemBase.cs:48-52`), não a fase exata (Update/Modification) em que o delegate
  roda quando disparado pela UI.
- Se `TutorialSystem.mode`/`tutorialEnabled` (usado por `GameTutorialsUISystem.cs:28-40` e
  `InGame/TutorialsUISystem.cs:191-225`) é `ISerializable`/persistido no save — não abri
  `Game/Tutorials/TutorialSystem.cs`. Classifiquei os 7+3 triggers de tutorial como
  LOCAL-COSMETIC seguindo a pista explícita do prompt, não uma confirmação de código.
- Se o componente `Highlighted` (usado em `TransportationOverviewUISystem.cs:241-252`,
  `ActionsSection`/outros) é `ISerializable` — não abri sua definição. Se for, `toggleHighlight`
  (#233) talvez devesse subir de LOCAL-COSMETIC para MUTA-MUNDO.
- Se a lista de séries selecionadas em `StatisticsUISystem` (`m_SelectedStatistics`,
  #180-#184) é persistida por save (alguns painéis de UI guardam preferências no arquivo) —
  não abri o backing field/serialização desse sistema.
- `EditorScreenUISystem` (#101) e `EditorTutorialsUISystem` (#118/#119) **não têm** override de
  `gameMode` (grep não achou `gameMode =>` em nenhum dos dois arquivos), então por
  `UISystemBase.cs:19` eles rodam com o default `GameMode.All` — diferente de
  `EditorBottomBarUISystem.cs:27`/`EditorToolUISystem.cs:33`/`EditorHierarchyUISystem.cs:1088`
  (`GameMode.Editor` explícito) e de `EditorPanelUISystem.cs:27` (`GameMode.GameOrEditor`
  explícito). Classifiquei os triggers desses dois arquivos como LOCAL-COSMETIC pelo
  propósito semântico (navegação de tela/tutorial do editor), não por confirmação de
  game-mode gate.
- Se o handler de `SpeedCommand` bloqueia a chamada local de
  `setSimulationPaused`/`setSimulationSpeed` num cliente não-host antes de gerar drift — não
  abri `CS2M/Commands/Handler/Game/*Speed*`.
- Se o handler de `DevTreeCommand` de fato aciona `DevTreeSystem.Purchase` ponta a ponta no
  receptor — só confirmei a entrada no `Manifest` (`SyncContract.cs:80`), não abri o handler.
