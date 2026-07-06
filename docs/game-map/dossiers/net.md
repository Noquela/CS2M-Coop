# Redes (ruas/trilhos/tubos/cabos) — dossiê de sync

> Fonte: decomp em `decomp/Game/Game/` (Tools/, Net/, Zones/, Common/, Prefabs/, Serialization/).
> Mod: `CS2M/Sync/` e `CS2M/Commands/Data/Game/`. Toda afirmação abaixo tem `arquivo:linha`.

## 1. Entradas do jogador

- **`NetToolSystem`** (`decomp/Game/Game/Tools/NetToolSystem.cs:34`, `ToolBaseSystem`) é a ferramenta de
  construção (rua/trilho/cano/energia — todos passam pelo mesmo pipeline `Game.Net`). `OnUpdate`
  (`NetToolSystem.cs:5902`) roda todo frame que a ferramenta está ativa: monta `m_ControlPoints` a partir
  de raycasts do cursor, decide `Mode` (Point/Straight/Curve/ContinueStraight/ContinueCurve/Replace/Upgrade
  — enum consultado via `actualMode`, `NetToolSystem.cs:5909`), calcula requisitos de camada
  (`base.requireNet`, `NetToolSystem.cs:5944`) a partir do `NetData`/`NetGeometryData` do prefab
  selecionado (`m_Prefab`, `NetToolSystem.cs:5933-5936`).
- `GetControlPoints(out JobHandle)` (`NetToolSystem.cs:5558`) expõe os pontos de controle publicamente —
  é o hook que o spike de input-replay (memória `input-replay-spike`) usa para dirigir a ferramenta real no
  receptor.
- **`CreateDefinitionsJob`** (struct em `NetToolSystem.cs:2480`, agendado em `NetToolSystem.cs:6974-7017`)
  transforma os control points em uma cadeia de entidades `CreationDefinition` + `NetCourse` + `Temp`
  (preview). Cada `CreationDefinition` recebe um `m_RandomSeed` derivado de `NetToolSystem.m_RandomSeed`
  (campo de instância, `NetToolSystem.cs:5002`; atribuído a `RandomSeed.Next()` em `NetToolSystem.cs:5383`
  e re-gerado em `6205`/`6412`/`6485`; repassado ao job em `NetToolSystem.cs:6981`).
- Ao soltar o botão, a definição perde `Temp` e ganha `Applied` (mecanismo genérico do
  `ToolOutputSystem`/`ToolOutputBarrier`, documentado em `GAME_SYSTEMS.md:107-113`; NÃO naveguei o
  `ToolOutputSystem.cs` linha a linha nesta sessão — ver seção 7).
- **Upgrade retroativo** (sidewalk/árvore/iluminação/quay/muro-anti-ruído em rede já existente) é uma
  ferramenta DIFERENTE: `UpgradeToolSystem` (`decomp/Game/Game/Tools/UpgradeToolSystem.cs:24`, herda
  `ObjectToolBaseSystem`, `kToolID = "Upgrade Tool"` em `UpgradeToolSystem.cs:38`) — tem seu PRÓPRIO
  `RandomSeed m_RandomSeed` (`UpgradeToolSystem.cs:54`), mesmo padrão de hazard da seção 4. O CS2M não
  gancha essa ferramenta; cobre o resultado por diff (seção 5).

## 2. Fluxo de aplicação

Pipeline real, fase por fase (fases confirmadas em
`decomp/Game/Game/Common/SystemOrder.cs`, não deduzidas):

1. **Modification1** — `GenerateNodesSystem` (`SystemOrder.cs:96`) consome `CreationDefinition`+`NetCourse`
   (`Applied`) e materializa/atualiza `Node`s reais. Para o traçado que atravessa nós/edges existentes,
   decide fusão/`AddNode`/`AddConnectedNodes` (`GenerateNodesSystem.cs:605-709`).
2. **Modification2** — `GenerateEdgesSystem` (`SystemOrder.cs:111`) consome a mesma definição e materializa
   `Edge`+`Curve`+`PrefabRef`(+`Upgraded`/`Elevation`/`Road` condicionais), atribuindo `BuildOrder` a
   edges genuinamente novas (`GenerateEdgesSystem.cs:1554-1560`, detalhe crítico na seção 4).
3. **Modification2B** — `Game.Net.ReferencesSystem` (`SystemOrder.cs:125`) re-tece `ConnectedEdge`/
   `ConnectedNode`; `NodeReductionSystem` (`SystemOrder.cs:128`) pode colapsar nós intermediários
   redundantes (herdando/mesclando `BuildOrder`, `NodeReductionSystem.cs:649-661` — ver seção 4);
   **`NodeAlignSystem`** (`SystemOrder.cs:129`, `decomp/Game/Game/Net/NodeAlignSystem.cs:21`) recentraliza
   `Node.m_Position`/`m_Rotation` a partir do conjunto ATUAL de `ConnectedEdge` (query
   `ComponentType.ReadOnly<Node>(), ComponentType.ReadOnly<Updated>()`, `NodeAlignSystem.cs:297`) — só
   roda em nós marcados `Updated` NESTE frame.
4. **Modification3** — `CompositionSelectSystem` (`SystemOrder.cs:139`) resolve a composição real
   (faixas/meio-fio) a partir de `Composition{m_Edge,m_StartNode,m_EndNode}`.
5. **Modification4** — `NetCompositionSystem`, `Game.Net.GeometrySystem` (`SystemOrder.cs:148,150`,
   fixed-point de 2 iterações, `decomp/Game/Game/Net/GeometrySystem.cs:3556` conforme
   `docs/atomicbatch-spec.md:91`), `LaneSystem`, **`Zones.BlockSystem`** (`SystemOrder.cs:155-156`) —
   é aqui que `Net.BuildOrder` vira `Zones.BuildOrder` no bloco de zona (seção 4).
6. **Modification5** — `Zones.CellCheckSystem` (`SystemOrder.cs:215`) resolve overlap célula-a-célula
   entre blocos vizinhos usando `Zones.BuildOrder.m_Order` como critério de prioridade
   (`decomp/Game/Game/Zones/CellOverlapJobs.cs:245,258,526,540`).
7. **ModificationEnd** — tudo assentado; é o slot onde os detectores do CS2M capturam (`Mod.cs:181,193`).

### O truque da "definição permanente" (mod → jogo)
O CS2M injeta, ANTES de Modification1, `CreationDefinition{m_Flags=Permanent}` + `NetCourse` sem `Temp`
(`CS2M/Sync/NetPlaceApplySystem.cs` e `CS2M/Sync/NetBatchApplySystem.cs:406-414`, réplica documentada em
`docs/atomicbatch-spec.md:68-85`). O caminho `Permanent` em `GenerateNodesSystem` **retorna cedo** e NÃO
faz fusão por proximidade (`GenerateNodesSystem.cs:1388`: `TryGetOldEntity` só é chamado quando
`(m_CreationFlags & Permanent) == 0`) — confirma `GAME_SYSTEMS.md:121-127`. É por isso que o AtomicBatch
resolve fronteira por `CS2M_NodeSyncId` em vez de deixar o jogo adivinhar.

## 3. Estado persistido tocado

Componentes `ISerializable` que esta mecânica cria/edita (têm que convergir):

| Componente | Onde | Papel |
|---|---|---|
| `Game.Net.Node{m_Position,m_Rotation}` | `decomp/.../Net/Node.cs` | posição/rotação do nó (ver seção 4 sobre convergência) |
| `Game.Net.Edge{m_Start,m_End}` | `decomp/.../Net/Edge.cs` | topologia do segmento |
| `Game.Net.Curve.m_Bezier` | `decomp/.../Net/Curve.cs` | geometria (4 pontos de controle) |
| `Game.Net.PrefabRef` | — | identidade do prefab (nome — NUNCA o hash local) |
| `Game.Net.Upgraded{CompositionFlags}` | — | sidewalk/árvore/iluminação/quay/muro-ruído; também em NÓS (junção: farol/pare/rotatória/faixa) |
| `Game.Net.Elevation{float2}` | — | elevação por ponta |
| `Game.Net.BuildOrder{m_Start,m_End}` | `decomp/Game/Game/Net/BuildOrder.cs:6-10` | ordem de criação — **feito para determinismo de lane/bloco, NÃO sincronizado hoje (seção 4)** |
| `Game.Common.PseudoRandomSeed{m_Seed}` | `decomp/Game/Game/Common/PseudoRandomSeed.cs:7` | semente cosmética (postes/catenária/detalhes/cor) em nó e edge — **sincronizada pelo mod** |
| `Game.Zones.BuildOrder{m_Order}` | `decomp/Game/Game/Zones/BuildOrder.cs:6-8` | ordem de prioridade do BLOCO de zona — **derivada de `Net.BuildOrder`, logo herda a lacuna** |
| `Game.Net.Standalone` (marker) | — | nó sem edge dona (ponta solta) |
| `Game.Net.Road` (condicional) | — | dados de estrada quando prefab tem `RoadData` |

## 4. Perigos cross-machine

1. **`RandomSeed` raiz é `DateTime.Now.Ticks` por processo.**
   `decomp/Game/Game/Common/RandomSeed.cs:8`: `Unity.Mathematics.Random m_Random = new(...DateTime.Now.Ticks)`.
   Cada `NetToolSystem`/`UpgradeToolSystem` local tem seu próprio `m_RandomSeed` (`NetToolSystem.cs:5002`,
   `UpgradeToolSystem.cs:54`) semeado por essa estática — **genuinamente diferente por máquina**. Esse
   valor vira `CreationDefinition.m_RandomSeed` (ex. `NetToolSystem.cs:3529`) e alimenta
   `PseudoRandomSeed` via `GenerateNodesSystem.cs:649` (`PseudoRandomSeed.kEdgeNodes`) e
   `GenerateEdgesSystem.cs:1047-1049` (`kSplitEdge`, ao dividir edge). **Mitigado**: `PseudoRandomSeed` é
   `ISerializable` e o mod EMBARCA o valor exato no wire (`NetPlaceCommand.RandomSeed`,
   `CS2M/Commands/Data/Game/NetPlaceCommand.cs:62`; `NetBatchCommand.NodeSeeds`/`EdgeSeeds`,
   `CS2M/Commands/Data/Game/NetBatchCommand.cs:47,83`) e o receptor grava o MESMO seed
   (`NetBatchApplySystem.cs:329,410`). Uma vez a raiz sincronizada, a derivação por split é determinística
   (XOR de razão fixa, `PseudoRandomSeed.cs:63-69`) — sem novo hazard downstream.

2. **`Net.BuildOrder` é um contador PERSISTENTE POR PROCESSO, nunca sincronizado — GAP confirmado.**
   `GenerateEdgesSystem` mantém `NativeValue<uint> m_BuildOrder` (`Allocator.Persistent`,
   `GenerateEdgesSystem.cs:2093`). Para cada edge GENUINAMENTE NOVA (`definitionData.m_Original ==
   Entity.Null`) ele atribui `BuildOrder.m_Start = m_BuildOrder + entityIndex*16` / `m_End = m_Start+15`
   (`GenerateEdgesSystem.cs:1554-1560`) e, ao fim do frame, `UpdateBuildOrderJob` avança o contador para
   `max(atual, max(build_orders_criados_este_frame)+1)` (`GenerateEdgesSystem.cs:1855-1867`). Esse
   contador é **local a cada instância do jogo** — soma TODA criação de edge que passa pelo
   `GenerateEdgesSystem` DAQUELA máquina, incluindo as aplicadas via `CreationDefinition` do próprio mod
   (`NetPlaceApplySystem`/`NetBatchApplySystem`/`NetToolReplaySystem`, todos "vanilla path"). No apply do
   AtomicBatch, o valor `EdgeBuildOrderStart/End` VIAJA no pacote
   (`NetBatchCommand.cs:85-87`, capturado em `NetBatchCaptureSystem.cs:249-257`) mas **é explicitamente
   IGNORADO na aplicação**: `NetBatchApplySystem.cs:417-419` — *"BuildOrder is NOT set here: the vanilla
   GenerateEdges pipeline assigns it locally... colisão com counter local do receptor = risco aceito v1"*
   (também documentado como risco aceito em `docs/atomicbatch-spec.md:73`). Consequência: se dois
   jogadores constroem CONCORRENTEMENTE (ou em ordem de chegada de rede diferente do host), o edge de A
   pode ganhar `BuildOrder` MENOR que o de B no host e MAIOR no cliente (cada um processa "eu primeiro,
   o outro depois" na sua própria linha do tempo local) — **valor por-máquina genuíno, não hipotético**.
   `NodeReductionSystem` (automático, Mod2B) também LÊ/ESCREVE `BuildOrder` ao fundir nós redundantes
   (`NodeReductionSystem.cs:649-661`), então a divergência se propaga mesmo sem novo comando de rede.

3. **`Net.BuildOrder` → `Zones.BuildOrder` → prioridade de overlap de célula — a cadeia que quebra zona.**
   `Zones.BlockSystem.CreateBlocks` copia `startOrder`/`endOrder` (= `Net.BuildOrder.m_Start/m_End` do
   edge dono) para o `Zones.BuildOrder` do bloco (`decomp/Game/Game/Zones/BlockSystem.cs:190,196,201,
   287,404,824`) e usa a comparação `endOrder > startOrder` para incrementar/decrementar
   `component.m_Order` bloco a bloco (`BlockSystem.cs:960-966`). Depois,
   `Zones.CellOverlapJobs.CheckPriority` usa esse `m_Order` para decidir QUAL bloco vence quando dois
   blocos vizinhos (de donos de rua diferentes) se sobrepõem numa junção (`CellOverlapJobs.cs:245,258,
   526,540`). Como o `BuildOrder` de origem pode divergir (item 2), o TAMANHO/POSIÇÃO do bloco resultante
   (`Block.m_Position`, `Block.m_Size.x/y`) pode divergir sem que a CONTAGEM de blocos mude — **exatamente
   o sintoma da memória do projeto ("zones 594vs594 hash-diff", mesma contagem, hash diferente)**. Fechando
   o ciclo: o StateHash de zonas mistura `Block.m_Position` e `Block.m_Size.x/y`
   (`CS2M/Sync/StateHashSystems.cs:521`), então uma divergência de `BuildOrder` aparece HOJE como
   "contagem bate, hash não bate" sem tocar `ZoneType.m_Index` nem exigir nenhum "fold" por-máquina —
   **hipótese alternativa e mais barata de testar que a suspeita de `m_Index` da memória**.

4. **`NodeAlignSystem` em si é determinístico DADO o mesmo conjunto de arestas conectadas.**
   Verifiquei a matemática (`NodeAlignSystem.cs:83-217`): a nova `Node.m_Position` é
   `position + Σ(edge_endpoint - position)/n`, que se reduz algebricamente a `average(edge_endpoints)` —
   **não depende da posição anterior nem da ordem de iteração** (soma comutativa). A rotação usa
   `angleBuffer.Sort()` (`NodeAlignSystem.cs:195`) — ordenação determinística de floats, também
   independente de ordem de chegada do `EdgeIterator`. **Conclusão**: o alinhamento em si não é a fonte
   de drift enquanto o CONJUNTO de arestas conectadas for idêntico quando o sistema roda — o que o design
   do AtomicBatch (criar TODAS as edges antes de marcar o nó de fronteira `Updated` UMA vez,
   `NetBatchApplySystem.cs:279-282`) garante para um ÚNICO batch. O risco realista é entre DOIS batches
   de jogadores DIFERENTES tocando o MESMO nó de fronteira em ordens de aplicação diferentes host/cliente
   — nesse caso o align roda duas vezes em ordens diferentes, mas como o resultado final só depende do
   conjunto (não da ordem), o estado FINAL converge; o que diverge é o `BuildOrder` (item 2/3), não a
   posição do nó.

5. **`Upgraded` (novo edge) não é aplicado no MESMO frame no caminho AtomicBatch — janela de perda real.**
   `NetBatchApplySystem.EmitEdgeCourse` explicitamente NÃO inclui `Upgraded` na `CreationDefinition`
   (comentário `NetBatchApplySystem.cs:421-426`); em vez disso enfileira em `RemoteNetUpgradeQueue`
   (`NetBatchApplySystem.cs:431-443`), consumida por `NetEditApplySystem.OnUpdate`
   (`CS2M/Sync/NetEditApplySystem.cs:64-67`, `while (RemoteNetUpgradeQueue.TryDequeue(...))`). **Os dois
   sistemas estão registrados no MESMO slot de fase** — `updateSystem.UpdateBefore<NetBatchApplySystem>
   (Modification1)` (`CS2M/Mod.cs:194`) e `updateSystem.UpdateBefore<NetEditApplySystem>(Modification1)`
   (`CS2M/Mod.cs:217`) — sem NENHUM `UpdateBefore/UpdateAfter` explícito ENTRE os dois. Se
   `NetEditApplySystem` rodar DEPOIS de `NetBatchApplySystem` no mesmo frame (ordem não fixada pelas
   citações que encontrei), ele drena o comando de upgrade ANTES de `GenerateEdgesSystem` (o sistema
   vanilla real de Modification1/2) ter criado a edge — `FindEdgeById`/`FindEdge` falham
   (`NetEditApplySystem.cs:81-85`), o comando é logado como `SKIP noMatch` e **descartado sem re-fila**
   (`RemoteNetUpgradeQueue` é um `ConcurrentQueue` puro, `TryDequeue`/`Enqueue` sem caminho de devolução —
   `CS2M/Sync/RemoteNetUpgradeQueue.cs:9-20`). Resultado possível: uma rua construída JÁ com
   calçada/árvore/quay/muro-anti-ruído chega ao receptor como edge NUA, sem re-tentativa. NÃO CONSEGUI
   VERIFICAR a ordem real de execução entre os dois sistemas em runtime (ver seção 7) — é um risco de
   design, não um bug comprovado ao vivo, mas a lacuna de proteção (sem retry) é real e verificável no
   código de qualquer forma.

## 5. O que o CS2M faz hoje

- **Caminho legado** (`CS2M_ATOMIC` desligado, default): `NetDetectorSystem` → `NetPlaceCommand` →
  `NetPlaceApplySystem` (`CS2M/Sync/NetDetectorSystem.cs`, `CS2M/Sync/NetPlaceApplySystem.cs`). Envia
  Bezier bruto + identidade de prefab + elevação + `RandomSeed` + posições/ids de nó (`HasNodes`,
  `StartNodeId`/`EndNodeId`) para reduzir a fusão por proximidade do receptor
  (`NetPlaceCommand.cs:43-62`).
- **Deleção/edição por identidade**: `NetEditDetectorSystem`/`NetEditApplySystem` resolvem edge/nó por
  `CS2M_NodeSyncId` primeiro, caem para posição (~3–10 m) só em conteúdo legado/save
  (`NetEditApplySystem.cs:273-347`).
- **Upgrade retroativo**: `NetUpgradeDetectorSystem` faz DIFF contínuo de `Upgraded.m_Flags` em TODA edge
  e TODO nó a cada frame (baseline silencioso no primeiro passe,
  `NetUpgradeDetectorSystem.cs:65-79`), envia por posição/id (`NetUpgradeDetectorSystem.cs:104-116,
  136-146`). Cobre junção (farol/pare/rotatória) via `Upgraded` no NÓ, algo que o detector de edge não
  via (`NetUpgradeDetectorSystem.cs:38-40`).
- **AtomicBatch** (`CS2M_ATOMIC=1`, HÍBRIDO): `NetBatchCaptureSystem` (builder) junta TODO um apply do
  tool local — nós novos, edges novas, edges removidas pelo split, ids de nós de fronteira — num
  `NetBatchCommand` só (`NetBatchCaptureSystem.cs:126-382`). `NetBatchApplySystem` (receptor) cria nós
  DIRETO do archetype do prefab (`NetData.m_NodeArchetype`, `NetBatchApplySystem.cs:299-349`) e emite
  UMA `CreationDefinition(Permanent)+NetCourse` por edge nova (`NetBatchApplySystem.cs:357-450`) — o
  mesmo caminho vanilla do `NetPlaceApplySystem`, garantindo que pavimento/terreno/composição/lane/bloco
  de zona se derivem de verdade (não um "casco oco"). Resolve fronteira por `CS2M_NodeSyncId` com
  planejamento atômico (miss estaciona o BATCH INTEIRO, nunca aplica parcial —
  `NetBatchApplySystem.cs:162-297`). Eco por componente (`CS2M_RemotePlaced`/`CS2M_RemoteDeleted`), não
  por hash+TTL. Especificação completa e decisões de freeze em `docs/atomicbatch-spec.md`.
- **Contrato**: `NetPlaceCommand`, `NetBatchCommand`, `NetToolReplayCommand`, `NetUpgradeCommand`,
  `NetDeleteCommand` — todos `SyncClass.WorldContract` (`CS2M/Sync/SyncContract.cs:49-53`).

## 6. GAPS e recomendação

1. **`Net.BuildOrder` não é aplicado no receptor do AtomicBatch** (`NetBatchApplySystem.cs:417-419`) —
   é capturado e transmitido (`NetBatchCommand.EdgeBuildOrderStart/End`) mas descartado no apply. Isso é o
   candidato mais barato de testar para o gap aberto "zonas divergem" da memória do projeto: hashear
   `Zones.BuildOrder.m_Order` (não só posição/tamanho do bloco) nos dois lados logo após um build
   concorrente e comparar. **Checklist de correção**: (a) no apply, `SetComponent(edge, new
   BuildOrder{m_Start=cmd.EdgeBuildOrderStart[i], m_End=cmd.EdgeBuildOrderEnd[i]})` IMEDIATAMENTE após
   `GenerateEdgesSystem` materializar a edge (precisa herdar `definitionData.m_Original` OU sobrescrever
   pós-frame — GenerateEdges já escreve seu PRÓPRIO valor em `GenerateEdgesSystem.cs:1556-1559`, então o
   mod precisaria overrid-lo no frame seguinte, current-value guard); (b) alternativa mais barata: nada
   de por-edge — sincronizar só o CONTADOR GLOBAL (`GenerateEdgesSystem.GetBuildOrder()`,
   `GenerateEdgesSystem.cs:2129`) via um comando leve de "reserva de faixa" antes de cada AtomicBatch,
   análogo ao que `ResetBuildOrderSystem` já faz determinística e identicamente nos dois PCs NO JOIN
   (`ResetBuildOrderSystem.cs:62-149`, roda `UpdateAfter(Deserialize)` — SystemOrder.cs:853) — mas diverge
   depois porque cada PC segue incrementando sozinho.
2. **Upgrade de edge/nó recém-criado no AtomicBatch não tem retry** (`RemoteNetUpgradeQueue.cs:9-20` +
   `NetEditApplySystem.cs:64-67`) — se a ordem intra-frame entre `NetBatchApplySystem` e
   `NetEditApplySystem` (ambos `UpdateBefore(Modification1)`, `Mod.cs:194,217`, sem ordem explícita entre
   si) colocar o segundo ANTES do edge existir, o comando é perdido silenciosamente (log `SKIP noMatch`,
   sem devolução à fila). **Checklist**: (a) medir/fixar a ordem real (log de timestamp/frame em ambos
   OnUpdate durante o 2-sim) — se `NetEditApplySystem` já roda depois por acaso, documentar e travar com
   `updateSystem.UpdateAfter<NetEditApplySystem, NetBatchApplySystem>(Modification1)` explicitamente (lei
   do projeto diz NUNCA usar dois-tipos `UpdateBefore<A,B>`, mas o par certo aqui é `UpdateAfter<A,B>`
   para o slot já existente — conferir a lei exata antes); (b) OU simplesmente fazer
   `NetEditApplySystem.ApplyUpgrade` reenfileirar (não descartar) quando `FindEdgeById`/`FindEdge`
   falharem, com um contador de idade (mesmo padrão do `_parked`/`_parkAge` de `NetBatchApplySystem.cs:
   109-137`) — mais robusto que depender de ordem de sistema.
3. **Nó novo com `Upgraded` de fábrica não é capturado pelo AtomicBatch.** `NetBatchCommand` não tem
   campo de Upgraded para NÓS (só `NodeHasStandalone`/`NodeHasElevation`/`NodeSeeds`,
   `NetBatchCommand.cs:42-48`) e `NetBatchCaptureSystem` não lê `Upgraded` na malha de nós novos
   (`NetBatchCaptureSystem.cs:271-311` não tem `EntityManager.HasComponent<Upgraded>(n)`). Na prática
   isso é coberto de lado por `NetUpgradeDetectorSystem` (diff contínuo, independente da flag
   `CS2M_ATOMIC`, seção 5) — mas existe uma janela de 1+ frame em que a junção nova mostra os flags
   PADRÃO do jogo (não os que o `Composition`/vizinhança local determinaria) até o diff alcançar. Não
   verifiquei se isso é visualmente perceptível ao vivo (seção 7).
4. **Sub-redes de prédio (`Owner`) são excluídas da captura por design** (`NetBatchCaptureSystem.cs:81`,
   comentário *"building sub-nets regenerate deterministically"*) — não verifiquei essa premissa dentro
   do escopo desta mecânica (pertence ao dossiê de Objetos/Prédios); se a garagem/entrada de veículo
   escolhe a rua mais próxima por busca local com desempate não-determinístico (ordem de iteração de
   query, `Entity.Index`), o prédio poderia grudar em ruas diferentes nos dois PCs sem que NENHUM detector
   de rede veja isso (não é um `Applied&Created&!Owner`). Marcado como GAP DE FRONTEIRA — para o dossiê
   de Objetos investigar.
5. **`NodeReductionSystem` (automático, Mod2B) mescla `BuildOrder` ao colapsar nós** (linhas 649-661) —
   mais um propagador do gap 1, fora do controle direto do mod (é um sistema vanilla que roda sempre que
   a condição de colapso bate, não um "comando" que o CS2M possa interceptar hoje).

## 7. NÃO VERIFICADO

- Mecanismo exato pelo qual `Temp` vira `Applied` no traçado interativo (qual sistema, `ToolOutputSystem`/
  `ToolOutputBarrier`?) — usei a descrição já validada em `GAME_SYSTEMS.md:107-113` sem reler
  `ToolOutputSystem.cs` linha a linha nesta sessão.
- **Ordem de execução real, dentro do mesmo frame, entre `NetBatchApplySystem` e `NetEditApplySystem`**
  (ambos `UpdateBefore(Modification1)` sem relação explícita entre si, `CS2M/Mod.cs:194,217`) — não
  tenho acesso a um log de runtime desta sessão para confirmar qual roda primeiro; o achado 5 da seção 4
  e o gap 2 da seção 6 dependem dessa ordem, então tratei como RISCO, não como bug confirmado ao vivo.
- Se a divergência de `Zones.BuildOrder`/`Block.m_Size` (seção 4, item 3) é DE FATO a causa raiz do
  "zones 594vs594 hash-diff" da memória do projeto — a cadeia de código é sólida e citada, mas não rodei
  o 2-sim para confirmar a correlação empírica nesta sessão.
- Se prédios/sub-redes (`Owner`) de fato "regeneram deterministicamente" como o comentário do capturador
  assume (gap 4) — não abri o pipeline de `AttachSystem`/`SubNetSystem` a fundo; toquei apenas de
  raspão (`decomp/Game/Game/Objects/AttachSystem.cs`, `decomp/Game/Game/Serialization/SubNetSystem.cs`,
  arquivos localizados mas não lidos linha a linha).
- Detalhe fino de `EdgeIterator`/ordem de iteração de `ConnectedEdge` (poderia, em tese, afetar qual edge
  é escolhida em empates de alguma heurística que eu não tenha lido) — cobri o suficiente para confirmar
  que `NodeAlignSystem` não é sensível a isso (soma comutativa + sort determinístico), mas não auditei
  TODO consumidor de `EdgeIterator` no arquivo (`decomp/Game/Game/Net/EdgeIterator.cs` não foi lido).
- Se o `CompositionSelectSystem`/`NetCompositionSystem` têm algum critério de desempate que use índice de
  entidade ou ordem de criação (por-máquina) ao escolher composição entre múltiplas opções válidas — não
  abri esses dois arquivos nesta sessão além do que já estava documentado no `atomicbatch-spec.md`.
