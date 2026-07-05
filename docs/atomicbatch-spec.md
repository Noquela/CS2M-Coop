# SPEC: AtomicBatch — sync de construção por transferência de resultado

> Status: **CONGELADA** (investigação wf_02875473 completa: I1+I2+I3 resolvidas).
> Este arquivo É o brief do agente implementador. Repo baseline: commit `abf5313`.
> Regra do projeto: NENHUM deploy sem o Fable revisar o diff. Build: `dotnet build CS2M/CS2M.csproj -c Release`.

## Por quê (1 parágrafo)
CS2 é não-determinístico cross-machine: `NodeAlignSystem@Mod2B` re-centraliza junções a partir
do conjunto de braços conectados, e o mod aplica 1 net/frame (incremental) → cada PC alinha com
conjuntos diferentes num dado instante → nós divergem <1m→14m → blocos de zona nascem diferentes
→ zona não casa (`DROP noBlock`). Fix de raiz: **transferir o RESULTADO assentado do construtor
e aplicar ATÔMICO** (tudo de um apply num frame só), como um save/load em miniatura. O receptor
nunca re-executa o tool; o derivado (align/composição/geometria/lanes/blocos) re-deriva de
fonte idêntica + conjunto completo = converge.

## Escopo desta implementação (Prova 2 do design — direto no alvo)
- SÓ o pipeline NET (rua/trilho/cano/energia). Objeto/área/zona vêm depois no mesmo mecanismo.
- Gated por `CS2M_ATOMIC=1` (env). Caminho NetPlaceCommand atual permanece o default — DLL segura.
- Builder-autoritativo: quem constrói manda o pacote; host repassa (relay já existente
  serve — comandos já passam pelo host hoje? [VERIFICAR no Networking durante implementação]).

## Arquitetura (5 estágios)
1. **CAPTURA (builder)** — novo `NetBatchCaptureSystem` (substitui NetDetectorSystem sob a flag):
   junta TUDO de um apply do tool local num pacote: edges novas (Applied), nodes novos, edges
   DELETADAS pelo split (originais), e ids dos nós de fronteira (existentes que ganharam braço).
   Espera a geometria ASSENTAR antes de capturar: [INVESTIGAÇÃO I2 — timing exato].
2. **PACOTE** — `NetBatchCommand : CommandBase` com DTOs explícitos (MessagePack, pipeline atual):
   - `BatchNode[]`: NodeSyncId, pos(x,y,z), rot(quat), prefabName, elevation?, seed
   - `BatchEdge[]`: EdgeSyncId?, startNodeId, endNodeId, bezier(4×float3), prefabName,
     upgradedFlags(g,l,r), seed, elevation?
   - `DeletedEdges[]`: par de nodeIds (identidade, não posição)
   - `BoundaryNodeIds[]`: nós PRÉ-existentes referenciados (NUNCA os dados deles — só o id)
   - [INVESTIGAÇÃO I2 — lista final de componentes-fonte por tipo]
3. **TRANSFERÊNCIA** — `Command.SendToAll` normal + handler + fila (`RemoteNetBatchQueue`),
   padrão idêntico aos comandos atuais.
4. **APPLY ATÔMICO (receptor)** — novo `NetBatchApplySystem` @ [INVESTIGAÇÃO I1 — fase]:
   drena UM pacote por frame mas aplica o pacote INTEIRO no mesmo frame:
   a. resolve fronteira: `CS2M_NodeSyncIds.TryResolve(boundaryId)` → entidade local
      (miss → parquear+retry N frames; expira → log ruidoso, NUNCA adivinha por proximidade)
   b. cria nodes novos DIRETO (archetype do prefab): [INVESTIGAÇÃO I1 — receita]
   c. cria edges novas DIRETO ligando (novo|fronteira) por identidade
   d. aplica deletes (por identidade, cascade existente RebuildAfterDelete)
   e. registra CS2M_NodeSyncId de todos os novos + `CS2M_RemotePlaced` (echo guard)
   f. NÃO re-alinhar depois; deixar o pipeline Mod2B-4 derivar sozinho (o ponto todo)
5. **ECO** — receptores não re-detectam: captura pula entidades com `CS2M_RemotePlaced`
   (guard por componente, não por hash geométrico+TTL).

## O que NÃO tocar
- NetPlaceCommand/NetDetectorSystem/NetPlaceApplySystem atuais (caminho default) — intactos.
- NetEditApply (delete/upgrade por id) — continua servindo o caminho velho E o novo (deletes
  do batch reusam ApplyDelete por id).
- Selftest existente (53 passos) — NÃO alterar os testes atuais; ADICIONAR passo novo gated
  na flag se viável.
- NodePinSystem — permanece desligado, não remover (referência histórica).

## Critérios de aceite (nesta ordem)
1. `dotnet build CS2M/CS2M.csproj -c Release` → 0 erros.
2. Flag OFF: selftest completo → MESMOS resultados do baseline (nenhuma regressão; net/net-delete PASS).
3. Flag ON (CS2M_ATOMIC=1): selftest ainda PASS (o selftest injeta NetPlaceCommand legado —
   deve continuar funcionando pois o caminho velho existe; o batch só muda o que o DETECTOR real envia).
4. Log observável: `[Batch] CAPTURED n=<nodes>/<edges>/<dels> boundary=<n>` no builder e
   `[Batch] APPLIED atomic n=...` no receptor (pro 2-sim com Bruno provar de ponta).
5. Diff revisado pelo Fable antes de qualquer deploy.

## [INVESTIGAÇÃO I1 — receita de criação direta] ✅ RESOLVIDO
**Archetypes:** vêm PRÉ-COMPUTADOS do prefab: `EntityManager.GetComponentData<NetData>(netPrefabEntity).m_NodeArchetype / .m_EdgeArchetype` (`Game.Prefabs/NetData.cs:9-11`; montados em `NetPrefab.LateInitialize`, `NetPrefab.cs:31-53`). **Created+Updated já vêm NO archetype** (adicionados incondicionalmente no LateInitialize) → `CreateEntity(archetype)` já sai com as tags; nenhum AddComponent extra. NUNCA montar archetype na mão (cada tipo de prefab — road/track/pipe/powerline — embute componentes diferentes). Curve/Composition/EdgeGeometry/BuildOrder/PseudoRandomSeed/SubLane/SubObject/ConnectedEdge/ConnectedNode/CullingInfo/MeshBatch **já estão no archetype zero-inicializados** — só `SetComponent` os valores (não AddComponent).

**EDGE — campos obrigatórios** (réplica de `GenerateEdgesSystem.GenerateEdge`, ~1259-1683):
1. `SetComponent(PrefabRef{m_Prefab=netPrefabEntity})` [1662]
2. `SetComponent(Edge{m_Start,m_End})` [1663] — entidades locais resolvidas (novas ou fronteira por NodeSyncId)
3. `SetComponent(Curve{m_Bezier=<do pacote>, m_Length=MathUtils.Length(bezier)})` [1339-1350]
4. **CRÍTICO** `SetComponent(Composition{m_Edge=prefabEntity, m_StartNode=prefabEntity, m_EndNode=prefabEntity})` [1303-1306,1454] — sem isso o CompositionSelectSystem@Mod3 não tem entrada e cai num caminho de fallback não-traçado
5. `BuildOrder`: enviar o valor EXATO do construtor (range {start,end} do entity dele) — os dois mundos precisam do MESMO valor pra ordenação determinística de lane/bloco (colisão com counter local do receptor = risco aceito v1, anotar)
6. Condicionais SÓ SE presentes no construtor (enviar presença+valor): `Upgraded{flags}` [1473-76], `Elevation` [1465-68], `Road` (se prefab tem RoadData) [1186-93]
7. `ConnectedNode` buffer: deixar vazio (só p/ local-connect de prédio; ReferencesSystem cuida do resto)
8. **NÃO adicionar `Temp`** (caminho Permanent não põe; 1643-46) e **NÃO adicionar `Applied`** (Applied dispararia detectores; nossos receptores usam CS2M_RemotePlaced como guard)

**NODE — campos obrigatórios** (réplica de `GenerateNodesSystem.CreateNodesJob`, ~1387-1520):
1. `SetComponent(PrefabRef{m_Prefab=netPrefabEntity})` [1398]
2. `SetComponent(Node{m_Position, m_Rotation})` [1422] — são os ÚNICOS 2 campos de Node
3. Condicionais espelhados do construtor (enviar presença): `Standalone` (capturar o bool do entity do construtor e replicar — NÃO decidir localmente), `Upgraded`, `Elevation`, `PseudoRandomSeed`

**Fase/agendamento:** sistema novo em `updateSystem.UpdateBefore<T>(SystemUpdatePhase.Modification1)` (mesmo slot do NetPlaceApplySystem atual — padrão provado). Main-thread `EntityManager` direto (GameSystemBase.OnUpdate, sem Burst/ECB — igual os apply systems atuais fazem).

**Pergunta aberta aceita (checar em runtime no selftest):** se `Composition{prefab,prefab,prefab}`+Curve+PrefabRef bastam pro CompositionSelect resolver no caso default sem Upgraded — validar com log `Composition.m_Edge != Entity.Null` pós-frame.

## [INVESTIGAÇÃO I2 — captura: timing + conjunto + componentes-fonte] ✅ RESOLVIDO
**Timing (a melhor notícia): TUDO assenta em UM frame.** ToolSystem (apply do tool via
ToolOutputSystem→ApplyTool→ToolOutputBarrier playback) roda ANTES de ModificationSystem
(Mod1→…→ModEnd) no MESMO frame; NodeAlign@Mod2B e Geometry@Mod4 (fixed-point interno de 2
iterações, `GeometrySystem.cs:3556`) completam antes de ModificationEnd. O detector do mod já
registra `UpdateBefore<...>(ModificationEnd)` → **vê posições de nó JÁ ASSENTADAS no frame do
apply. Captura = mesmo slot do NetDetectorSystem atual, sem espera multi-frame.**
Tags Applied/Created/Updated/Deleted vivem EXATAMENTE 1 frame (CleanUpSystem@Cleanup strippa)
→ o snapshot por-frame do OnUpdate É a fronteira natural do batch.

**Partição limpa e exaustiva (de ApplyNetSystem.cs:370-699, Create/Update/Delete):**
- NEW (nó ou aresta): `Applied & Created & !Deleted` (None: Temp, Owner p/ sub-nets)
- MODIFIED in-place (ex.: nó existente ganhou braço; upgrade em aresta não-splitada):
  `Applied & Updated & !Created & !Deleted`
- REMOVED (bulldoze OU original cortada por split OU substituída): `Deleted & !Applied`
  (válido p/ Node/Edge; NÃO generalizar pra Lane)

**Componentes-FONTE a enviar (confirmado por ISerializable vs IEmptySerializable):**
- Node: `Node{m_Position:float3, m_Rotation:quaternion}`, `PrefabRef`(→nome),
  `Elevation{m_Elevation:float2}` (opcional), `PseudoRandomSeed{m_Seed:ushort}` (opcional)
- Edge: `Edge{m_Start,m_End}` (→ traduzir por NodeSyncId), `Curve.m_Bezier` (m_Length é
  DERIVADO — recomputado de MathUtils.Length no Deserialize; NÃO enviar), `PrefabRef`,
  `Upgraded{CompositionFlags}` (opcional), `Elevation` (opcional), `PseudoRandomSeed`
- DERIVADOS (NUNCA enviar): NodeGeometry/EdgeGeometry/Composition/ConnectedEdge/SubLane/
  SubBlock/lanes — re-derivam do pipeline. EXCEÇÃO anotada: `ConnectedNode` é FONTE (checar
  caso a caso os Connected*).

**GOTCHA CRÍTICO:** `Curve.m_Bezier.a/.d` ≠ `Node.m_Position` pós-align (nenhum sistema
escreve o align de volta no bezier). A posição autoritativa do nó é SEMPRE lida do componente
`Node` via `edge.m_Start/m_End` (o detector atual já faz isso certo — manter).

**GOTCHA DETECTOR:** a query `_appliedEdges` atual casa `Applied` sem exigir `Created` →
dispara também pra upgrades in-place. Na captura nova, usar a partição de 3 vias acima.

**Perguntas abertas (aceitáveis, cobertas por design):** (a) drag contínuo pode gerar 1 apply
por segmento em frames distintos → batches separados por frame, cada um atômico — espelha a
formação incremental do próprio construtor, align converge por curvas idênticas a cada passo;
(b) bezier nunca reconciliado com o nó alinhado → irrelevante: enviamos ambos como estão no
construtor (fonte fiel).

## [INVESTIGAÇÃO I3 — wire/formato] ✅ RESOLVIDO: Opção A (MessagePack DTOs)
- `EntitySerializer` do Colossal É público (Colossal.Core.dll, mod já referencia) MAS é
  maquinário de save de mundo inteiro: serializa estado de TODOS os sistemas a cada chamada,
  bibliotecas de inicialização caras, formato orientado a espaço de entidade consistente,
  zero precedente no repo. NÃO usar pro batch.
- Opção A provada: comandos atuais já enviam `float[]/int[]/string[]` via MessagePack
  (AreaEditCommand, ZonePaintCommand) e o transporte LiteNetLib `ReliableOrdered` fragmenta
  sozinho (sem cap manual no caminho de comandos). → `NetBatchCommand : CommandBase` com
  arrays paralelos de DTOs primitivos (mesmo estilo dos comandos existentes).

## DECISÕES DE FREEZE (Fable, 04/07)
1. **Wire:** Opção A. Arrays paralelos primitivos (sem structs aninhados — seguir o estilo NetPlaceCommand).
2. **Apply:** UpdateBefore(Modification1), main-thread EntityManager, 1 batch por frame, batch INTEIRO no frame.
3. **Espelhar, não decidir:** todo componente condicional (Standalone/Upgraded/Elevation/seed) vai com flag de presença capturada do entity real do construtor. O receptor replica cegamente. Zero heurística.
4. **Boundary node:** só NodeSyncId + posição (p/ log). Resolver por id; miss → parquear batch inteiro e re-tentar ~300 frames; expirar → log `[Batch] DROP boundary-miss` (nunca proximidade).
5. **Boundary node ganhou braço:** depois de criar as edges, marcar o nó de fronteira `Updated` (+BatchesUpdated) — ReferencesSystem re-wira ConnectedEdge e NodeAlign re-alinha com o conjunto novo (curvas idênticas → converge).
6. **Deletes no batch:** pares de NodeSyncId; resolver via FindEdgeById existente (NetEditApplySystem) + cascade RebuildAfterDelete. Antes de marcar Deleted, adicionar CS2M_RemotePlaced no edge (echo-guard de delete por componente).
7. **Echo guard geral:** TODA entidade criada/deletada pelo apply ganha `CS2M_RemotePlaced`; as queries de captura excluem `CS2M_RemotePlaced`. Sem hash geométrico, sem TTL.
8. **Sob a flag CS2M_ATOMIC=1:** NetBatchCaptureSystem ATIVO e NetDetectorSystem+NetEditDetectorSystem (só a parte de delete de edge) INATIVOS (early-return na flag) — sem envio duplo. Flag OFF (default): tudo como hoje, batch dormant. Os APPLY systems dos dois caminhos ficam sempre ativos (aplicam o que chegar).
9. **Zona:** NENHUMA mudança — com nós XZ idênticos, os blocos derivam iguais e o ZonePaint atual passa a casar. (É o teste de sucesso.)
10. **NodeSyncId de nós novos:** o construtor ALOCA (`CS2M_SyncIdSystem.Allocate`) e registra localmente ANTES de enviar; receptor registra os mesmos ids nos entities que criar. Fronteira usa os ids já existentes (Ensure no construtor se faltar).
