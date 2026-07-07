# CS2M — Co-op (fork com sincronização de gameplay)

Fork do [CitiesSkylinesMultiplayer/CS2M](https://github.com/CitiesSkylinesMultiplayer/CS2M) para
**Cities: Skylines II 1.5.3f1** que adiciona a **sincronização de gameplay** que o mod original nunca
implementou.

O upstream conecta dois mundos, transfere o save inicial e tem chat — mas **depois disso cada jogador
edita a própria cidade sozinho**: nada do que um faz aparece pro outro. Este fork sincroniza as
**ações dos jogadores** de verdade, validado dentro do jogo.

> **v1.0.56.0** — cobertura auditada: as **249 ações de UI** do jogo foram mapeadas, uma a uma, a um
> sync ou confirmadas como emergentes (todo estado *autorado* pelo jogador está coberto; só a simulação
> emergente — cidadãos, veículos, trânsito, chirps — diverge por design e não se sinca). Validado por um
> harness que roda o jogo de verdade e confere o estado do mundo antes/depois — **selftest 88 PASS / 0
> FAIL** — e por **sessões reais de 2 PCs** (host + cliente via VPN), que expuseram e corrigiram os bugs
> que só rede de verdade mostra.
> Veja [COOP_SYNC.md](COOP_SYNC.md) para a matriz de validação e o guia de debug.

---

## Documentação e diagnóstico

| Arquivo | Para quê |
|---|---|
| [COOP_SYNC.md](COOP_SYNC.md) | Arquitetura do MOD: matriz de features, as leis de sync, build/validação. **Ler antes de mexer.** |
| [GAME_SYSTEMS.md](GAME_SYSTEMS.md) | Referência dos sistemas ECS **vanilla** do jogo (componentes/fase/jeito certo) + estudo da fonte do MoveIt + auditoria dos apply-paths. |
| [FIELD_TEST.md](FIELD_TEST.md) | Roteiro do teste de campo dos 3 jogadores: ligar os radares, reproduzir bugs, como reportar com prova. |
| [tools/wiretap-diff/](tools/wiretap-diff/) | Cruza as gravações `CS2M_wiretap_*.jsonl` dos jogadores e aponta o comando perdido (onde o mundo divergiu). |

**Radares de bug de sync (v52):** com `CS2M_WIRETAP=1` o jogo grava todo comando; o
`StateHashSystems` compara os mundos por hash de conteúdo e loga `[Hash] DRIFT categoria` quando
divergem de verdade; o `InvariantCheckSystem` loga `[Invariant] VIOLATION` em anomalia estrutural.
Juntos localizam qualquer desync residual em campo. Detalhes em GAME_SYSTEMS.md §11.

---

## O que sincroniza

| Categoria | Feature | Como é endereçado entre PCs |
|---|---|---|
| **Construção** | Prédios, props, árvores, serviços (Object Tool) | prefab (tipo+nome) + `CS2M_SyncId` |
| | Redes: estradas, trilhos, canos, energia, cercas | prefab + geometria (bézier) |
| | Zoneamento (pintar/despintar) | posição do bloco + nomes de ZonePrefab |
| **Edição** | Bulldoze / deletar objeto | `CS2M_SyncId` |
| | Mover / realocar objeto | `CS2M_SyncId` |
| | Deletar rede (bulldoze) | posição dos nós da ponta |
| | Upgrade de rede (calçada/árvores/etc.) | posição dos nós + `CompositionFlags` |
| **Território** | Distritos (pintar a área) | archetype de `AreaData` + polígono de nós |
| | Fontes de água (nascente/dreno) | `WaterSourceData` + posição |
| | Terraformação (best-effort) | replay do brush via `TerrainSystem.ApplyBrush` |
| | **Compra de tiles do mapa** | centro do tile → `RemoveComponent<Native>` (espelho do vanilla) |
| **Progressão** | Compras da dev tree ("skill tree") | nome do nó → evento `Unlock` + débito de pontos |
| | Extensões de prédios de serviço | prefab + dono (`SyncId`/posição) → `ServiceUpgradeSystem` fia o resto |
| | **Growables (EXPERIMENTAL)** | host-autoritativo: spawns do sim do host sincam; clientes suprimem o próprio `ZoneSpawnSystem` (level-up = delete-por-id + spawn). `CS2M_GROWABLE_SYNC=0` desliga |
| | Delete de objetos nativos | prefab + posição (gate: só com o bulldozer ativo) |
| **Ambiente** | Clima (chuva/nuvens/temperatura) | overrides do `ClimateSystem` (~0,5 Hz) |
| | Relógio/data compartilhados | realinhamento do `TimeData.m_FirstFrame` |
| **Transporte** | **Linhas (criar/re-rotear/deletar/cor/número)** | waypoints completos + paradas por `SyncId`/posição; o receptor constrói dos archetypes e os sistemas do jogo fazem pathing/veículos |
| **Cidade** | Empréstimos | delta reconciliado pelo host |
| | Renomear (prédios, distritos, linhas) | `NameSystem` por identidade |
| | Mover prédios nativos | transform pré-move capturado do Temp do move tool |
| | Políticas de prédio/distrito ("esvaziar aterro"…) | eventos `Modify` da própria UI, com alvo resolvido |
| | Superfícies/pavimento + delete de áreas | polígono + prefab/centro |
| | Campos de fazenda/minas (padrão + edição) | réplica do `CreateAreas` + `AreaEditCommand` |
| **Economia/cidade** | Dinheiro (caixa da cidade) | valor autoritativo do host (~1 Hz) |
| | Custo de construção remota | **o host debita** o custo do que o cliente constrói |
| | Impostos (todas as alíquotas) | array de `TaxSystem.GetTaxRates()` |
| | Orçamento de serviços (sliders) | prefab do serviço + porcentagem |
| | Políticas da cidade | prefab da política + flags/ajuste |
| | Milestones / XP / desbloqueios | XP autoritativo do host |
| **Sessão** | Cursor + nome de cada jogador | overlay (círculo) + label na UI |
| | Velocidade da simulação | autoritativa do host, reforçada por frame |
| | Pause-on-join (+ aviso no chat) | estado de "entrando", reforçado por frame |
| | `/resync` no chat | host re-transmite o mundo inteiro sob demanda |

**3+ jogadores** são suportados desde a v43: o host retransmite os comandos de cada cliente aos
demais (topologia estrela) e carimba a identidade de cada conexão. Um **detector de divergência**
compara contagens do mundo a cada 10 s e sugere `/resync` no chat quando algo escapa.

**Lacuna conhecida** (ver seção *Limites*): a **simulação emergente por-PC** (cidadãos, tráfego,
economia por tick) diverge por design entre PCs — o jogo não é determinístico entre máquinas. Mitigada
pelos growables host-autoritativos + `/resync`.

---

## Como funciona (arquitetura)

O jogo é **Unity DOTS/ECS**. Cada mudança do mundo é uma entidade com componentes; cada sistema é um
`GameSystemBase` que roda numa fase do frame (`SystemUpdatePhase.Modification1..5`, `ModificationEnd`,
`Rendering`, `UIUpdate`).

A sincronização segue **um padrão único** para (quase) toda feature:

```
  [ jogador A faz uma ação ]
            │
   Detector System  (roda em Modification…End; lê um "sinal transitório" ou faz diff de snapshot)
            │  monta um comando POCO plano (só primitivos)
            ▼
   Command.SendToAll  ──►  MessagePack (attributeless)  ──►  LiteNetLib  ──►  rede
                                                                               │
   CommandHandler<T>  (auto-descoberto)  ◄────────────────────────────────────┘
            │  enfileira numa fila thread-safe
            ▼
   Apply System  (drena a fila no main thread e altera o ECS, na fase certa do frame)
            │
   [ a mesma coisa aparece no mundo do jogador B ]
```

### As 4 peças

1. **Comando** — um POCO plano herdando `CommandBase`, **só com primitivos** (`float`, `int`, `string`,
   `int[]`…). Serializado por MessagePack *attributeless* (sem atributos no código) via o walker
   `BetterGraphOf`. **Nada de `Entity`/índice cruza a rede** — índices de entidade são instáveis entre
   PCs. Identifica-se por **nome de prefab**, **posição no mundo** ou um **id sincronizado** (abaixo).

2. **Detector** — um `GameSystemBase` em `UpdateBefore<T>(ModificationEnd)`. Dois estilos:
   - **por tag transitória**: o jogo marca `Applied` num objeto/edge recém-colocado, `Deleted` num
     bulldoze, `Upgraded` num upgrade. O detector faz uma `EntityQuery` com `RequireForUpdate` — então o
     sistema **nem roda** quando não há nada marcado.
   - **por diff de snapshot**: para valores (impostos, orçamento, políticas, zoneamento) o detector
     compara o estado atual com o último conhecido e **só envia quando muda**.

3. **Handler** — `CommandHandler<T>` (auto-descoberto, `TransactionCmd = false`) que só **enfileira** o
   comando numa fila estática thread-safe (a rede roda em outra thread; o ECS só pode ser tocado no main
   thread).

4. **Apply** — um `GameSystemBase` que drena a fila **no main thread** e aplica a mudança no ECS.
   A **fase importa** (lição da v38): quem *cria coisas* (objetos/redes) roda **antes de
   `Modification1`**, para que `Created`/`Updated` sobrevivam até os consumidores nativos do mesmo
   frame; os demais applies rodam em `Modification5` (política em `Modification3`).

### Identidade entre PCs (o problema difícil)

Como índices de entidade diferem entre máquinas, cada feature usa a chave estável certa:

- **`CS2M_SyncId`** (objetos): o colocador aloca `(nonce_da_sessão << 40) | contador` (nonce aleatório
  por processo, porque `SenderId` é sempre 0 no CS2M), manda no comando, e **os dois PCs carimbam a mesma
  entidade**. Isso permite depois **mover/deletar** o mesmo objeto nos dois lados.
- **Geometria** (redes): edges não têm id sincronizado, então delete/upgrade endereçam a rede pelas
  **posições dos dois nós da ponta** (match order-independent, tolerância ~3 m).
- **Nome de prefab** (zonas, políticas, orçamento): o índice/entidade do prefab difere por PC, mas o
  **nome do asset** é igual — resolve-se `nome → prefab local` no receptor.

### Materialização — dois caminhos, uma regra de ouro

A regra de ouro (descoberta a caro na v38): **os consumidores nativos de rede rodam em
Modification2B–4, e o jogo apaga `Created`/`Updated` no fim do MESMO frame**. Qualquer coisa criada em
Modification5 vira uma "casca" que nenhum sistema nativo vê — foi exatamente o bug da primeira sessão
real de 2 PCs (edge criado, contagem subiu, mas **sem malha, sem composição, sem blocos de zona**).
Por isso os applies de criação rodam **antes de `Modification1`**.

- **Objetos** (prédios/props/árvores): criação direta do archetype pré-compilado
  (`ObjectData.m_Archetype`) + `Transform`/`PrefabRef`/`PseudoRandomSeed` — síncrono, entidade
  conhecida na hora (necessário pra carimbar o `CS2M_SyncId`). Rodando antes de Mod1, o
  `SubObjectSystem` (Mod2B) gera os sub-objetos no mesmo frame.
- **Sub-nets do prédio** (ex.: o caminho invisível de um transformador): **não nascem sozinhas** — o
  apply replica o `BuildingConstructionSystem.CreateNets` do próprio jogo, injetando uma
  `CreationDefinition(Permanent, m_Owner=prédio)` + `NetCourse` por entrada do buffer `SubNet` do
  prefab. Cada PC gera as suas deterministicamente — **sub-nets nunca cruzam a rede**.
- **Redes** (estradas/trilhos/canos/energia/cercas): **injeção de definição vanilla** —
  `CreationDefinition` com `CreationFlags.Permanent` + `NetCourse` + `Updated`, o mesmo caminho
  programático que o jogo usa pra construir prédios spawnados. Com `Permanent` não há `Temp`, e o
  `GenerateNodes/EdgesSystem` (Mod1/2) constrói a rede REAL: ajuste ao terreno, merge/reuso de nós
  (as pontas snapam em nós existentes num raio de 0,5 m — conexão cross-PC), composição, geometria,
  lanes, **blocos de zoneamento** e malha, tudo no mesmo frame. Guard de idempotência: se já existe um
  edge do mesmo prefab ligando os mesmos dois nós, o comando duplicado é ignorado.

### Casos que fogem do padrão

- **Dinheiro / XP / velocidade**: são **autoritativos do host** — o host transmite o valor (~1 Hz e na
  mudança), os clientes convergem. Dinheiro usa **delta-`Add`** (nunca `new PlayerMoney`, pra não zerar
  o modo "unlimited"). O papel de host é derivado do `PlayerType` da camada de rede e espelhado em
  `Command.CurrentRole` na transição (na v37 o `CurrentRole` nunca era atribuído — todos os senders
  host-autoritativos morriam em silêncio; bug achado na primeira sessão real).
- **Economia coerente**: quando o **cliente** constrói, o débito local dele seria sobrescrito pelo sync
  de dinheiro do host — então **o host debita o custo de construção ao aplicar** o comando remoto
  (espelho do `ToolApplySystem` vanilla), e o caixa corrigido se propaga a todos.
- **Pause-on-join / velocidade**: a UI do jogo **reescreve** `selectedSpeed` a qualquer interação
  (espaço, teclas 1/2/3, foco) — um write único perde. Igual ao forced-pause vanilla, o
  `JoinPauseSystem` (e o apply de velocidade no cliente) **reforçam o valor todo frame** na fase
  Rendering (que tica mesmo com a sim pausada).
- **Políticas**: em vez de escrever o buffer na mão, o apply **levanta o mesmo evento que a UI do jogo
  levanta** (`Event`+`Modify`) — e roda em **Modification3**, *antes* do `Game.Policies.ModifiedSystem`
  (Modification4) que consome o evento, pra ser processado no mesmo frame.
- **Impostos / orçamento**: usam a **API pública do próprio jogo** (`TaxSystem.GetTaxRates()` — array
  vivo; `CityServiceBudgetSystem.SetServiceBudget(prefab, %)`).
- **Nome sobre o cursor**: o motor de UI do CS2 (cohtml 1.64) tem CSS parcial — nada de `max-content`
  nem `position:fixed` esticado por offsets. O label vive no slot fullscreen `Game` com
  `position:absolute` + `width/height:100%`, unidades `rem`, e reporta o retângulo renderizado de volta
  ao C# (render-ack) pra ser validável por log.

---

## Performance — por que não dá lag

A decisão de arquitetura que faz tudo caber num orçamento minúsculo:

### 1. Sincroniza **ações**, não estado

O mod **não streama o mundo** a cada frame. Ele manda um comandinho **só quando um jogador faz algo**.
Colocar um prédio ≈ **~50 bytes, uma vez**. Uma cidade parada (ninguém editando) gera **zero tráfego de
gameplay**. Isso é a diferença fundamental para "sincronizar a simulação" (que exigiria megabytes por
segundo ou lockstep determinístico).

### 2. Detectores que **não rodam à toa**

- Os detectores por tag usam `EntityQuery` + `RequireForUpdate`: quando não há nada `Applied`/`Deleted`/
  `Upgraded` no frame, **o `OnUpdate` nem é chamado**.
- Os detectores por diff (impostos, orçamento, políticas, zona, upgrade) comparam contra um **snapshot** e
  **só emitem na mudança real** — sem envio contínuo.

### 3. Senders contínuos são **throttled e pequenos**

O único fluxo realmente contínuo é o **cursor** (~20 Hz, com interpolação no receptor pra parecer suave a
partir de poucos pacotes) e o **dinheiro** (~1 Hz e só quando muda). Ambos são pacotes minúsculos.

### 4. Nada de ECS entre threads

A rede (LiteNetLib) roda em outra thread. O handler **só enfileira** numa fila thread-safe; **quem toca o
ECS é sempre o Apply System, no main thread**. Isso evita corrida de dados e travadas de sincronização.

### 5. Echo guards param loops de feedback

Sem proteção, uma mudança recebida seria **re-detectada e re-enviada** — um loop infinito que entupiria a
rede. Cada feature tem um guard:
- tag `CS2M_RemotePlaced` nas entidades criadas remotamente (o detector as exclui);
- `RemoteNetEcho` (hash de segmento quantizado **só em XZ** — o jogo re-ajusta o Y ao terreno) para
  redes/delete/upgrade;
- `ZoneEcho` (TTL por bloco): depois de aplicar zona remota, o detector **absorve** por alguns frames o
  estado real recalculado pelo jogo (células compartilhadas entre blocos vizinhos) em vez de re-enviar —
  mata o ping-pong visto na primeira sessão real;
- refresh de **snapshot** no apply para impostos/orçamento/políticas (o diff seguinte não acusa
  diferença);
- sub-nets com `Owner` (pertencem a um prédio) são **excluídas do detector de redes** — cada PC gera as
  suas a partir do prefab.

### 6. Criação num frame só, sem retry

Objetos são **um `CreateEntity` + `SetArchetype` síncronos**; redes são **uma definição consumida no
mesmo frame** pelo pipeline nativo. Não há tentativa-e-erro por timing nem sistemas extras varrendo o
mundo todo frame.

### 7. Fase certa

Cada apply roda na fase que casa com o sistema nativo correspondente: **criação antes de
`Modification1`** (o pipeline inteiro — geometria, lanes, blocos, malha — completa no mesmo frame),
política em `Modification3` (antes do `ModifiedSystem`), o resto em `Modification5`. Isso evita
processamento repetido e o custo de "esperar o próximo frame".

**Resumo:** o custo por frame é ~o de algumas `EntityQuery` vazias; o custo de rede é ~o tamanho da ação
que o jogador acabou de fazer. Não há varredura do mundo nem estado periódico.

---

## O diferencial de engenharia: o harness que testa sozinho

O jogo trava múltiplas instâncias na mesma máquina, então o teste clássico de "2 PCs" era impossível
sem um segundo computador. A solução: dois harnesses que dirigem o jogo real.

### Selftest 1-instância (`AutopilotSystem`)

Um sistema **desligado por padrão** (só liga com a env var `CS2M_AUTOPILOT`, então o build normal é
idêntico). No modo `selftest` ele: sobe um servidor local (o que já coloca o jogo em `PLAYING` sem
precisar de cliente), **injeta os mesmos comandos que os detectores emitiriam** direto nas filas de
apply, roda uma matriz de **~40 passos**, e **lê o mundo de volta** pra conferir cada feature — tudo
numa instância só. Cada rodada imprime uma matriz `RESULT <feature>: PASS/FAIL` com a evidência.
Estado atual: **88 PASS / 0 FAIL** em save limpo com todos os gates ligados.

**Validações anti-mentira** (endurecidas depois que a 1ª sessão real desmentiu dois PASS): rede só
passa se a **construção real** aconteceu (composição selecionada + nó conectado + blocos de zona, não
só contagem de entidades); pause é validado pelo **`frameIndex` congelado** — inclusive sob um write
adversário de velocidade no meio (emulando a tecla espaço) — e não lendo de volta o valor que nós
mesmos escrevemos; o papel de host (`CurrentRole`) é conferido após o `StartServer` real; e um cursor
remoto falso ("FakeFriend") atravessa o pipeline inteiro do label até o **render-ack** do motor de UI
(retângulo com `w/h > 0`).

### 2-sim na mesma máquina (host + Sandboxie)

Host normal + cliente rodando dentro do **Sandboxie** (`NoSecurityIsolation=y`) contorna o lock de
instância única do jogo e dá **duas simulações reais no mesmo mundo**, sem precisar de um segundo PC.
Cenas roteirizadas:

- **test=1** — roteiro de ~20 tools over-the-wire (smoke test geral).
- **test=3** (TRIREPRO) — triângulo de rua + splits + zonas + fazenda pelo caminho real do tool.
- **test=5** (client-actions) — o cliente apaga água colocada pelo host, tax concorrente.
- **test=6** — rota/política/devtree/move combinados.

Foi com esse harness que **8 fixes** foram validados ao vivo (não só "compila"): fazenda/área por
identidade de placeholder, overdraw de aresta (aresta não some mais), delete-de-remoto (apagar algo
criado pelo outro jogador propaga), tax concorrente (granular por índice), move de prédio (sub-net/
sub-área acompanham), route-reroute de linha carregada do save (por prefab+número), policy de prédio
(filtro por prefab), e zonas (`ZoneOrderTiebreak` por hash de posição + `ZoneBlockAuthority` cura
divergência).

### `tools/autotest/`

- `run_selftest.ps1` / `run_2sims.ps1` — ambos aceitam `-StartGame <guid>` (o guid é o conteúdo de
  `.SaveGameData.cid` dentro do `.cok`, que é um zip) pra carregar um save específico sem depender do
  ponteiro de last-save.
- `statediff.py` — diff por-entidade (nodes/edges/areas/zones/buildings) entre os dois logs, com
  `CS2M_NODEDUMP=1`; mascara divergência emergente de growable (não é bug).

**Defaults**: os 8 fixes acima vêm **ligados** por padrão (uma env var `=0` desliga cada um, ex.:
`CS2M_OVERDRAWFIX=0`). Ficam **desligados** por padrão, ainda experimentais: `CS2M_NODEHEAL`,
`CS2M_DEVTREEFIX`, `CS2M_ATOMIC`.

**Cuidado ao lançar**: não inicie o `Cities2.exe` com o diretório de trabalho dentro de uma pasta que
contenha DLLs do mod (o Mono sonda o CWD e o registro de mods do jogo quebra com
`NotSupportedException`). O launcher do autotest usa a pasta do jogo como CWD.

O selftest valida a camada de apply + detectores; **as sessões reais de 2 PCs** (host + cliente via
VPN) e o **2-sim local** validam o caminho completo com rede, latência e dois mundos vivos.

---

## Limites (v2 / não sincronizado)

- **Simulação emergente** — população, cidadãos, veículos, tráfego, tick de economia, level-up de
  prédios, felicidade/poluição. A simulação do CS2 **não é determinística** entre máquinas;
  sincronizar isso exigiria lockstep determinístico (o jogo não tem) ou stream de estado autoritativo
  (muita banda). O mod alinha o que **as ações + dinheiro + XP + velocidade** conseguem alinhar — e o
  **`/resync`** reconcilia qualquer divergência acumulada re-transmitindo o mundo do host.
- **Pendentes**: **split** de rede cross-PC (ponta no MEIO de um edge existente — snap em nós
  existentes já funciona); edição de objetos nativos/growables (não têm `CS2M_SyncId`).
- **Terraformação** é best-effort: o delta por frame do brush depende de frame-time, então o replay é
  aproximado; o `/resync` corrige o drift de terreno.

---

## Build

Com as env vars `CSII_*` do toolchain de modding configuradas:

```bash
dotnet build CS2M/CS2M.csproj -c Release \
  -p:AssemblyVersion=1.0.N.0 -p:FileVersion=1.0.N.0 -p:Version=1.0.N.0
# copie CS2M.dll / CS2M.API.dll / CS2M.BaseGame.dll para  …/Mods/CS2M/

# UI (label do cursor, chat, menus) — sai direto em …/Mods/CS2M/CS2M.mjs + .css:
# (o dotnet build NÃO roda isso sozinho — é um passo separado)
cd CS2M.UI && npm run build
```

Pasta final do mod (`…/Mods/CS2M/`): `CS2M.dll`, `CS2M.API.dll`, `CS2M.BaseGame.dll`, `CS2M.mjs`,
`CS2M.css`, `lang/` (traduções).

Rodar o autoteste in-game (uma instância):

```powershell
# via tools/autotest, ou manualmente:
$env:CS2M_AUTOPILOT='selftest'; $env:CS2M_AP_LOG='out\selftest.log'
Start-Process Cities2.exe -ArgumentList '-continuelastsave'
```

Todos os jogadores precisam da **mesma versão** (a precondição do mod bloqueia versões diferentes).

---

## Créditos e licença

Fork de **[CitiesSkylinesMultiplayer/CS2M](https://github.com/CitiesSkylinesMultiplayer/CS2M)** — o
framework de conexão, transferência de save, chat e o command/handler base são do projeto original
([contribuidores](https://github.com/CitiesSkylinesMultiplayer/CS2M/graphs/contributors),
[Discord](https://discord.gg/RjACPhd)). Este fork adiciona a camada de **sincronização de gameplay**
(`CS2M/Sync/*`), o harness de teste (`AutopilotSystem` + `tools/autotest`) e as correções descritas acima.

Dependência de mod: [I18n EveryWhere](https://mods.paradoxplaza.com/mods/75426/Windows).

Licenciado sob **MIT**, igual ao upstream.
