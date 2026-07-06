# Zoneamento (pintar/despintar células) — dossiê de sync

## 1. Entradas do jogador

O jogador usa `Game.Tools.ZoneToolSystem` (`decomp/Game/Game/Tools/ZoneToolSystem.cs`). Três modos
(`Tools/ZoneToolSystem.cs:28-33`): `FloodFill`, `Marquee`, `Paint`; três estados internos
(`Tools/ZoneToolSystem.cs:35-40`): `Default`, `Zoning`, `Dezoning`.

- `OnUpdate` (`Tools/ZoneToolSystem.cs:631-705`) decide, por frame, entre `Apply`/`Cancel`/`Update`
  conforme a ação de input (`applyAction`/`secondaryApplyAction`/`cancelAction`) e o estado atual.
- `Apply`/`Cancel` (`Tools/ZoneToolSystem.cs:734-840`) alternam entre `Zone`/`Dezone` e chamam
  `UpdateDefinitions`.
- `UpdateDefinitions` (`Tools/ZoneToolSystem.cs:969-992`) roda **todo frame** enquanto a ferramenta
  está ativa: destrói a definição do frame anterior (`DestroyDefinitions`) e, se há um ponto de
  raycast válido, agenda `CreateDefinitionsJob` que cria uma entidade nova com:
  - `CreationDefinition { m_Prefab = <entidade do ZonePrefab>, m_Original = <bloco sob o cursor> }`
    (`Tools/ZoneToolSystem.cs:222-226`);
  - `Zoning { m_Position: Quad3, m_Flags: ZoningFlags }` — o quad é a área pintada em **coordenadas
    de mundo** (float3), calculado por modo (Paint/FloodFill usam ponto/segmento; Marquee usa um
    retângulo orientado pela direção da câmera ou do bloco inicial) e com a altura amostrada do
    terreno em cada canto (`Tools/ZoneToolSystem.cs:228-327`);
  - `Updated` (`Tools/ZoneToolSystem.cs:227`).
  - Os flags (`Tools/ZoneToolSystem.cs:229-244`) codificam Zone-vs-Dezone, Overwrite e o modo
    (FloodFill/Paint/Marquee).
- `GetAllowApplyZone`/`GetAllowRemoveZone` (`Tools/ZoneToolSystem.cs:509-580`) só habilitam o botão
  comparando `Cell.m_Zone.m_Index` da célula sob o cursor com `ZoneData.m_ZoneType.m_Index` do prefab
  selecionado — comparação **só local** (gating de UI), não é o que viaja pelo fio.

## 2. Fluxo de aplicação

Passo a passo, com fase citada onde encontrada (barreiras `SafeCommandBufferSystem`; a numeração
Modification1/4/5 segue a convenção já usada no projeto — não achei o atributo de fase explícito no
código decompilado, ver §7):

1. **ZoneToolSystem** (fora de qualquer `ModificationBarrierN` — usa `ToolOutputBarrier`,
   `Tools/ZoneToolSystem.cs:985-987`) grava `CreationDefinition+Zoning+Updated` todo frame de drag.

2. **GenerateZonesSystem.OnUpdate** (`Tools/GenerateZonesSystem.cs:564-595`) — query
   `CreationDefinition + Zoning + Updated` (`Tools/GenerateZonesSystem.cs:560-561`); usa
   `ModificationBarrier1` (`Tools/GenerateZonesSystem.cs:559,586,593-594` — fase "Modification1").
   - `FillBlocksListJob` (`Tools/GenerateZonesSystem.cs:35-450`) varre o quadtree de blocos
     (`SearchSystem`) e, conforme o flag (FloodFill/Paint/Marquee), monta
     `NativeParallelMultiHashMap<Entity,CellData>`: **bloco real → lista de (índice de célula, novo
     ZoneType)**. Respeita `CellFlags.Visible` e o flag `Overwrite` (só sobrescreve célula já zonada
     se Overwrite estiver setado) — `Tools/GenerateZonesSystem.cs:279-428`.
   - `CreateBlocksJob` (`Tools/GenerateZonesSystem.cs:453-508`, `IJobParallelForDefer`) — para cada
     bloco real afetado: cria uma entidade **fantasma** com o mesmo arquétipo
     (`ZoneBlockData.m_Archetype`), copia `PrefabRef`+`Block`+buffer `Cell` inteiro do bloco real,
     sobrescreve `m_Zone` só nos índices tocados e liga `CellFlags.Selected` neles
     (`Tools/GenerateZonesSystem.cs:491-506`); marca o fantasma `Temp { m_Original = <bloco real> }`
     (`Tools/GenerateZonesSystem.cs:486-488`) e o bloco real `Hidden`+`BatchesUpdated`
     (`Tools/GenerateZonesSystem.cs:489-490`) — é assim que a prévia ao vivo aparece sem tocar ainda
     no bloco real "de verdade".

3. **ApplyZonesSystem.OnUpdate** (`Tools/ApplyZonesSystem.cs:200-214`) — query `Temp + Block`
   (`Tools/ApplyZonesSystem.cs:195`), sem gate por `Applied`; usa `ToolOutputBarrier`
   (`Tools/ApplyZonesSystem.cs:194,210,212`). Roda **todo frame**, inclusive durante o arrasto (não é
   só no "soltar o mouse"):
   - `Update(...)` (`Tools/ApplyZonesSystem.cs:111-140`) copia o buffer `Cell` ATUAL do bloco real, e
     só para os índices marcados `Selected` no fantasma copia `m_Zone` do fantasma para o real — a
     menos que a célula real já tenha `CellFlags.Overridden` e a zona não bata, caso em que a zona
     real é preservada (`Tools/ApplyZonesSystem.cs:122-134`, ver §4 sobre `Overridden`).
   - Remove `Hidden` do bloco real e marca-o `Updated` (`Tools/ApplyZonesSystem.cs:96-107`); marca o
     fantasma `Deleted` (`Tools/ApplyZonesSystem.cs:108`).
   - **Conclusão importante:** o buffer `Cell` do bloco REAL é mutado ao vivo a cada frame do
     arrasto — não existe um "commit" único e separado de uma "prévia" puramente visual; o próprio
     bloco real já reflete a pintura em progresso.

4. Em paralelo (disparado por edição de rua, não pela ferramenta de zona): **BlockSystem**
   (`Zones/BlockSystem.cs`) deriva blocos a partir de `Edge`s marcadas `Updated`/`Deleted` com buffer
   `SubBlock`, excluindo `Temp` (`Zones/BlockSystem.cs:1114-1128`); usa `ModificationBarrier4`
   (`Zones/BlockSystem.cs:1113,1160-1161` — fase "Modification4", confirma a convenção
   "BlockSystem@Mod4" já usada no projeto). Reaproveita a entidade antiga do bloco (preservando seu
   buffer `Cell`, logo a pintura) quando o novo `Block` (posição/direção/tamanho) é **igual por
   valor** ao antigo (`Zones/BlockSystem.cs:491,968`, via `Block.Equals`,
   `Zones/Block.cs:16-23`); senão cria bloco novo com `Cell` todo default/não-zonado
   (`Zones/BlockSystem.cs:500-511,977-988`). Isso é o "blocos existem ao longo de toda rua,
   independente de pintura" (`GAME_SYSTEMS.md:183-184`).

5. **CellCheckSystem** (`Zones/CellCheckSystem.cs`) — usa `ModificationBarrier5`
   (`Zones/CellCheckSystem.cs:160,179,305,307` — fase "Modification5"), disparado quando
   `ZoneUpdateCollectSystem.isUpdated` (ou Object/Net/Area equivalentes) é true
   (`Zones/CellCheckSystem.cs:186`). Pipeline interno:
   `CellBlockJobs.BlockCellsJob` (geometria/roadside por célula) →
   `FindOverlappingBlocksJob`+`GroupOverlappingBlocksJob` (acha pares/grupos de blocos cujos quads se
   sobrepõem — ex.: lotes de esquina) → `CellOccupyJobs.ZoneAndOccupyCellsJob` (marca `Occupied` a
   partir de objetos realmente colocados; se um prédio força uma zona diferente da pintada, seta
   `CellFlags.Overridden` e sobrescreve `m_Zone`, `Zones/CellOccupyJobs.cs:244-248`) →
   `CellOverlapJobs.CheckBlockOverlapJob` (resolve células **compartilhadas** entre blocos
   sobrepostos, literalmente copiando `cell.m_Zone = cell2.m_Zone` do bloco "vencedor" pra célula
   compartilhada do outro, `Zones/CellOverlapJobs.cs:522-531`, critério de prioridade em
   `Zones/CellOverlapJobs.cs:557-577`) → `CellCheckHelpers.UpdateBlocksJob`/`LotSizeJobs` (recomputa
   `ValidArea`/`VacantLot`). Esse é o "CellCheckSystem overlap sharing" que os comentários do próprio
   mod já mencionam (`CS2M/Sync/ZoneEcho.cs:9-12`).

## 3. Estado persistido tocado

Todos `ISerializable`, confirmados no código:

| Componente | Onde | Campos relevantes |
|---|---|---|
| `Block` (component) | `Zones/Block.cs:8,30-42` | `m_Position` (float3), `m_Direction` (float2), `m_Size` (int2) — também é a **chave de igualdade** usada pro reaproveitamento em BlockSystem |
| `Cell` (buffer no Block) | `Zones/Cell.cs:7,15-43` | `m_State` (`CellFlags`), `m_Zone` (`ZoneType`), `m_Height` — grava `m_Zone` cru via `ZoneType.Serialize` |
| `ZoneType` | `Zones/ZoneType.cs:6,17-31` | `m_Index` (ushort) — grava/lê o índice **cru**, sem nenhum remap (ver §4) |
| `ValidArea` (component) | `Zones/ValidArea.cs:7,11-19` | `m_Area` (int4) — derivado, mas persistido |
| `VacantLot` (buffer) | `Zones/VacantLot.cs:9,37-62` | `m_Area`, `m_Type` (**outro `ZoneType` cru!**), `m_Height`, `m_Flags` |
| `BuildOrder` (component no Block) | `Zones/BuildOrder.cs:6-19` | `m_Order` (uint) — herdado do `Game.Net.BuildOrder` da rua, usado como critério de prioridade no overlap (§2 passo 5) |
| `SubBlock` (buffer na Edge) | `Zones/SubBlock.cs:8` | **NÃO** persiste de verdade — `IEmptySerializable`, é reconstruído no load |

`Cell.m_Zone.m_Index` e `VacantLot.m_Type.m_Index` são os dois lugares onde o índice cru de zona
sobrevive no save.

## 4. Perigos cross-machine

1. **`ZoneType.m_Index` é atribuído por-máquina, na ordem de criação dos prefabs, e NUNCA é
   persistido nem remapeado.** `Prefabs/ZoneSystem.cs:131-203` (`InitializeZonePrefabs`) roda
   sempre que uma entidade `ZoneData+PrefabData` recebe a tag `Created` e chama `GetNextIndex()`
   (`Prefabs/ZoneSystem.cs:167-169,205-219`), que devolve `math.max(1, m_ZonePrefabs.Length)` (ou um
   slot vago) — ou seja, o índice de cada `ZonePrefab` depende **da ordem em que os prefabs de zona
   são registrados nesse boot específico** (`Prefabs/PrefabSystem.AddPrefab`,
   `Prefabs/PrefabSystem.cs:88`). `ZoneSystem` **não implementa `ISerializable`** (compare a
   assinatura em `Prefabs/ZoneSystem.cs:16` com `Prefabs/PrefabSystem.cs:24`, que É
   `ISerializable` para o índice genérico de prefab) — então essa numeração não é restaurada do
   save, é recriada do zero a cada boot. `Zones/LoadSystem.cs:9-37` só marca blocos `Updated` em
   `Purpose.NewGame`; não existe nenhum passo de remap de índice de zona (busquei por
   `Remap`/`IndexRemap` em `decomp/Game/Game` e não achei nada). `Cell.Deserialize`
   (`Zones/Cell.cs:22-43`) lê `m_Zone` cru, sem tradução.
   - **Consequência 1 (a mais grave):** o join do CS2M-Coop transfere o save inteiro do host pro
     client via `SaveGameSystem`/`GameManager.Load` padrão do jogo
     (`CS2M/Helpers/SaveLoadHelper.cs:201-246`, patch de `StreamBinaryReader.ReadBytes` em
     `CS2M/Helpers/SaveLoadHelper.cs:260-287`). Isso significa que o client desserializa
     `Cell.m_Zone.m_Index` usando o esquema de numeração **do host**, mas o `ZoneSystem` do client
     já reatribuiu seus próprios índices no boot dele, na ordem de registro de prefab **do client**.
     Se a ordem de registro dos `ZonePrefab` diferir entre as duas máquinas (DLC de zona diferente,
     mod de zona em ordem diferente, etc.), o client vai literalmente **ler zonas erradas assim que
     entra** — antes de qualquer ação do jogador. Isso bate exatamente com o observado ("zones
     594vs594 hash-diff logo após o join, ANTES de ação").
   - **Consequência 2:** `CS2M/Sync/StateHashSystems.cs` (o "radar") hashea
     `buf[i].m_Zone.m_Index` cru (`CS2M/Sync/StateHashSystems.cs:515-517`), com um comentário (linhas
     25-26,515-516) dizendo que esse é "o valor exato que o sync de zona manda pelo fio" — **isso
     está desatualizado/errado em relação ao código atual** (ver §5: o sync real manda o NOME, não o
     índice). Então o radar pode acusar "hash-diff" mesmo quando as duas máquinas pintaram
     exatamente a mesma zona, só porque cada uma numera aquela zona diferente internamente —
     **falso positivo de drift**, sustentando a suspeita já registrada em memória.

2. **Resolução de bloco por proximidade (posição+tamanho+direção), não por identidade.**
   `ZonePaintApplySystem.FindBlock` (`CS2M/Sync/ZonePaintApplySystem.cs:184-225`) acha o bloco alvo
   pelo `Block` mais próximo dentro de um raio (`MatchEpsilonSq = 4f` → 2m na primeira tentativa,
   16 → 4m no retry, `CS2M/Sync/ZonePaintApplySystem.cs:22,117`), filtrando por `SizeX/SizeY` iguais
   e por um "gate de direção" pra não pegar o bloco espelhado do outro lado da rua
   (`CS2M/Sync/ZonePaintApplySystem.cs:194-207`). É exatamente o padrão "proximidade" que a
   arquitetura do projeto trata como perigoso (nunca identidade explícita) — mitigado, mas não
   eliminado: dois blocos do mesmo tamanho a poucos metros um do outro (comum em más esquinas/lotes
   densos) podem ser confundidos, e o próprio comentário do código já documenta que isso já causou
   bug de pintura espelhada (`CS2M/Sync/ZonePaintApplySystem.cs:199-202`).

3. **Ordenação de desempate por `Entity.Index` dentro do próprio jogo.**
   `CellCheckHelpers.SortedEntity.CompareTo` ordena por `m_Entity.Index`
   (`Zones/CellCheckHelpers.cs:14-22`), e `CellCheckHelpers.BlockOverlap.CompareTo` desempata por
   `m_Group`, depois `m_Priority` e só then por `m_Block.Index - other.m_Block.Index`
   (`Zones/CellCheckHelpers.cs:24-43`). Isso alimenta `CellOverlapJobs.CheckBlockOverlapJob`, que
   decide qual bloco "empresta" sua zona pra célula compartilhada com base em `CheckPriority`
   (`Zones/CellOverlapJobs.cs:557-577`, usa `BuildOrder`/flags de estado da célula) — e só cai no
   desempate por `Entity.Index` **quando esses critérios empatam**. `Entity.Index` não é garantido
   igual entre máquinas (mesma ressalva já documentada em `GAME_SYSTEMS.md:80-81` pra
   `PrefabRef.m_Prefab.Index`). Em blocos novos que colidem com prioridade empatada (situação
   plausível em interseções recém-criadas), as duas máquinas podem escolher um "doador de zona"
   diferente pra célula compartilhada.

4. **Esse "empréstimo de zona" em célula nova nunca é transmitido pelo sync.**
   `ZoneDetectorSystem` trata o primeiro avistamento de um `Block` como baseline silenciosa, sem
   enviar nada (`CS2M/Sync/ZoneDetectorSystem.cs:73-77`). Então, se um bloco novo (ex.: nasceu de uma
   rua nova) já chega com uma célula pré-zonada pelo "empréstimo" do item 3 acima
   (`Zones/CellOverlapJobs.cs:530`), essa zona nunca é diffada/enviada — ela simplesmente vira a
   "verdade local" de cada máquina. Combinado com o item 3, uma divergência nesse "empréstimo" em
   uma célula de esquina pode persistir para sempre, sem o radar de paint pegar (ele só detecta
   *mudanças* relativas à própria baseline de cada máquina).

5. **`Overridden` (prédio real força zona) é puramente local/emergente.**
   `CellOccupyJobs.cs:244-248` seta `CellFlags.Overridden` + sobrescreve `m_Zone` quando um objeto
   realmente colocado ocupa células com zona diferente. Como o crescimento de growables é emergente
   e pode divergir por design (`GAME_SYSTEMS.md:189-190`), duas máquinas podem ter prédios diferentes
   ocupando a mesma área — logo, `Overridden` pode estar setado numa máquina e não na outra. Isso
   importa porque `ApplyZonesSystem.Update` (`Tools/ApplyZonesSystem.cs:122-134`) **ignora silenciosamente**
   uma repintura remota sobre uma célula `Overridden` cuja zona não bate — então uma repintura pode
   "pegar" numa máquina e ser descartada na outra, sem nenhum log alertando.

6. **Sub-bloco (`SubBlock`) não persiste e é reconstruído no load** (`Zones/SubBlock.cs:8`,
   `IEmptySerializable`) — isso é seguro (não carrega `Entity` cru pelo save), mas significa que a
   lista de sub-blocos de uma rua é recomputada localmente a cada load; combinado com o item 1, é
   mais uma superfície onde a ordem local de (re)criação de blocos pode diferir entre host e client
   logo após o join.

## 5. O que o CS2M faz hoje

- **`CS2M/Sync/ZoneSync.cs`** — mantém, por máquina, um dicionário `ushort m_Index ↔ string nome do
  ZonePrefab` (`CS2M/Sync/ZoneSync.cs:18-19,24-52`) e expõe `Name(index)`/`Index(name)`
  (`CS2M/Sync/ZoneSync.cs:55-69`). **Isso já neutraliza corretamente o perigo #1 no fio**: o sync
  manda o NOME estável, nunca o índice cru. `IsKnown(name)` (`CS2M/Sync/ZoneSync.cs:74-77`) distingue
  um "Unzoned" legítimo (dezone, nome vazio/índice 0) de um nome desconhecido (que não deve ser
  escrito, senão dezona sem querer) — já corrigido um bug histórico de dezone que não sincava
  (comentário em `CS2M/Sync/ZonePaintApplySystem.cs:151-154`).
- **`CS2M/Sync/ZoneDetectorSystem.cs`** — a cada frame, para todo `Block` `Updated` (exclui
  `Temp`/`Deleted`, `CS2M/Sync/ZoneDetectorSystem.cs:31-44`), tira um snapshot de
  `Cell[].m_Zone.m_Index`, diffa contra o snapshot anterior (`CS2M/Sync/ZoneDetectorSystem.cs:60-107`)
  e manda `ZonePaintCommand` só com os índices de célula que mudaram + o NOME da nova zona
  (`CS2M/Sync/ZoneDetectorSystem.cs:88-124`). Guarda de eco via `ZoneEcho.IsMarked`
  (`CS2M/Sync/ZoneDetectorSystem.cs:82-86`, `CS2M/Sync/ZoneEcho.cs`) evita reenviar a própria aplicação
  remota.
- **`CS2M/Commands/Data/Game/ZonePaintCommand.cs`** — identifica o bloco alvo por
  `BlockX/BlockZ/DirX/DirZ/SizeX/SizeY` (posição/direção/tamanho do `Block`, valores de mundo
  cross-machine-estáveis) em vez de `Entity`; carrega `CellIndices[]` + `ZoneNames[]` em paralelo
  (`CS2M/Commands/Data/Game/ZonePaintCommand.cs:11-24`). Classificado `WorldContract` em
  `CS2M/Sync/SyncContract.cs:55`, mapeado a partir de `ZoneToolSystem` em
  `CS2M/Sync/SyncContract.cs:112`.
  `ZonePaintHandler` só enfileira (`TransactionCmd = false`,
  `CS2M/Commands/Handler/Game/ZonePaintHandler.cs:9-19`).
- **`CS2M/Sync/ZonePaintApplySystem.cs`** — no recebimento, acha o bloco local por proximidade
  (`FindBlock`, §4 item 2), reescreve só as células cujo nome é `IsKnown`
  (`CS2M/Sync/ZonePaintApplySystem.cs:150-165`), marca `Updated`, atualiza o snapshot e chama
  `ZoneEcho.Mark` (`CS2M/Sync/ZonePaintApplySystem.cs:167-177`). Trata o caso de o bloco ainda não
  existir no receptor (rua sincronizada há pouco) com uma fila de retry por até ~15s
  (`CS2M/Sync/ZonePaintApplySystem.cs:24-28,80-105`) em vez de descartar — histórico de bug
  documentado no próprio comentário (linhas 24-26).
- **`CS2M/Sync/StateHashSystems.cs`** — radar de divergência: conta blocos e hashea
  `Cell.m_Zone.m_Index` cru por célula (`CS2M/Sync/StateHashSystems.cs:499-527`), comparando
  host×local (`CS2M/Sync/StateHashSystems.cs:763`). Ver §4 item 1 pro problema disso.
- **Transferência de save no join** — `CS2M/Helpers/SaveLoadHelper.cs` faz o host rodar
  `SaveGameSystem` (`Purpose.SaveGame`) pra um stream fatiado, manda pela rede, e o client roda
  `GameManager.Load(GameMode.Game, Purpose.LoadGame, ...)` interceptando `ReadBytes` via Harmony
  (`CS2M/Helpers/SaveLoadHelper.cs:201-287`) — é o caminho que carrega o `Cell.m_Zone.m_Index` cru do
  host pro client sem tradução (§4 item 1).

## 6. GAPS e recomendação

Checklist do que falta para o zoneamento convergir de forma robusta:

1. **Remapear `ZoneType.m_Index` no handoff de save (join).** Hoje o join transfere o save inteiro
   com índices crus do host (`CS2M/Helpers/SaveLoadHelper.cs:201-246`) e o client reindexará seus
   próprios `ZonePrefab`s de forma independente (`Prefabs/ZoneSystem.cs:131-203`). Precisa de um
   passo, logo após o `LoadGame` no client e antes de liberar o jogo pra "PLAYING", que:
   - construa a mesma tabela `índice↔nome` que `ZoneSync.EnsureBuilt` já constrói
     (`CS2M/Sync/ZoneSync.cs:24-52`), mas usando o **mapeamento do HOST** (recebido separadamente,
     ex.: como parte do handshake de join) e o mapeamento local;
   - percorra todo `Cell` (e `VacantLot.m_Type`, hoje não tocado em lugar nenhum do mod) e reescreva
     `m_Index` do esquema do host pro esquema local, célula por célula, uma vez, antes de qualquer
     diff.
   - Alternativa mais barata: já que o vanilla parece atribuir índices na mesma ordem quando os
     mesmos prefabs de zona existem nas mesmas condições (não confirmei a fonte exata de ordenação —
     ver §7), pelo menos **logar/alertar** se `IndexToName`/`NameToIndex` construídos após o load
     diferirem em algum nome-índice entre host e client, pra distinguir um drift real de um drift de
     numeração.
2. **Trocar o radar de zona pra hashear por NOME, não por índice cru.** Hoje
   `StateHashSystems.AccBlocks` (`CS2M/Sync/StateHashSystems.cs:499-527`) mistura
   `Mix(i, buf[i].m_Zone.m_Index)`. Deveria usar `ZoneSync.Name(buf[i].m_Zone.m_Index)` (hash da
   string, ou de um id estável derivado dela) — assim o radar só acende quando a pintura realmente
   difere, não quando a numeração-por-máquina difere. É uma correção pequena e direta.
3. **Endereçar bloco por chave estável e não só por proximidade.** `FindBlock`
   (`CS2M/Sync/ZonePaintApplySystem.cs:184-225`) já mitiga bem com filtro de tamanho + direção +
   epsilon, mas continua sendo proximidade. Já que `Block` é 100% derivado de `Edge`s de rua
   (`Zones/BlockSystem.cs`), dá pra amarrar a identidade do bloco a
   `(Entity da Edge dona da rua via Owner, índice do bloco dentro dessa Edge)` em vez de posição
   crua — mais parecido com o padrão "identidade explícita" já usado pra outros sistemas do mod.
4. **Cobrir `VacantLot.m_Type`.** Esse buffer é persistido (`Zones/VacantLot.cs:9,37-62`) e carrega
   outro `ZoneType` cru, mas nenhum arquivo do mod (`ZoneSync`/`ZoneDetectorSystem`/
   `ZonePaintApplySystem`) toca nele — hoje ele só se realinha via a mesma simulação local
   (`Zones/LotSizeJobs.cs`, não auditado a fundo aqui) e sofreria do mesmo perigo de índice cru
   descrito em §4.1 sem estar no radar de hash (`AccBlocks` não lê `VacantLot`).
5. **Detectar/registrar o desempate por `Entity.Index` do próprio jogo (§4 item 3).** Não dá pra
   sincronizar isso diretamente (é interno do `CellOverlapJobs`), mas dá pra pelo menos fazer o radar
   incluir uma amostra dos `CellFlags.Shared` além do `m_Zone`, pra distinguir "discordância de zona
   pintada" de "discordância de resolução de sobreposição" quando o hash bater errado.
6. **`ZoneDetectorSystem`'s "first sight" (§4 item 4) deveria comparar contra o estado do HOST, não
   contra si mesmo, para blocos recém-criados por rua sincronizada** — hoje qualquer zona já presente
   num bloco na primeira vez que ele aparece nunca é enviada, mesmo que tenha vindo de um
   "empréstimo" de sobreposição potencialmente divergente.

## 7. NÃO VERIFICADO

- **Fases exatas (`UpdateInGroup`/`UpdateBefore`/`UpdateAfter`) de `ZoneToolSystem`,
  `GenerateZonesSystem`, `ApplyZonesSystem`, `BlockSystem`, `CellCheckSystem`.** O código
  decompilado não mostra esses atributos nas classes (procurei literalmente por
  `UpdateInGroup`/`UpdateBefore`/`UpdateAfter` em `Tools/GenerateZonesSystem.cs`,
  `Zones/BlockSystem.cs`, `Zones/CellCheckSystem.cs` e não encontrei nada, nem uma referência
  central tipo "SystemOrder" que mapeie tipo→fase). Infiro a ordem relativa (1 < 4 < 5) só pela
  numeração das classes `ModificationBarrierN`/`ToolOutputBarrier` usadas por cada sistema, que é a
  mesma convenção já adotada no projeto (`BlockSystem@Mod4` etc.), mas não confirmei o mecanismo de
  registro em si.
- **Qual é exatamente o critério de ordenação com que `PrefabSystem`/`ZoneSystem` registram
  `ZonePrefab`s no boot** (alfabético, por GUID de asset, por ordem de carregamento de disco/mods) —
  vi só o resultado (`GetNextIndex()` incrementando na ordem de chegada da tag `Created`,
  `Prefabs/ZoneSystem.cs:131-219`), não a fonte da ordem em si (`PrefabSystem.AddPrefab`,
  `Prefabs/PrefabSystem.cs:88`, não tive tempo de seguir até a chamada que itera o asset database).
  Isso significa que não confirmei se dois PCs com o MESMO conjunto de mods garantidamente produzem
  a MESMA ordem (o que tornaria o perigo do §4.1 só relevante quando os DLCs/mods de zona diferem
  entre PCs) ou se a ordem já é inerentemente frágil mesmo com mods idênticos.
- **Se o `Purpose.LoadGame` do client roda `ZoneSystem.InitializeZonePrefabs` antes ou depois de
  desserializar os `Block`/`Cell` do save** — não segui a ordem de boot completa; não muda a
  conclusão (a tabela de índice é local e não persiste), mas não confirmei o timing exato.
- **`ZoneUtils.CanShareCells`** (usado em `Tools/GenerateZonesSystem.cs:214`, floodfill) — não abri o
  corpo dessa função; não confirmei se ela usa algo por-máquina para decidir se dois blocos podem
  compartilhar zona no floodfill.
- **`LotSizeJobs`/`CellBlockJobs`** — citados como parte do pipeline (§2 passo 5) mas não auditados
  linha a linha; não confirmei se há alguma outra fonte de não-determinismo aí (floats acumulados,
  iteração dependente de ordem) além do que já achei em `CellOverlapJobs`/`CellCheckHelpers`.
- **Divergência de "campo de fazenda"** mencionada na memória do projeto — é um mecanismo de
  `Game.Areas` (áreas de extração/trabalho), não de `Game.Zones` (células de zoneamento); fora do
  escopo deste dossiê, não investigado aqui.
- **Se existe algum passo de remap de índice de zona em algum lugar fora de `decomp/Game/Game`**
  (ex.: em assemblies de UI/Editor não decompilados aqui, ou em código gerenciado por asset bundle)
  — busquei só dentro de `decomp/Game/Game`; não posso garantir 100% que não exista um remap em
  outra assembly do jogo que eu não tenha acesso.
