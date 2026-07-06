# Áreas (distritos, lotes, superfícies, campos de fazenda) — dossiê de sync

Fonte: `decomp/Game/Game/Tools/AreaToolSystem.cs`, `decomp/Game/Game/Areas/*`,
`decomp/Game/Game/Simulation/AreaSpawnSystem.cs`, `decomp/Game/Game/Common/{RandomSeed,PseudoRandomSeed,SystemOrder}.cs`.
Mod: `CS2M/Sync/AreaEditSystems.cs`, `CS2M/Sync/AreaSpawnSuppressSystem.cs`, `CS2M/Sync/StateHashSystems.cs`,
`CS2M/Sync/SyncContract.cs`, `CS2M/Commands/Data/Game/AreaEditCommand.cs`, `CS2M/Commands/Handler/Game/AreaEditHandler.cs`.

## 1. Entradas do jogador

`AreaToolSystem` (`Tools/AreaToolSystem.cs:28`) é a ferramenta única para distrito, lote (bordas
raramente editadas manualmente), superfície pintada e área de trabalho/extração (campo de
fazenda/floresta/mina/poço). Tem dois modos (`Tools/AreaToolSystem.cs:30-34`, enum `Mode`):
`Edit` (desenhar/editar/apagar um polígono) e `Generate` (grade automática de tiles, usada pelo
editor de mapa/distrito — linha 1473-1503, fora do escopo de gameplay normal).

Máquina de estados por frame em `OnUpdate` (`Tools/AreaToolSystem.cs:3049-3209`):
- `State.Default`/`State.Create`: `base.applyAction.WasPressedThisFrame()` dispara `Apply(...)`
  (linha 3139-3142); cada clique adiciona um `ControlPoint` até fechar o polígono.
- `State.Modify`: arrastar um vértice/aresta de uma área existente; `applyAction.WasReleasedThisFrame()`
  confirma (linha 3155-3158).
- `State.Remove`: apagar um vértice; `secondaryApplyAction` confirma (linha 3167-3170).

`Apply()` (`Tools/AreaToolSystem.cs:3396` em diante) resolve o próximo estado e chama
`SnapControlPoints` (job `SnapJob`, linha 63-1080 — resolve o snap em rede/objeto/área existente) e
`UpdateDefinitions` (linha 3777-3836), que agenda `CreateDefinitionsJob` (linha 1347) escrevendo no
`EntityCommandBuffer` do `ToolOutputBarrier` (`m_ToolOutputBarrier`, campo em 2503, atribuído em 2652).

## 2. Fluxo de aplicação

Diagrama por fase (`SystemUpdatePhase`, ordem geral documentada em `GAME_SYSTEMS.md:40-46`; fases
de área citadas em `Common/SystemOrder.cs`):

```
ToolUpdate      AreaToolSystem.OnUpdate → Apply() → CreateDefinitionsJob.Edit()/Generate()
                (Common/SystemOrder.cs:698)          cria Entity+CreationDefinition+Node(buffer)+Updated
                                                      no ECB do ToolOutputBarrier
                                                      (Tools/AreaToolSystem.cs:1562-1580, 3794-3833)
                     ↓
ApplyTool       ApplyAreasSystem.OnUpdate           consome Temp+Area (m_TempQuery, linha 271):
                (Common/SystemOrder.cs:716)          PatchTempReferencesJob (linha 19-76) corrige Owner
                                                      quando o original foi substituído;
                                                      HandleTempEntitiesJob (linha 79-214):
                                                        Create()  → remove Temp, soma Applied+Created+Updated
                                                                    (linha 204-208, m_AppliedTypes)
                                                        Update()  → sobrescreve o Node buffer do m_Original,
                                                                    marca Updated no original, Deleted no shadow
                                                                    (linha 167-202)
                                                        Delete()  → Deleted no original e no shadow (158-165)
                     ↓
GameSimulation  AreaSpawnSystem.OnUpdate            roda a cada 64 frames (GetUpdateInterval, linha 755-758)
                (Common/SystemOrder.cs:580)          sobre TODA Area com Geometry+SubObject, sem Temp/Destroyed/
                                                      Deleted (m_AreaQuery, linha 768); se tem Storage ou
                                                      Extractor, calcula área-alvo de objetos decorativos
                                                      (CalculateStorageObjectArea/CalculateExtractorObjectArea,
                                                      linha 176-189) e spawna SubObjects reais via
                                                      CreationDefinition+ObjectDefinition (SpawnObject,
                                                      linha 566-595) usando Random tirado de
                                                      RandomSeed.Next() (linha 811) — ver §4.
                     ↓
Modification2B  Game.Areas.GeometrySystem.OnUpdate  TriangulateAreasJob (linha 27-158) sobre toda Area
                (Common/SystemOrder.cs:124)          Updated+Node+Triangle (ou TODAS no primeiro load,
                                                      GetLoaded()==true, linha 781): recalcula
                                                      AreaFlags.CounterClockwise/NoTriangles a partir do
                                                      SINAL da área do polígono atual (linha 99-130),
                                                      triangula (Triangulate, linha 857) e regrava
                                                      Geometry (m_CenterPosition/m_Bounds/m_SurfaceArea,
                                                      derivado — ver §3), amostrando altura de
                                                      terreno/água quando o nó não tem elevação fixa
                                                      (m_TerrainHeightData/m_WaterSurfaceData, linha
                                                      802-803, 813-814). Se `AreaFlags.Slave` está setado,
                                                      antes disso `GenerateSlaveArea` (linha 160-284)
                                                      RECONSTRÓI o buffer de nós inteiro copiando do
                                                      owner e recortando "buracos" de prédios — ver §4.
                     ↓
Modification4B  AreaConnectionSystem.OnUpdate       sobre toda Area com buffer SubLane que ficou
                (Common/SystemOrder.cs:179)          Updated/Deleted (m_ModificationQuery, linha 886-901,
                                                      ModificationBarrier4B); se a Area (tipicamente um
                                                      Lote) não tem PseudoRandomSeed ainda, sorteia um NOVO
                                                      (linha 217-231) a partir do MESMO RandomSeed
                                                      divergente — ver §4 — e usa para escolher/posicionar
                                                      as pistas de borda do lote (UpdateLanes→
                                                      GetRandom(kAreaBorder), linha 297).
                     ↓
ModificationEnd AreaResourceSystem.OnUpdate         dispara quando m_UpdatedAreaQuery (Updated +
                (Common/SystemOrder.cs:259)          Extractor|MapFeatureElement) não está vazia, OU há
                                                      Brush aplicado (pincel de recurso do editor), OU
                                                      objetos próximos mudaram (linha 879-920). Para cada
                                                      Area com Extractor, recalcula m_ResourceAmount do
                                                      ZERO (linha 382) a partir do polígono (Node+Triangle)
                                                      cruzado com:
                                                        - MapFeature.FertileLand/Ore/Oil/Fish →
                                                          textura estática m_NaturalResourceData (mapa de
                                                          fertilidade/minério/petróleo/peixe, linha
                                                          492-561) — puramente geométrico, DETERMINÍSTICO
                                                          dado o mesmo polígono + o mesmo save.
                                                        - MapFeature.Forest → soma de árvores REAIS dentro
                                                          do triângulo via quadtree de objetos
                                                          (`m_ObjectTree`/WoodIterator, linha 437-490,
                                                          657-712) — depende de quais árvores EXISTEM
                                                          fisicamente ali, não só do polígono.
```

## 3. Estado persistido tocado

Componentes `ISerializable` (persistem no save; são os que precisam convergir):

| Componente | Campos | Onde |
|---|---|---|
| `Game.Areas.Area` | `m_Flags` (`AreaFlags`: Complete/CounterClockwise/NoTriangles/Slave) | `Areas/Area.cs:6-29` |
| `Game.Areas.Node` (buffer) | `m_Position` (float3), `m_Elevation` (float) | `Areas/Node.cs:9-40` |
| `Game.Areas.District` | `m_OptionMask` (uint) | `Areas/District.cs:6-19` |
| `Game.Areas.Extractor` | `m_ResourceAmount`, `m_MaxConcentration`, `m_ExtractedAmount`, `m_WorkAmount`, `m_HarvestedAmount`, `m_TotalExtracted` (floats), `m_WorkType` | `Areas/Extractor.cs:7-57` |
| `Game.Areas.Storage` | `m_Amount` (int), `m_WorkAmount` (float) | `Areas/Storage.cs:6-27` |
| `Game.Common.PseudoRandomSeed` | `m_Seed` (ushort) — usado em `Game.Areas.Lot` para as pistas de borda | `Common/PseudoRandomSeed.cs:7-80` |

Componentes `IEmptySerializable` (NÃO são salvos — recalculados no load/replay, então não entram
na conta de "o que sincronizar" desde que os dados de origem convirjam):
- `Game.Areas.Geometry` (`m_Bounds`, `m_CenterPosition`, `m_SurfaceArea`) — `Areas/Geometry.cs:8-15`,
  recomputado por `GeometrySystem.TriangulateAreasJob` a cada Updated (§2).
- `Game.Areas.Lot` — marcador vazio, `Areas/Lot.cs:7-10`.
- `Game.Areas.Triangle` (não lido aqui, mas é buffer derivado da triangulação, mesmo raciocínio).

## 4. Perigos cross-machine

1. **`Game.Common.RandomSeed` é semeado por relógio de parede do PROCESSO, não do save.**
   `private static Unity.Mathematics.Random m_Random = new Unity.Mathematics.Random((uint)DateTime.Now.Ticks);`
   (`Common/RandomSeed.cs:8`). Cada chamada a `RandomSeed.Next()` (`Common/RandomSeed.cs:12-17`) tira
   o PRÓXIMO valor dessa sequência por-processo — host e cliente têm processos diferentes, logo
   sequências diferentes DESDE O FRAME 1. Dois consumidores diretos na mecânica de área:
   - `AreaSpawnSystem.OnUpdate` chama `RandomSeed.Next()` a cada execução (a cada 64 frames,
     `Simulation/AreaSpawnSystem.cs:811`) e usa o resultado para: (a) sortear QUAL prefab de
     sub-objeto decorativo colocar (`TryGetObjectPrefab`, `Simulation/AreaSpawnSystem.cs:308-384`,
     usa `random.NextInt`), e (b) sortear ONDE dentro do polígono
     (`AreaUtils.TryGetRandomObjectLocation`, chamado em `Simulation/AreaSpawnSystem.cs:258`). O
     resultado é uma Entity REAL, persistida, criada via `CreationDefinition`+`ObjectDefinition`
     (`Simulation/AreaSpawnSystem.cs:566-595`) — não é cosmético de render, é estado de save.
   - `AreaConnectionSystem`: quando uma Area (tipicamente um Lote) ainda não tem
     `PseudoRandomSeed`, sorteia um novo ali mesmo a partir do mesmo `RandomSeed`
     (`jobData.m_RandomSeed = RandomSeed.Next()` em `Areas/AreaConnectionSystem.cs:938`; consumo em
     `Areas/AreaConnectionSystem.cs:217-231`), e usa esse seed para escolher/posicionar as pistas de
     borda do lote (`GetRandom(PseudoRandomSeed.kAreaBorder)`, `Areas/AreaConnectionSystem.cs:297`).
     Isso acontece na criação de QUALQUER Lote com buffer `SubLane` (todo prédio plausivelmente),
     não só em campos de fazenda.
   Efeito prático: mesmo que o polígono do campo/lote sincronize perfeitamente, os objetos
   decorativos (fileiras de plantação, fardos de feno, silos, pilhas) e as pistas de borda do lote
   nascem DIFERENTES em cada máquina, porque nascem de uma fonte de aleatoriedade que nunca foi
   pensada para ser determinística entre processos.

2. **`Extractor.m_ResourceAmount` para floresta depende de árvores REAIS, não só do polígono.**
   `AreaResourceSystem.CalculateWoodResources` (`Areas/AreaResourceSystem.cs:437-490`) soma
   `ObjectUtils.CalculateWoodAmount` de cada árvore encontrada por uma quadtree de objetos
   (`WoodIterator`, `Areas/AreaResourceSystem.cs:657-712`, filtrando `BoundsMask.IsTree`) DENTRO do
   triângulo da área. Se a população de árvores ali diferir entre as máquinas (crescimento/plantio
   têm sua própria cadeia de determinismo, fora do escopo deste dossiê), o valor de recurso do campo
   de FLORESTA diverge mesmo com o polígono idêntico. Para FertileLand/Ore/Oil/Fish o cálculo
   (`CalculateNaturalResources`, `Areas/AreaResourceSystem.cs:492-561`) é puramente geométrico sobre
   uma textura estática (`m_NaturalResourceData`) — não achei fonte de divergência aqui além do
   próprio polígono e do modificador de cidade (`CityModifier`, que já é estado de cidade
   sincronizado por outro subsistema, fora do escopo deste dossiê).

3. **Vanilla marca reshape de área como `Updated`, nunca `Applied`.** Documentado em
   `GAME_SYSTEMS.md:206-209` e confirmado operacionalmente pelo comentário do próprio mod em
   `CS2M/Sync/AreaEditSystems.cs:74-76` ("v51 FIELD FIX"). Um detector que só olha `Applied` nunca
   vê a edição — precisa de diff de hash de polígono por polling (~1 Hz), não é instantâneo.

4. **`GenerateSlaveArea` reconstrói o polígono a partir da ORDEM de iteração de `m_Buildings`.**
   Para uma Area com `AreaFlags.Slave` (sub-área "buraco" recortada ao redor de prédios dentro de um
   Lote), `Areas/GeometrySystem.cs:160-284` copia os nós do owner e insere recortes por prédio
   iterando `m_Buildings[j]` (uma `NativeList<Entity>` de todos os prédios do owner) usando um
   algoritmo guloso de menor distância (`CanAddEdge`) para decidir onde costurar cada buraco. Em
   caso de empate de distância (posições simétricas), a ORDEM de iteração decide o resultado — não
   verifiquei se essa ordem é estável entre máquinas (depende de ordem de criação/arquétipo das
   entidades prédio). Risco de cauda, não confirmado como causa real de divergência observada.

5. **`GeometryFlags.PseudoRandom`** é setado para toda Area tipo `Lot` na inicialização do prefab
   (`Prefabs/AreaInitializeSystem.cs:307`: `geometryFlags = GeometryFlags.PhysicalGeometry |
   GeometryFlags.PseudoRandom`). Não encontrei o consumidor da flag dentro de `Areas/`/`Simulation/`
   nesta varredura — ver §7 (NÃO VERIFICADO).

6. **`AreaFlags.CounterClockwise`/`NoTriangles` NÃO precisam ser sincronizados à parte** — são
   recomputados do zero a cada triangulação a partir do sinal da área do polígono
   (`Areas/GeometrySystem.cs:99-130`, `176`). Contanto que o `Node` buffer convirja, essas flags
   convergem sozinhas. Confirmado, não é gap.

7. **Identidade de prefab**: `PrefabRef.m_Prefab.Index` não é estável entre máquinas
   (`GAME_SYSTEMS.md:80-84`, regra geral do jogo). O `AreaEditCommand` do mod evita isso corretamente
   endereçando por nome de prefab (`PrefabType`+`PrefabName`, ver §5).

## 5. O que o CS2M faz hoje

`CS2M/Sync/AreaEditSystems.cs`:
- `AreaEditDetectorSystem` — detecta área de trabalho (`Extractor`) recém-`Applied`
  (`_appliedAreas`, linhas 101-119) e área standalone sem dono (`_appliedStandalone`, linhas
  122-140) e manda o polígono inteiro (`Xs/Ys/Zs/Els`) + identidade do dono
  (`CS2M_SyncId`/nome+posição de prefab como fallback). Para EDIÇÃO (reshape), como o vanilla só
  marca `Updated`, roda `ScanWorkAreaEdits` a ~1 Hz (linhas 237-241, 248-349) comparando um hash de
  polígono (`WorkAreaHash.Compute`, linhas 530-543) contra o snapshot anterior. **Autoridade: só o
  HOST manda** (linha 285-288, decisão explícita registrada no comentário "Fase 1" — cliente
  editando o campo NÃO propaga, limitação aceita). Delete sincroniza por centro+prefab
  (`DetectDeleted`, linhas 437-514), com exceção de cascata (área cai porque o dono morreu —
  não reenvia, linha 454-477).
- `AreaEditApplySystem` — resolve o dono (`ResolveOwner`, linhas 970-1053, por `CS2M_SyncId` ou por
  proximidade de Transform+nome de prefab, com um fallback específico para "Agriculture Area
  Placeholder"), reescreve o `Node` buffer da área já existente (linhas 706-760) OU, se a área ainda
  não foi regenerada localmente pelo sistema de sub-áreas do prédio, ESTACIONA o comando e tenta de
  novo por até 300 frames (`AreaRetryTtlFrames`, linha 576) antes de criar via `CreationDefinition`
  Permanent (linhas 769-801) — evita duplicar a sub-área que o próprio jogo regenera localmente.
  Marca `CS2M_RemotePlaced` como guarda de eco em toda escrita remota.
- `DistrictDetectorSystem`/`DistrictApplySystem` (`CS2M/Sync/DistrictDetectorSystem.cs`) — mesmo
  padrão de polígono + hash-diff a ~1 Hz (`ScanDistrictReshapes`, linhas 178-252), mas SEM a
  limitação de autoridade única (qualquer lado pode reenviar, endereçando pelo centróide ANTERIOR,
  linha 232-243) e shipando `District.m_OptionMask` (linha 143-150) — único campo persistido do
  componente, cobertura completa confirmada (§3).
- `StateHashSystems.cs` — o radar de divergência (`AreaInContract`, linhas 551-560) inclui no hash
  apenas `Geometry.m_CenterPosition` de áreas SEM dono (distrito/superfície standalone) OU com
  `Extractor` — exclui deliberadamente sub-áreas decorativas donas-de-prédio (Surface/Space/
  Hangaround/Walking) porque "regeneram localmente por PC" (comentário, linha 551-554). Não hasheia
  contagem de nós, forma do polígono ou os valores de `Extractor`/`Storage`.
- `AreaSpawnSuppressSystem.cs` — mitigação experimental: enquanto CLIENTE, desliga o
  `AreaSpawnSystem` local inteiro (`_areaSpawn.Enabled = false`, linha 47) para não gerar decoração
  divergente. **Desligado por padrão** (exige `CS2M_AREASUPPRESS=1`, linha 36) porque, por si só, NÃO
  substitui a decoração suprimida por nada vindo do host — o próprio comentário do arquivo assume o
  risco ("cliente pode ficar com o campo vazio", linhas 14-17).

## 6. GAPS e recomendação

1. **Os SubObjects decorativos do `AreaSpawnSystem` nunca são sincronizados como entidades.**
   `AreaEditCommand` só carrega o polígono do `Extractor`/superfície (§5); as entidades reais criadas
   por `SpawnObject` (`Simulation/AreaSpawnSystem.cs:566-595`) — fileiras de plantação, fardos, silos —
   não têm nenhum canal de sync. A única mitigação (`AreaSpawnSuppressSystem`) troca "decoração
   divergente" por "nenhuma decoração no cliente", e está desligada por padrão. Checklist pra um sync
   correto: ou (a) capturar e sincronizar a seed usada por `SpawnObject`/`TryGetObjectPrefab`
   (transformar `RandomSeed.Next()` num valor host-autoritativo transmitido junto com o comando de
   área, igual já se faz para `CreationDefinition.m_RandomSeed` em outras mecânicas), ou (b) ligar
   `AreaSpawnSuppressSystem` por padrão E adicionar um canal que replique as entidades de decoração
   do host (via `CreationDefinition`, como qualquer objeto).

2. **`Extractor.m_ResourceAmount`/`m_ExtractedAmount`/`m_WorkAmount`/`m_HarvestedAmount`/
   `m_TotalExtracted` nunca são shipados.** O mod confia inteiramente em `AreaResourceSystem`
   recalcular `m_ResourceAmount` do zero, de forma idêntica, nas duas máquinas — o que é verdade
   para FertileLand/Ore/Oil/Fish (função pura do polígono + textura estática, §4.2) mas NÃO para
   Forest (depende de árvores reais, §4.2), e em NENHUM caso cobre o PROGRESSO de extração
   (`m_ExtractedAmount`/`m_HarvestedAmount`/`m_TotalExtracted`), que é resultado de veículos de
   trabalho rodando localmente em cada máquina — nada aqui garante que os dois lados extraem no
   mesmo ritmo. Checklist: tratar esses campos como estado emergente host-autoritativo (mesmo padrão
   de `MoneySyncSenderSystem`/demanda RCI citado em `GAME_SYSTEMS.md:213-225`) e transmitir
   periodicamente, ou aceitar a divergência e documentar explicitamente (hoje não está documentado
   em lugar nenhum do código do mod).

3. **Escopo do sync de área de trabalho é hard-coded pra `Extractor`.** As queries `_workAreas`/
   `_appliedAreas`/`_deletedAreas` em `AreaEditSystems.cs` (linhas 82-99, 101-119, 143-158) exigem
   `ComponentType.ReadOnly<Game.Areas.Extractor>()`. Uma área de trabalho `Storage`-only (sem
   `Extractor` — ex.: pátio de armazenamento/lixo, se for desenhável pelo jogador) não teria NENHUM
   sync de polígono, nem mesmo de forma. Não verifiquei se existe hoje algum prefab in-game que
   produza uma Area com `Storage` mas sem `Extractor` desenhável pelo jogador (§7).

4. **Reshape client-side de campo é descartado por design (linha 285-288 de
   `AreaEditSystems.cs`).** Aceito e documentado no próprio código como limitação de "Fase 1", mas
   vale registrar explicitamente no checklist de sync: hoje só o HOST pode redesenhar um campo já
   existente; um cliente que redesenha vê sua mudança ser sobrescrita pelo próximo polling do host
   (ou simplesmente ignorada, dependendo do timing).

5. **`PseudoRandomSeed` de pistas de borda de Lote nunca é sincronizado** — é gerado localmente,
   por máquina, na primeira vez que `AreaConnectionSystem` processa um Lote sem seed
   (`Areas/AreaConnectionSystem.cs:217-231`, §4.1). Isso é consistente com a filosofia "decorativo,
   regenera localmente" que `StateHashSystems.AreaInContract` já assume para toda sub-área
   dona-de-prédio — mas é uma escolha de escopo, não algo verificado como inofensivo: se as pistas
   de borda produzem geometria de rede visível (cercas/caminhos), o resultado visual diverge em todo
   prédio colocado, não só em fazendas.

## 7. NÃO VERIFICADO

- Implementação de `AreaUtils.CalculateExtractorObjectArea`, `AreaUtils.CalculateStorageObjectArea`
  e `AreaUtils.TryGetRandomObjectLocation` — a classe `AreaUtils` não está presente na árvore
  decompilada disponível (`decomp/Game/Game`); só vi os pontos de chamada em
  `Simulation/AreaSpawnSystem.cs:176-189, 258`. Não confirmei os detalhes exatos de como a
  "área alvo" de decoração é calculada a partir de `Extractor`/`Storage`.
  - **[SPEC A REFAZER]** Vou pedir a outro agente pra recompilar/gerar o dump de `Game.AreaUtils`
    especificamente (`ilspycmd -t Game.Areas.AreaUtils` ou equivalente), porque essas 3 funções são
    exatamente o motor da "quantidade de decoração" no campo de fazenda e o dossiê fica capenga sem
    elas.
- Onde exatamente `GeometryFlags.PseudoRandom` (setado em `Prefabs/AreaInitializeSystem.cs:307`) é
  lido/consumido — não encontrei o consumidor dentro de `Areas/`ou `Simulation/` nesta varredura;
  pode estar em `Rendering/` (variação de mesh/textura, cosmético puro) ou em um arquivo não
  presente na árvore decompilada.
- Se existe algum prefab de jogo que produza uma Area com `Storage` mas SEM `Extractor` e que seja
  desenhável/editável pelo jogador (relevante para o Gap 3) — não confirmei nenhuma instância
  concreta, só o fato de que o código trata os dois componentes com o mesmo tratamento de "área alvo
  de decoração" em `AreaSpawnSystem`.
- Estabilidade entre máquinas da ordem de iteração de `m_Buildings` em `GenerateSlaveArea`
  (`Areas/GeometrySystem.cs:190-197`) — não tenho evidência de que isso já causou uma divergência
  observada; é um risco de cauda por leitura de código, não uma reprodução.
- Não tracei o pipeline completo de crescimento de árvore/vegetação (fora do escopo "Áreas") que
  alimentaria a divergência de `Extractor.m_ResourceAmount` para floresta citada no Gap 2 — apenas
  confirmei que o cálculo em si LÊ entidades de árvore reais, não uma textura estática.
- Não abri `Tools/GenerateAreasSystem.cs` além do necessário para confirmar que `Mode.Generate` é
  fluxo de editor/geração de tile — não verifiquei se algum caminho de gameplay normal usa
  `Mode.Generate` fora do editor de mapa.
