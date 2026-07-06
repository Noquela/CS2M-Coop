# Fontes de água — dossiê de sync

Fonte primária lida: `decomp/Game/Game/Tools/WaterToolSystem.cs`, `Tools/WaterSourceDefinition.cs`,
`Tools/GenerateWaterSourcesSystem.cs`, `Tools/ApplyWaterSourcesSystem.cs`, `Simulation/WaterSourceData.cs`,
`Simulation/WaterSystem.cs`, `Simulation/WaterSourceInitializeSystem.cs`, `Simulation/WaterSimulation.cs`,
`Common/SystemOrder.cs`. Mod lido: `CS2M/Commands/Data/Game/WaterCommand.cs`,
`CS2M/Commands/Handler/Game/WaterSyncHandler.cs`, `CS2M/Sync/WaterDetectorSystem.cs`,
`CS2M/Sync/WaterApplySystem.cs`, `CS2M/Sync/RemoteWaterQueue.cs`, `CS2M/Sync/CS2M_RemotePlaced.cs`,
`CS2M/Sync/StateHashSystems.cs`, `CS2M/Sync/SyncContract.cs`.

## 1. Entradas do jogador

`WaterToolSystem` (`Tools/WaterToolSystem.cs:24`) tem `toolID = "Water Tool"` (linha 219 — `kToolID`) e dois
modos, `EditSource` e `AddSource` (linhas 43-47, enum `Mode`). As ações de input são registradas em `OnCreate`
(linhas 300-308): `Add Water Source`, `Delete Water Source`, `Move Water Source`, `Change Water Source
Height/Rate/Radius`, `Discard Water Source Height/Rate/Radius`. `UpdateActions()` (linhas 321-411) liga a ação
`applyAction`/`secondaryApplyAction` correta conforme `mode`, `m_State` (`Default/MouseDown/Dragging/Removing`,
linhas 35-41) e `attribute` (`None/Location/Radius/Rate/Height`, linhas 26-33).

O raycast usa `TypeMask.WaterSources` fora de drag e `TypeMask.Terrain` durante drag de posição/`AddSource`
(`InitializeRaycastImpl`, linhas 450-469; `InitializeRaycast`, linhas 471-487). Qual atributo (Location vs
Radius vs Height/Rate) o cursor está editando é decidido por `GetAttribute` comparando o offset do cursor em
relação aos eixos da câmera (linhas 692-721) — puramente derivado da posição do mouse em tela, não enviado
como um campo de "modo" explícito.

`OnUpdate` (linhas 490-527) despacha por frame para `Apply`, `Cancel` ou `Update` conforme os inputs foram
pressionados/soltos naquele frame. `Apply()` (linhas 593-631) avança a máquina de estados
`Default→MouseDown→Dragging→Default` (aplicando ao soltar o drag) ou, em `AddSource`, aplica direto no clique.
`Cancel()` (linhas 542-591) trata o clique secundário: em `State.Default` sobre uma fonte existente vira
`State.Removing` (preview de deleção) e dispara `onWaterSourceDeleted` (linha 580) quando confirmado.

## 2. Fluxo de aplicação

Cada frame com raycast válido, `WaterToolSystem` chama `UpdateDefinitions` (linhas 823-845):
1. `DestroyDefinitions(m_DefinitionQuery, ...)` — `m_DefinitionQuery` = `GetDefinitionQuery()`
   (`Tools/ToolBaseSystem.cs:697-699`) = `CreationDefinition` **excluindo** `Updated`. Isso apaga a entidade de
   definição do frame ANTERIOR (que já perdeu a tag `Updated` pelo cleanup de 1 frame).
2. Agenda `CreateDefinitionsJob` (linhas 50-201) que cria uma NOVA entidade de definição via
   `m_ToolOutputBarrier.CreateCommandBuffer()`: `CreationDefinition{ m_Original, m_Flags }` +
   `WaterSourceDefinition{ m_Position, m_ConstantDepth, m_Radius, m_Multiplier, m_Polluted, m_Height,
   m_SourceId }` + tag `Updated` (linhas 191-199). Em `Mode.AddSource`, `m_Original = Entity.Null`,
   `m_Flags = 0`, e os valores default são fixos no código: raio 30, altura 10, `m_Id = -1` (linhas 150-160,
   "-1" = "precisa de ID novo"). Em `Mode.EditSource`, `entity` é a fonte real sob o cursor
   (`m_RaycastPoint.m_OriginalEntity`) e os campos vêm de `GetWaterDefinition`/`GetWaterDefinitionLegacy`
   (linhas 84-142) — só mudam radius/height durante `State.Dragging`, escalados pelo atributo ativo.

`WaterToolSystem` roda na fase `ToolUpdate` (`Common/SystemOrder.cs:708`).

`GenerateWaterSourcesSystem` (`Tools/GenerateWaterSourcesSystem.cs:16`) roda na fase `Modification1`
(`SystemOrder.cs:103`) e exige `CreationDefinition + Updated` com `WaterSourceDefinition` (query, linhas
121-129). Para cada definição: cria uma entidade real no arquétipo `WaterSourceData + Transform + Temp +
Created + Updated` (linha 130), grava `WaterSourceData{ m_ConstantDepth, m_Radius, m_Polluted, m_Height,
m_Id = m_SourceId, m_Modifier = 1f }` (linhas 41-49 — **`m_Modifier` é sempre forçado a `1f` aqui**, nunca
copiado da definição, porque `WaterSourceDefinition` nem tem esse campo) e `Transform{ m_Position, identity
}` (linhas 50-54). Se a flag `Permanent` não estiver setada (nunca está, vindo do tool), monta um `Temp{
m_Original, m_Flags }` — `Select`(+`Dragging`) se veio com `CreationFlags.Select`, `Delete` se veio com
`CreationFlags.Delete`, senão `TempFlags.Create` se `m_Original == Entity.Null` (linhas 62-77) — e esconde a
entidade original com `Hidden` quando ela existe (linhas 79-82).

`ApplyWaterSourcesSystem` (`Tools/ApplyWaterSourcesSystem.cs:17`) roda na fase `ApplyTool`
(`SystemOrder.cs:719`), com query `Temp + WaterSourceData`, excluindo `PrefabRef` (linha 205) e
`RequireForUpdate` nessa mesma query (linha 206) — ou seja, roda **todo frame** em que existir qualquer
entidade `Temp`+`WaterSourceData`, independente do `ApplyMode` do tool (`Tools/ApplyMode.cs`: `None/Apply/
Clear`). Antes do job, para toda entidade com `m_Id == -1` chama `m_WaterSystem.GetNextSourceId()` e grava o
novo id (linhas 212-221). O job (`HandleTempEntitiesJob`, linhas 20-161) então, por `Temp.m_Flags`:
- `Cancel` → reexibe o original (remove `Hidden`) e deleta o ghost (linhas 47-49, 68-76).
- `Delete` → marca o original E o ghost como `Deleted` (linhas 51-54, 78-85).
- se `temp.m_Original` já tem `WaterSourceData` (edição de fonte existente) → `UpdateComponent` copia
  `WaterSourceData` e `Transform` do ghost PARA o original (linhas 55-60, 87-111) — **isso significa que, ao
  arrastar raio/altura/posição de uma fonte existente, o valor é gravado na entidade real a cada frame do
  drag**, não só ao soltar o mouse — e deleta o ghost (linha 147).
- senão (entidade nova, `AddSource`) → `Create()`: remove `Temp`, adiciona `Updated` + `Created` — a entidade
  passa a ser permanente (linhas 63-64, 150-155).

Tags `Temp/Created/Updated/Deleted` vivem 1 frame; `CleanUpSystem` (`SystemOrder.cs:54`, fase `Cleanup`) as
limpa no fim do frame, conforme a regra geral do projeto.

## 3. Estado persistido tocado

- `Simulation.WaterSourceData` (`Simulation/WaterSourceData.cs:6`, `ISerializable`): serializa
  `m_ConstantDepth, m_Height, m_Radius, m_Multiplier, m_Polluted, m_Id` (linhas 22-30); **NÃO** serializa
  `m_Modifier` — no `Deserialize` ele é sempre resetado para `1f` (linha 55), e formatos antigos sem
  `FormatTags.NewWaterSources` recuperam `m_Id = -1` (linha 44, será renumerado).
- `Objects.Transform` (`Objects/Transform.cs:8`, `ISerializable`) — posição/rotação da fonte.
- `Simulation.WaterSystem` (`Simulation/WaterSystem.cs:34`, implementa `ISerializable`/`IDefaultSerializable`):
  serializa `m_NextSourceId` (linha 984 write; 1093/1099 read) e `m_UseLegacyWaterSources` (linha 987 write;
  1094/1102 read) — ambos fazem parte do save, não são flags locais soltas.

## 4. Perigos cross-machine

1. **`m_Id` é um contador por-máquina, dependente de ORDEM.** `GetNextSourceId()` (`Simulation/
   WaterSystem.cs:719-721`) é só `return m_NextSourceId++`. `ApplyWaterSourcesSystem` chama isso sempre que
   encontra `Temp`+`WaterSourceData` com `m_Id == -1` (linhas 212-221 do arquivo do mod acima). Se duas
   máquinas criam/replay fontes em ordem diferente (concorrência, latência de rede), a MESMA fonte lógica
   recebe `m_Id` diferente em cada lado — um campo `ISerializable` (`WaterSourceData.cs:18,29,53`) que diverge
   silenciosamente sem qualquer erro visível.
2. **Identidade via `Entity` bruto.** `CreationDefinition.m_Original` (`WaterToolSystem.cs:193`) e
   `Temp.m_Original` (`GenerateWaterSourcesSystem.cs:60,79-82`; usado inteiro em
   `ApplyWaterSourcesSystem.HandleTempEntitiesJob`) são `Unity.Entities.Entity` — índice+versão só válidos
   dentro do processo local. O jogo nunca precisa mandar isso pela rede (é raycast→definição→apply no mesmo
   processo), mas confirma que **nenhuma identidade nativa e estável existe pronta para o mod reusar**.
3. **Fontes sem `PrefabRef` nunca são "normalizadas".** `WaterSourceInitializeSystem`
   (`Simulation/WaterSourceInitializeSystem.cs:87`) exige `WaterSourceData + PrefabRef + Created`, excluindo
   `Temp`, para reforçar `m_Height/m_Radius/m_Polluted/m_Modifier=1f/m_Id=-1` a partir do prefab (linhas 39-46)
   — isso é para fontes de mapa (rio/nascente prefabricados), não para as criadas por `AddSource` via tool (que
   também não têm `PrefabRef`, mas passam por `GenerateWaterSourcesSystem` que já seta `m_Modifier=1f` na
   criação). Qualquer entidade criada por FORA desses dois caminhos e sem `PrefabRef` nunca recebe esse
   reforço.
4. **`m_Modifier` é "deve ser sempre 1.0" por convenção, não por tipo.** É um `float` puro
   (`Simulation/WaterSourceData.cs:20`), nunca serializado, e forçado a `1f` em exatamente 3 lugares do
   Game.dll: `GenerateWaterSourcesSystem.cs:48`, `WaterSourceInitializeSystem.cs:44` e
   `WaterSourceData.cs:55` (Deserialize). É consumido em `Simulation/WaterSimulation.cs:58`:
   `waterSourceCache.m_Radius = source.m_Radius * source.m_Modifier` — ou seja, `m_Modifier == 0` (default
   de struct) equivale a **raio efetivo zero** na simulação: a fonte existe mas não produz água. Qualquer
   código que monte `WaterSourceData` na mão sem setar esse campo cria uma fonte visualmente presente e
   funcionalmente morta.
5. **`m_UseLegacyWaterSources` é por-save, mas mutável em runtime por um único jogador.** É persistido
   (`WaterSystem.cs:987/1094/1102`) e `SetDefaults` (linha 1116-1130, chamado em mapa novo) fixa `false`; então
   ambas as máquinas que carregam o MESMO save partem do mesmo valor. Mas o setter de
   `UseLegacyWaterSources` (linha ~572-585) chama `UpgradeToNewWaterSystem()` (linhas 1162-1215) quando o
   valor muda de `true` para `false` — isso altera como `Height`/`Radius`/`Rate` são interpretados
   (`CreateDefinitionsJob` ramifica em `GetWaterDefinitionLegacy` vs `GetWaterDefinition`, `WaterToolSystem.cs:
   84-142`, escolhido por `m_useLegacy` = `m_WaterSystem.UseLegacyWaterSources` na linha 839). Se esse upgrade
   for disparado por ação LOCAL de um único jogador (ex.: um botão de opções) sem ser sincado, os dois lados
   passam a interpretar drag de altura/raio de forma diferente dali em diante. NÃO VERIFICADO o gatilho de UI
   exato — ver seção 7.
6. **Preview e "apply final" não são fases distintas no pipeline do jogo.** `ApplyWaterSourcesSystem` roda
   toda vez que existe `Temp`+`WaterSourceData` (linha 206), não é gated por `ToolBaseSystem.applyMode`. Um
   esquema de sync que só reenviasse o valor final do mouse-up (em vez de replay de input completo) arriscaria
   ficar dessincronizado do estado intermediário que o próprio jogo já commitou frame a frame durante o drag
   — hoje irrelevante porque o mod não faz input-replay para água (ver seção 5), mas relevante se um dia
   migrar para esse modelo (como já foi feito pro Net tool, por nota de projeto).

## 5. O que o CS2M faz hoje

`WaterCommand` está classificado `WorldContract` em `SyncContract.Manifest`
(`CS2M/Sync/SyncContract.cs:67`). O fluxo é **detector por polling + resolução por proximidade**, não
input-replay:

- `WaterDetectorSystem` (`CS2M/Sync/WaterDetectorSystem.cs:23`) faz baseline na primeira passada (linhas
  52-60: tudo que já existe no save vira `_seen` sem ser enviado) e depois, a cada frame, escaneia
  `WaterSourceData+Transform` excluindo `Temp/Deleted/CS2M_RemotePlaced` (linhas 33-37 — o `CS2M_RemotePlaced`
  é o guarda de eco, definido em `CS2M/Sync/CS2M_RemotePlaced.cs:11` como `IComponentData` **não**
  `ISerializable**, ou seja, não sobrevive a save/load). Para cada entidade JÁ conhecida (`_seen`), só
  reage se a posição XZ andou mais que `4f` (distância² > 4 → > 2 m) desde o último envio (linha 73) e manda
  `WaterCommand{ Move=true, OldX/OldZ, PosX/PosY/PosZ, Radius, Height, Multiplier, Polluted, ConstantDepth }`
  (linhas 82-89). Para entidade NOVA, manda `WaterCommand` de criação com os mesmos campos (linhas 97-105).
  Fontes que sumiram do scan viram `WaterCommand{ Delete=true, PosX/PosZ }` (linhas 139-144), a menos que
  `WaterSync.ConsumeRemoteDelete` confirme que o sumiço veio da própria rede (guarda de eco, linhas 134-137).
- `WaterSyncHandler` (`CS2M/Commands/Handler/Game/WaterSyncHandler.cs:14-18`) só enfileira em
  `RemoteWaterQueue` (`CS2M/Sync/RemoteWaterQueue.cs`, fila `ConcurrentQueue` thread-safe).
- `WaterApplySystem` (`CS2M/Sync/WaterApplySystem.cs:22`) drena a fila e despacha por `cmd.Delete`/`cmd.Move`/
  criação (linhas 40-49). Em TODOS os três casos, o Y recebido é descartado em favor da altura do terreno
  LOCAL (`TerrainUtils.SampleHeight`, linhas 58-60, 97-100 — decisão deliberada, documentada no comentário de
  `WaterCommand.cs:8-11`). `ApplyOne` (criação, linhas 52-82) monta a entidade **na mão**:
  `EntityManager.CreateEntity()` + `AddComponentData(WaterSourceData{ m_Radius, m_Height, m_Multiplier,
  m_Polluted, m_ConstantDepth })` + `AddComponentData(Transform{...})` + `CS2M_RemotePlaced` + `Created` +
  `Updated` (linhas 67-79) — sem passar por `CreationDefinition`/`WaterSourceDefinition`/
  `GenerateWaterSourcesSystem`/`ApplyWaterSourcesSystem`. `ApplyMove` e `ApplyDelete` resolvem o alvo por
  `FindNearestSource`, uma busca linear pela entidade `WaterSourceData+Transform` (excluindo `Temp/Deleted`)
  mais próxima em XZ dentro de 10 m (`bestDistSq = 100f`, linhas 119-150, 155-181) — sem qualquer id.
- `StateHashSystems.cs` inclui água no fingerprint de saúde: `AccWater` (linhas 531-549) soma
  `Pt(Transform.m_Position)` (hash FNV-1a arredondado a 0,5 m, linhas 598-609) de toda entidade que casar
  `WaterDesc()` (`All: WaterSourceData`, linha 171-173) — **só posição**, contagem incluída
  (`StateHashSenderSystem`/`StateHashApplySystem`, linhas 666-771, comparação em `Check`, linha 820-828).

## 6. GAPS e recomendação

1. **[CRÍTICO — bug funcional] Fonte remota nasce com `m_Modifier = 0`.** `WaterApplySystem.ApplyOne`
   (`CS2M/Sync/WaterApplySystem.cs:68-75`) nunca seta `m_Modifier` no inicializador de `WaterSourceData`; o
   default de `float` em C# é `0f`. Por `Simulation/WaterSimulation.cs:58` (`m_Radius * m_Modifier`), a fonte
   criada no lado remoto tem **raio efetivo zero** na simulação — não produz água — até que um jogador local
   passe a ferramenta de água sobre ela (o que dispara `ApplyWaterSourcesSystem`'s `Update()`, que copia um
   ghost com `m_Modifier=1f` por cima, linhas 55-59 do arquivo do jogo). Checklist: setar
   `m_Modifier = 1f` explicitamente em `ApplyOne` (e em qualquer outro ponto do mod que monte
   `WaterSourceData` na mão).
2. **[GRAVE — silencioso nos dois sentidos] Editar raio/altura/vazão/poluição de uma fonte já existente, sem
   mover, nunca é sincado.** `WaterDetectorSystem` só reage a deslocamento XZ > 2 m (`WaterDetectorSystem.cs:
   73`); não há nenhuma comparação de `Radius/Height/Multiplier/Polluted/ConstantDepth` contra o valor
   conhecido. E o radar de saúde também não pegaria: `AccWater` (`StateHashSystems.cs:531-549`) só hash a
   posição, então o selo de sincronia mostraria "sincado" mesmo com raios/profundidades diferentes nas duas
   máquinas. Checklist: (a) `WaterDetectorSystem` precisa comparar TODOS os campos autorados contra o último
   valor enviado, não só a posição; (b) `AccWater` precisa dobrar `Radius/Height/Multiplier/Polluted/
   ConstantDepth` no hash para que o drift pelo menos seja detectável enquanto o fix de (a) não sai.
3. **[Identidade por proximidade, já documentado no próprio código do mod] Move/Delete resolvem "a fonte mais
   próxima em 10 m".** `WaterCommand` não carrega nenhum identificador estável (nem `m_Id` do jogo, nem um id
   próprio do mod) — só `PosX/PosZ` (mais `OldX/OldZ` para move). `FindNearestSource`
   (`WaterApplySystem.cs:119-150`) e a busca equivalente em `ApplyDelete` (linhas 155-181) processam por
   distância². Duas fontes a menos de 10 m uma da outra (represa alimentada por duas nascentes próximas, ou
   deletar-e-criar rápido perto do mesmo ponto) resolvem para a entidade ERRADA. Reaproveitar `m_Id` do jogo
   não resolve (perigo #1: é por-máquina); checklist: o mod precisa cunhar um id PRÓPRIO e estável no primeiro
   `DETECT+SEND` de criação (ex.: GUID ou `(hostId, contador)` incremental do mod) e propagar esse id em
   Move/Delete/Edit em vez de posição.
4. **[Drift sub-limiar nunca converge]** O limiar de 2 m em `WaterDetectorSystem.cs:73` só atualiza `_seen`
   quando envia; pequenos arrastões/ajustes de terreno abaixo disso nunca disparam envio, então a posição pode
   divergir aos poucos sem nunca cruzar o limiar de detecção local — embora o hash de `StateHash` (arredondado
   a 0,5 m) tenda a pegar isso mais cedo que os 2 m do detector, então hoje é mais um "atraso de convergência"
   que um buraco silencioso total.
5. **[`m_Id` nunca é setado/avançado no lado remoto]** `ApplyOne` deixa `m_Id` no default `0` (não no sentinel
   `-1` que o jogo usa para "precisa de id novo") e nunca toca `WaterSystem.GetNextSourceId()`
   (`WaterSystem.cs:719-721`) — então fontes remotas tendem a colidir em `m_Id=0` entre si e com a primeira
   fonte local da própria máquina, e os contadores `m_NextSourceId` das duas máquinas divergem ao longo da
   sessão. Não verificado nenhum consumidor visível de `m_Id` além do upgrade legado (seção 7), mas é uma
   divergência de campo `ISerializable` real e checklist válido: se o gap #3 for resolvido com um id próprio
   do mod, aproveitar para também popular `m_Id` de forma consistente (ou aceitar explicitamente que ele é
   "don't care" e documentar isso no comentário do comando).

## 7. NÃO VERIFICADO

- A relação exata intra-frame vs. entre-frames entre as fases `ToolUpdate` (WaterToolSystem) → `Modification1`
  (GenerateWaterSourcesSystem) → `ApplyTool` (ApplyWaterSourcesSystem): confirmei a fase registrada de cada
  sistema em `Common/SystemOrder.cs`, mas não confirmei se o agendador as executa todas no mesmo frame ou se
  há um frame de atraso entre a criação da definição e sua aplicação final.
- Se `m_Id` é lido em algum outro lugar do jogo (UI, notificações, referências salvas) além de
  `WaterSystem.UpgradeToNewWaterSystem` (`Simulation/WaterSystem.cs:1162-1215`, migração legada) — busquei só
  por atribuições/leituras diretas de `.m_Id` no contexto de `WaterSourceData`, não fiz uma varredura semântica
  exaustiva de todo o assembly.
- O gatilho exato de `UpgradeToNewWaterSystem()` — vi que é chamado pelo setter de `UseLegacyWaterSources`
  quando o valor muda para `false` (`WaterSystem.cs` por volta da linha 572-585), mas não abri a UI/settings
  que efetivamente chama esse setter, então não confirmei se é uma ação por-jogador (ex.: botão de opções) que
  poderia ficar dessincada entre host/cliente.
- Se o preview de `AddSource`/edição realmente vira entidade `Created` permanente A CADA FRAME antes do
  jogador soltar o clique (minha leitura de `ApplyWaterSourcesSystem.HandleTempEntitiesJob`, que não é gated
  por `ApplyMode`) — a leitura do código aponta que sim, mas não achei um portão de nível mais alto que
  contradissesse isso, e não rodei o jogo para confirmar visualmente o comportamento.
- Se o dispatch GPU do `WaterSystem` (`RegisterGPUSystem<WaterSystem>()`, `SystemOrder.cs:46`) consome o
  `WaterSourceCache` (`Simulation/WaterSimulation.cs:24-81`) no mesmo frame em que a fonte é criada ou com 1
  frame de atraso — irrelevante para convergência de estado autorado, mas não verificado.
- Não testei em jogo (2 sims reais) nenhum dos gaps listados na seção 6 — a análise é 100% estática sobre o
  decomp e o código do mod.
