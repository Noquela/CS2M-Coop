# GAME_SYSTEMS.md — como o Cities: Skylines II funciona por dentro (para quem sincroniza)

> Referência dos sistemas ECS **vanilla** do CS2 1.5.3f1 que o CS2M-Coop precisa espelhar entre
> máquinas. Cada afirmação aqui foi validada na prática — pelas 58 cenas do selftest passando e por
> correções de bugs de campo reais. Complemento de `COOP_SYNC.md` (que descreve o MOD); este arquivo
> descreve o JOGO. Quando em dúvida sobre um detalhe, decompile a fonte da verdade:
> `ilspycmd -t Game.Net.GenerateNodesSystem "$GAME/Managed/Game.dll"` (nunca `-o`; joga no stdout e
> `grep`). O caminho do `Game.dll` está nas env vars `CSII_MANAGEDPATH`.

---

## 0. O modelo mental: duas simulações determinísticas que precisam concordar

CS2 roda em **Unity DOTS/ECS** (Entities 1.3.10). O mundo é um monte de *entidades* (só um id) com
*componentes* (dados puros, structs) processados por *sistemas* (lógica) em *jobs* paralelos e
Burst-compilados. Não existe "objeto GameObject" com métodos — tudo é dado + sistema.

**A implicação central para co-op:** cada PC roda a sua própria simulação completa. Se os dois
partem do mesmo save e recebem exatamente as mesmas *ações do jogador* na mesma ordem, convergem —
mas só para o estado **autorado** (o que o jogador coloca). O estado **emergente** (cidadãos,
veículos, valores exatos de simulação econômica por tick) diverge por design: é caro demais e
desnecessário sincronizar agente por agente. Por isso o CS2M sincroniza **ações** (colocou rua,
pintou zona, demoliu) e deixa a sim local recriar o resto — com alguns valores macro
host-autoritativos (demanda RCI, dinheiro) espelhados para as barras baterem.

O que DEVE bater entre máquinas (o "superfície sincronizada"): redes, nós, prédios colocados,
zonas pintadas, áreas/distritos, fontes d'água, terreno, dinheiro, demanda, progressão, políticas,
impostos, orçamento, linhas de transporte. O que PODE divergir legitimamente: cidadãos individuais,
veículos em trânsito, posições de agentes, contadores de tick da economia. **O detector de
divergência (`StateHashSystems`) só faz hash da superfície sincronizada** — daí ele conseguir
apontar bug real sem falso-positivo do que é divergente por natureza.

---

## 1. O frame, as fases e a lei da fase

Um frame de simulação roda os sistemas em **fases** fixas (`SystemUpdatePhase`), nesta ordem:

```
PreSimulation → GameSimulation → Modification1 → Modification2 → Modification3
 → Modification4 → Modification4B → Modification5 → ModificationEnd → PreCulling
 → Rendering → PreCulling2 → UIUpdate → UITooltip → ...
```

- **Modification1–5 / ModificationEnd**: onde os sistemas de ferramenta (colocar/editar/deletar)
  produzem e consomem *definições* de mudança. Os consumidores (que transformam definição em
  entidade real) rodam em Mod1–4.
- **CleanupSystem** (fim do frame): apaga entidades marcadas `Deleted` e remove as tags de fluxo
  `Created`/`Updated`/`Applied`/`Temp` das que sobreviveram. **Ou seja: uma tag de fluxo só vive
  um frame.**

### A LEI DA FASE (a mais violada)
Para injetar/remover entidades de forma que os consumidores vejam, **crie/marque ANTES de
Modification1** (na prática: `UpdateBefore<SeuApplySystem>(SystemUpdatePhase.Modification1)`). Se
você criar depois que os consumidores já rodaram, a entidade fica uma casca invisível ou causa
crash nativo atrasado quando o CleanUp/renderer tromba com estado meio-construído.

### Componentes de fluxo (tags que vivem 1 frame)
| Tag | Significado |
|---|---|
| `Created` | nasceu neste frame |
| `Updated` | mudou neste frame — reprocessar |
| `Applied` | uma definição de ferramenta foi confirmada (vira real) |
| `Deleted` | morre no fim do frame (o CleanUp coleta) |
| `Temp` (`Game.Tools`) | preview da ferramenta (o "fantasma" enquanto arrasta) — nunca é estado real |
| `Overridden` | substituída por outra |

**Armadilha do double-register:** `updateSystem.UpdateBefore<A,B>(phase)` (2 tipos genéricos)
registra o sistema A uma **segunda vez** → ele roda 2×/frame → se A guarda estado por-frame
(definições pendentes), a segunda passada destrói tudo antes dos consumidores. Use sempre a forma
de 1 tipo com a fase.

---

## 2. Identidade de prefab entre máquinas

`PrefabRef.m_Prefab` é uma **Entity** cujo `.Index` **NÃO é garantido igual entre PCs** (depende da
ordem de criação dos prefabs no boot). Portanto **nunca** mande `prefab.Index` pelo fio nem use ele
como chave cross-machine. Identidades estáveis:
- **Nome do prefab** (`PrefabBase.name` via `PrefabSystem.TryGetPrefab`) — estável entre máquinas
  na mesma versão + mesmos mods. É o que o sync usa para dizer "qual prefab".
- **Posição de mundo** (coordenadas float3) — idêntica entre máquinas. Base do `StateHash` (hash de
  posição não depende de índice de prefab).
- **`ZoneType.m_Index`** — usado pelo próprio sync de zona como valor de fio, então é tratado como
  estável (ver §5).
- **`CS2M_SyncId`** (do mod) — nonce<<40|counter, para objetos que o mod colocou.

---

## 3. Redes (ruas, canos, energia) — `Game.Net`

O sistema mais complexo e onde mais deu bug. Componentes-chave:

| Componente | Papel |
|---|---|
| `Edge` | um **segmento** de rede; `m_Start`/`m_End` são as **Entity dos nós** das pontas |
| `Node` | um **nó** (junção/ponta); `m_Position` (float3), `m_Rotation` |
| `Curve` | a geometria: `m_Bezier` (Bezier4x3 — 4 pontos de controle `a,b,c,d`; `a`=início `d`=fim) |
| `PrefabRef` | qual prefab de rede (Small Road etc.) |
| `Composition`/`EdgeGeometry` | faixas, calçadas, meio-fio (derivado do prefab) |
| `ConnectedEdge` (buffer no nó) | lista de edges que tocam aquele nó |
| `SubNet` (buffer) | sub-redes de um prédio (pátio, faixas internas) — têm `Owner`, são derivadas |

### Como uma rua nasce (o pipeline real da ferramenta)
1. `NetToolSystem` cria uma **definição** enquanto o jogador arrasta: entidade com
   `CreationDefinition` + `NetCourse` (o traçado) + `Temp` (preview).
2. Ao soltar, a definição perde `Temp` e ganha `Applied`.
3. `GenerateNodesSystem` → `GenerateEdgesSystem` transformam a definição em `Node`s + `Edge`s reais,
   costurando conexões, dividindo edges que o traçado cruza, fundindo nós coincidentes.
4. `CompositionSystem` e cia. constroem a geometria (faixas, meio-fio).

### O truque da "definição permanente" (como o MOD cria rua sem a ferramenta)
O receptor não tem o arrasto do jogador. Ele injeta:
`CreationDefinition { m_Flags = CreationFlags.Permanent }` + buffer `NetCourse` (ou `Node`) +
`Updated`, tudo ANTES de Modification1. O `GenerateEdgesSystem` pega isso e constrói o segmento real;
a definição é destruída no frame seguinte.

### ⚠️ A limitação que custou o bug das junções T/X (lei 16)
O pipeline **Permanent retorna cedo** em `GenerateNodesSystem` para `m_IsPermanent` — ele **NÃO**
conecta a edges existentes nem divide um edge que a rua nova cruza no meio. Só o fluxo `Temp` da
ferramenta real faz splitting/fusão. **Consequência:** ao sincronizar uma rua que forma junção T ou
cruzamento X com rua de outro jogador, o RECEPTOR precisa dividir manualmente o edge alvo no ponto
de junção (criar nó, quebrar em 2 pedaços Permanent) e fatiar a rua nova em cada cruzamento. É
exatamente o que `NetPlaceApplySystem.SnapOrSplitAt/SplitTargetEdge/FindMidSpanCrossings` faz.

### ⚠️ Um net por frame (lei 17)
Segmentos do mesmo lote precisam enxergar uns aos outros como edges **reais** antes de cortar — não
dá pra dividir uma definição ainda pendente. Por isso o apply drena **um** net por frame, não um
while-loop. Drenar tudo de uma vez → junção nasce com `connectedEdges=1` (não costurou).

### Deletar rede
Marque o `Edge` (e o `Node` que ficar órfão) com `Deleted`. **Sub-redes de prédio têm `Owner`** —
elas cascateiam sozinhas quando o prédio morre; se você deletar uma sub-rede explicitamente nos dois
PCs, uma delas re-emite delete fantasma. Regra: detectores de delete de rede **excluem `Owner`**.

---

## 4. Objetos e prédios — `Game.Objects` / `Game.Buildings`

| Componente | Papel |
|---|---|
| `Game.Objects.Transform` | `m_Position` (float3) + `m_Rotation` (quaternion) de qualquer objeto |
| `Game.Buildings.Building` | marca um prédio; liga a estrada de acesso, lote |
| `Game.Objects.Attached` | `m_Parent` — objeto grudado num edge/outro (paradas, placas roadside) |
| `Owner` (`Game.Common`) | `m_Owner` — sub-objeto derivado de um pai (luz, arbusto, extensão) |
| `PrefabRef` | qual prefab |
| `Game.Prefabs.ServiceUpgradeData` / `BuildingExtensionData` | marca um prefab como upgrade/extensão real |
| `Game.Buildings.InstalledUpgrade` (buffer) | upgrades instalados num prédio |

### ⚠️ Sub-objetos derivados NÃO se sincronizam (lei 13/14)
Quando um prédio nasce, os dois PCs geram sozinhos os sub-objetos dele (luzes, arbustos, placeholder
de fazenda, faixas do pátio). Se você sincronizar cada sub-objeto `Applied` com `Owner`, eles
**duplicam**. Regra: só sincronize como "extensão" um objeto cujo **prefab** tenha
`ServiceUpgradeData` ou `BuildingExtensionData`; o resto os dois PCs recriam.

### ⚠️ Attach roadside compartilha flag com growable
A flag `RoadSide` (de `PlacementFlags`) aparece tanto em paradas/placas quanto em prédios que
crescem à beira da estrada. Fazer attach por essa flag grudou 30 prédios em ruas. Regra: só faz
attach com **hint explícito** do detector (`OwnerX/Z≠0`) e checando `TransportStopData` — nunca em
spawn de growable, nunca em `Source==1`.

### Demolição limpa (lei da cascata)
Deletar o prédio deve marcar `Deleted` **no prédio + todos os filhos `Owner` recursivos** — senão
fica pedaço no chão (`CascadeDeleteUtil.DeleteWithChildren`). Mas cuidado: a sim do host demolindo
abandonados sozinha NÃO deve re-emitir deletes pela rede (senão rasga rua/cano real no cliente).

---

## 5. Zonas — `Game.Zones`

Zoneamento é **por célula**, não por área contínua.

| Componente | Papel |
|---|---|
| `Block` | um bloco de células ao longo de uma frente de rua; `m_Position` (float3), `m_Size` (int2), `m_Direction` |
| `Cell` (buffer no Block) | cada célula: `m_State` (flags — ocupada, etc.) e `m_Zone` (`ZoneType`) |
| `ZoneType` | `m_Index` (ushort) — o tipo de zona (residencial baixa, comércio…) |
| `ValidArea` | região válida do bloco |

**Blocos existem ao longo de toda rua** (o `ZoneSystem`/`BlockSystem` os cria automaticamente na
malha viária), independente de pintura. **Pintar** = setar `m_Zone` das células dentro dos blocos.
Por isso o `StateHash` de zona faz hash de `Cell.m_Zone.m_Index` por célula — a contagem de blocos
sozinha não pega divergência de pintura. `m_Index` é o valor que o próprio sync de zona manda pelo
fio (`ZoneDetectorSystem` lê `cells[i].m_Zone.m_Index`), então é cross-machine estável.

Growables nascem das células zonadas pela sim LOCAL seguindo a demanda — por isso a demanda precisa
ser host-autoritativa (§7), senão cada PC cresce prédio diferente na mesma zona.

---

## 6. Áreas — `Game.Areas`

Áreas são **polígonos** (distritos, lotes, superfícies de tinta, áreas de trabalho/extração).

| Componente | Papel |
|---|---|
| `Game.Areas.Area` | marca uma área |
| `Game.Areas.Node` (buffer) | os vértices do polígono (**cuidado: colide com `Game.Net.Node`** — qualifique!) |
| `Game.Areas.Geometry` | `m_CenterPosition` (float3), `m_Bounds` — derivado dos nós |
| `Game.Areas.District` | um distrito |
| `Game.Areas.Lot` | lote de prédio |

### ⚠️ Editar área marca `Updated`, não `Applied`
Arrastar um vértice de área não gera `Applied` — só `Updated`. Um detector que espera `Applied`
nunca vê a edição (foi o bug do "só aparece a área depois do resync"). Solução: varrer as áreas por
**diff de hash de polígono** (~1Hz), comparando o hash dos nós arredondados com um snapshot.

---

## 7. Demanda RCI — `Game.Simulation`

| Sistema | Expõe |
|---|---|
| `ResidentialDemandSystem` | `householdDemand` (int), `buildingDemand` (int3 low/med/high) |
| `CommercialDemandSystem` | `companyDemand`, `buildingDemand` |
| `IndustrialDemandSystem` | `industrialCompanyDemand`, `industrialBuildingDemand`, `storage*`, `office*` |

A demanda deriva da simulação de cidadãos LOCAL → diverge entre PCs ("eu tinha residencial baixa e
ele não"). Como o crescimento de growable já segue a demanda do host, a solução é **host-autoritativa**:
o host lê os valores públicos e transmite ~0.5Hz; o cliente **desliga** os 3 sistemas
(`.Enabled=false`) e espelha os números nos campos privados `m_Last*` via reflection cacheada (o jogo
só expõe getters read-only). É o padrão geral "suprimir sim divergente + espelhar via reflection".

---

## 8. Cidade, dinheiro, progressão — `Game.City` / `Game.Simulation`

| Onde | O quê |
|---|---|
| `CitySystem.City` (`Game.Simulation`) | a Entity da cidade (raiz de money/xp/pop) |
| `Game.City.PlayerMoney` (na City) | `money` (int), `m_Unlimited` (bool) |
| `XP` (na City) | `m_XP` |
| `Population` (na City) | `m_Population` |
| `CityServiceBudgetSystem` | `GetServiceBudget(prefabEnt)` / `SetServiceBudget` — sliders de verba |

Dinheiro muda todo tick pela economia (receita/despesa) — se a economia roda independente, diverge.
Por isso é host-autoritativo (`MoneySyncSenderSystem`/`MoneySyncApplySystem`). O `StateHash` compara
money com folga (a economia tem timing levemente diferente).

---

## 9. Água e terreno — `Game.Simulation`

| Componente/Sistema | O quê |
|---|---|
| `WaterSourceData` | fonte d'água (nascente, mar, esgoto); tem posição + vazão/altura |
| `TerrainSystem` | heightmap; `TerrainUtils.SampleHeight` lê a altura local num ponto |

**Y da água precisa ser ancorado ao terreno LOCAL** (`SampleHeight`) — mandar o Y do host cru fez a
"água do nada" flutuar. Terreno: aplicar com cap de strokes/frame + descartar backlog, senão a
"torre de terreno" acumula quando o jogo despausa.

---

## 10. Mapa das ferramentas → o que sincronizar

| Ação do jogador | Componentes tocados | Detector do mod |
|---|---|---|
| Colocar rua/cano | `Edge`,`Node`,`Curve`,`CreationDefinition` | `NetDetectorSystem` |
| Editar/upgrade rede | `Edge`+`Updated`, `Composition` | `NetEditDetectorSystem`,`NetUpgradeDetectorSystem` |
| Deletar rede | `Edge`+`Deleted` (excl. `Owner`) | `NetEditDetectorSystem` |
| Colocar prédio/serviço | `Building`,`Transform`,`PrefabRef` | `PlacementDetectorSystem` |
| Mover objeto | `Transform`+`Updated` | `MoveDetectorSystem` |
| Demolir | `Deleted` + cascata `Owner` | `DeleteDetectorSystem` |
| Pintar zona | `Block`/`Cell.m_Zone` | `ZoneDetectorSystem` |
| Distrito/área | `Area`,`Node`,`Geometry` | `DistrictDetectorSystem`,`AreaEditSystems` |
| Água | `WaterSourceData` | `WaterDetectorSystem` |
| Terreno | heightmap | `TerrainDetectorSystem` |
| Impostos/verba/política | sistemas de `Game.City` | `Tax/Budget/PolicyDetectorSystem` |
| Linha de transporte | `Route`,`RouteWaypoint` | `RouteSyncSystems` |

---

## 11. Como validar que os dois mundos concordam (o "pega 100% dos bugs")

Duas ferramentas, ambas no v52:
1. **`StateHashSystems`** — o host transmite um fingerprint por posição da superfície sincronizada a
   cada ~10s; o cliente compara e só acusa `[Hash] DRIFT` quando um item fica **assentado E
   divergente** (parou de mudar dos dois lados e ainda discorda — elimina falso-positivo de comando
   em trânsito). Categoria no log diz na hora se foi rua/zona/prédio/área.
2. **Wire-tap** (`CS2M_WIRETAP=1`) — grava TODO comando (entrada e saída) num `.jsonl` na pasta
   LocalLow do jogo, com seq/tempo/direção/tipo/campos. Para reproduzir um bug: ligue nos dois PCs,
   reproduza, e faça diff dos dois arquivos no timestamp do bug — mostra exatamente onde os fluxos de
   comando divergiram.

Complementos já existentes: `InvariantCheckSystem` (vigia estrutural: edges duplicadas, órfãos,
attach com pai morto) e o `/validate` no chat.

---

## 12. Para aprofundar (as melhores "documentações")

O CS2 não tem doc oficial de ECS decente. As fontes reais, em ordem de valor:
1. **Decompilação do `Game.dll`** — a fonte da verdade. `ilspycmd -t <Tipo> "$GAME/Managed/Game.dll"`
   (nunca `-o`; stdout + grep). Tipos-chave: `Game.Net.GenerateNodesSystem`,
   `Game.Net.NetToolSystem`, `Game.Zones.BlockSystem`, `Game.Areas.*`, `Game.Simulation.*DemandSystem`.
2. **Mods open-source maduros** (o padrão testado por quem já sofreu):
   - **MoveIt (CS2)** / **Traffic** — recriam/movem redes preservando conexões (exatamente a dor de
     junções/split). Estudar como eles disparam a reconstrução de edges.
   - **Anarchy / Tree Controller / Water Features (yenyang)** — supressão de sistemas vanilla,
     serialização de estado de mod.
   - **Road Builder / Extended Road Upgrades** — composições e upgrades de rede.
   - **CSM (CS1)** — padrões de multiplayer (echo, autoridade) testados por anos.
3. **cs2.paradoxwikis.com/Modding** — toolchain e setup (fraca em ECS).

### 12.1 O que aprendi lendo a fonte do MoveIt (Quboid/CS2-MoveIt)

Estudei o código real (`Code/MoveIt/Moveables/MVNode.cs`, `MVSegment.cs`, `Tool/Creation.cs`).
Achados que valem para o CS2M:

- **`Game.Net.ConnectedEdge` (buffer no `Node`)** é o jeito CANÔNICO de enumerar os edges de uma
  junção: cada item tem `m_Edge`; para saber a ponta, `edge.m_Start.Equals(node)` (senão é `m_End`).
  Melhor que varrer todos os edges. Adotar isso no split/junção e no `InvariantCheckSystem` (contar
  grau do nó direto do buffer).
- **MoveIt MOVE entidades existentes, nunca recria.** Ao mover um nó, ele altera a posição + os
  pontos de controle (bezier) dos edges conectados e marca `Updated`+`BatchesUpdated`; a **topologia
  sobrevive porque as refs de Entity (`m_Start`/`m_End`) não mudam**. → **Lição para o caminho de
  MOVER/EDITAR do CS2M:** sincronizar um "mover" como *update do entity existente* (posição + Updated),
  NÃO como delete-e-recria. Recriar perde conexões; o vanilla reconecta sozinho se você só mexe na
  posição e marca Updated.
- **API vanilla `CreateDefinitions(objectPrefab, …, original, controlPoints, …)`** (de
  `ObjectToolBaseSystem`): `original = Entity` → RELOCA um existente; `original = Entity.Null` → cria
  NOVO. `ControlPoint` carrega `m_Position`, `m_Rotation`, `m_Elevation`, `m_ElementIndex`,
  `m_OriginalEntity` (setar aqui atacha extensão). É o caminho oficial para plopar objeto.
- Ao manipular elevação de rede, o componente `Game.Net.Elevation` precisa existir nos DOIS nós
  (`edge.m_Start` e `edge.m_End`) — MoveIt adiciona antes de mexer.

**Conclusão estratégica:** o MoveIt **não** cria rua nova conectando a uma existente (ele só move o
que já existe), então ele **não substitui** o split manual de junção T/X do `NetPlaceApplySystem` —
esse continua sendo o jeito certo para o caso "receptor constrói rua nova que encosta em rua alheia".
MAS o padrão dele (mover = update do existente + `ConnectedEdge` para topologia) é o certo para o
caminho de MOVE/EDIT, e vale conferir se o `MoveDetector/RemoteEditApply` recria em vez de atualizar.

> Próxima ação concreta desta trilha: (1) trocar a enumeração de junção por `ConnectedEdge` no split
> e no InvariantCheck; (2) clonar `Krzychu124/Cities2-Traffic` e ver o tratamento de lanes/conexões.
> Fonte MoveIt clonada em cache: `scratchpad/CS2-MoveIt`.

---

## 13. Auditoria de fidelidade dos apply-paths (verificado contra o padrão vanilla)

Confrontei os caminhos de aplicação do CS2M com os padrões vanilla acima. Resultado:

| Caminho | Padrão esperado | Verificado |
|---|---|---|
| **Mover objeto** (`RemoteEditApplySystem.ApplyMove`) | resolver entity existente + `SetComponentData(Transform)` + `Updated`+`BatchesUpdated`; NÃO recriar | ✅ **correto** — é exatamente o padrão MoveIt (update in-place, topologia preservada). Nós/segmentos de rede não são movíveis sem MoveIt, então não há gap. |
| **Deletar prédio/objeto** | cascata pelos filhos `Owner` (senão "pedaço no chão") | ✅ **correto** — deleções reais usam `CascadeDeleteUtil.DeleteWithChildren`; os `AddComponent<Deleted>` bare são só em entidades sem filhos-Owner (definições rejeitadas recém-criadas, edges no split, eventos de fogo, fontes d'água, rotas, áreas, limpeza de teste). |
| **Criar rua c/ junção T/X** | dividir edge existente no ponto de junção (o pipeline Permanent não conecta) | ✅ **correto** — `NetPlaceApplySystem` faz split manual; selftest `net-tee` = 1 nó / 3 edges. |
| **Sub-objetos derivados** | não re-sincar (os dois PCs geram) | ✅ **correto** — filtra por `ServiceUpgradeData`/`BuildingExtensionData`. |

Conclusão: os apply-paths seguem os padrões vanilla corretos. A incerteza residual de sync está só
na interação das **duas simulações vivas** (que só o teste de 3 jogadores expõe) — e para isso o
`StateHashSystems` (divergência por hash de conteúdo) + wire-tap estão armados para pegar qualquer
resíduo em campo. Ver §11.
