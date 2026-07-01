# CS2M — Co-op (fork com sincronização de gameplay)

Fork do [CitiesSkylinesMultiplayer/CS2M](https://github.com/CitiesSkylinesMultiplayer/CS2M) para
**Cities: Skylines II 1.5.3f1** que adiciona a **sincronização de gameplay** que o mod original nunca
implementou.

O upstream conecta dois mundos, transfere o save inicial e tem chat — mas **depois disso cada jogador
edita a própria cidade sozinho**: nada do que um faz aparece pro outro. Este fork sincroniza as
**ações dos jogadores** de verdade, validado dentro do jogo.

> **14 features validadas no jogo real** (não só "compila"): cada uma foi testada com um harness que roda
> o jogo e confere o estado do mundo antes/depois. Veja [COOP_SYNC.md](COOP_SYNC.md) para a matriz de
> validação e o guia de debug.

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
| **Economia/cidade** | Dinheiro (caixa da cidade) | valor autoritativo do host |
| | Impostos (todas as alíquotas) | array de `TaxSystem.GetTaxRates()` |
| | Orçamento de serviços (sliders) | prefab do serviço + porcentagem |
| | Políticas da cidade | prefab da política + flags/ajuste |
| | Milestones / XP / desbloqueios | XP autoritativo do host |
| **Sessão** | Cursor + nome de cada jogador | overlay + UI |
| | Pause-on-join (+ aviso no chat) | estado de "entrando" |

**Lacunas conhecidas** (ver seção *Limites*): terraformação, linhas de transporte, distritos, e toda a
**simulação emergente** (população, tráfego, cidadãos, economia por tick) — que é um problema
fundamentalmente diferente.

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
   Apply System  (roda em Modification5; drena a fila no main thread e altera o ECS)
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

4. **Apply** — um `GameSystemBase` em `UpdateAt<T>(Modification5)` que drena a fila **no main thread** e
   aplica a mudança no ECS.

### Identidade entre PCs (o problema difícil)

Como índices de entidade diferem entre máquinas, cada feature usa a chave estável certa:

- **`CS2M_SyncId`** (objetos): o colocador aloca `(nonce_da_sessão << 40) | contador` (nonce aleatório
  por processo, porque `SenderId` é sempre 0 no CS2M), manda no comando, e **os dois PCs carimbam a mesma
  entidade**. Isso permite depois **mover/deletar** o mesmo objeto nos dois lados.
- **Geometria** (redes): edges não têm id sincronizado, então delete/upgrade endereçam a rede pelas
  **posições dos dois nós da ponta** (match order-independent, tolerância ~3 m).
- **Nome de prefab** (zonas, políticas, orçamento): o índice/entidade do prefab difere por PC, mas o
  **nome do asset** é igual — resolve-se `nome → prefab local` no receptor.

### Materialização direta ("Option B")

O jeito "oficial" de criar coisas no CS2 é injetar uma **definição** (`CreationDefinition`+`NetCourse`/…)
e deixar o tool do jogo construir. **Isso não funciona quando injetado por fora do fluxo do tool** (o
consumidor vive no `ToolOutputBarrier`, com entidades `Temp` recriadas por frame). Então o apply
**cria a entidade real direto do archetype pré-compilado do prefab**:

- objetos: `ObjectData.m_Archetype`;
- redes: `NetData.m_NodeArchetype` (2 nós) + `NetData.m_EdgeArchetype` (1 edge) com `Curve`/`Edge`/
  `PrefabRef` + `Updated`.

É **síncrono, num frame só, sem timing de barrier** — e os sistemas nativos de geometria/lanes do jogo
constroem o visual a partir dali. (Mesma abordagem que já funcionava pros objetos, estendida pras redes.)

### Casos que fogem do padrão

- **Dinheiro / XP**: são **autoritativos do host** — o host transmite o valor (~1 Hz e na mudança), os
  clientes convergem. Dinheiro usa **delta-`Add`** (nunca `new PlayerMoney`, pra não zerar o modo
  "unlimited").
- **Políticas**: em vez de escrever o buffer na mão, o apply **levanta o mesmo evento que a UI do jogo
  levanta** (`Event`+`Modify`) — e roda em **Modification3**, *antes* do `Game.Policies.ModifiedSystem`
  (Modification4) que consome o evento, pra ser processado no mesmo frame.
- **Impostos / orçamento**: usam a **API pública do próprio jogo** (`TaxSystem.GetTaxRates()` — array
  vivo; `CityServiceBudgetSystem.SetServiceBudget(prefab, %)`).

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
- `RemoteNetEcho` (hash de segmento quantizado) para redes/delete/upgrade;
- refresh de **snapshot** no apply para impostos/orçamento/políticas/zona (o diff seguinte não acusa
  diferença).

### 6. Criação direta = sem retry, sem frame perdido

A materialização por archetype ("Option B") é **um `CreateEntity` + `SetArchetype` síncrono**. Não
depende de barrier/definição/pathfinding, então não há tentativa-e-erro por timing nem sistemas extras
rodando todo frame.

### 7. Fase certa

Cada apply roda na fase que casa com o sistema nativo correspondente (ex.: objetos/redes em
Modification5, política em Modification3 antes do `ModifiedSystem`). Isso evita processamento repetido e
o custo de "esperar o próximo frame".

**Resumo:** o custo por frame é ~o de algumas `EntityQuery` vazias; o custo de rede é ~o tamanho da ação
que o jogador acabou de fazer. Não há varredura do mundo nem estado periódico.

---

## O diferencial de engenharia: o harness que testa sozinho

O crack RUNE mata a segunda instância do jogo, então o teste clássico de "2 PCs" era impossível na mesma
máquina. A solução:

- **`AutopilotSystem`** — um sistema **desligado por padrão** (só liga com a env var `CS2M_AUTOPILOT`,
  então o build normal é idêntico). No modo `selftest` ele: sobe um servidor local (o que já coloca o
  jogo em `PLAYING` sem precisar de cliente), **injeta os mesmos comandos que os detectores emitiriam**
  direto nas filas de apply, e **lê o mundo de volta** pra conferir cada feature — tudo numa instância só.
- **`tools/autotest/`** — o launcher e o roteiro. Cada rodada (~2 min) imprime uma matriz
  `RESULT <feature>: PASS/FAIL` com a evidência (ex.: `edges 482→483`, `adj 10→27`).

É isso que permitiu afirmar **"14/14 validado no jogo"** em vez de só "compila".

---

## Limites (v2 / não sincronizado)

- **Simulação emergente** — população, cidadãos, veículos, tráfego, tick de economia, level-up de
  prédios, felicidade/poluição, clima/hora. A simulação do CS2 **não é determinística** entre máquinas;
  sincronizar isso exigiria lockstep determinístico (o jogo não tem) ou stream de estado autoritativo
  (muita banda). O mod alinha o que **as ações + dinheiro + XP** conseguem alinhar.
- **Pendentes** (código difícil de RE): terraformação de terreno (heightmap via compute shader),
  linhas de transporte (recriar rota exige dirigir o pathfinding do tool), distritos, água.
- **Redes**: cross-PC snapping/split em nós existentes é aproximado (pontas coincidentes auto-mergeiam).

---

## Build

Com as env vars `CSII_*` do toolchain de modding configuradas:

```bash
dotnet build CS2M/CS2M.csproj -c Release \
  -p:AssemblyVersion=1.0.N.0 -p:FileVersion=1.0.N.0 -p:Version=1.0.N.0
# copie CS2M.dll / CS2M.API.dll / CS2M.BaseGame.dll para  …/Mods/CS2M/
```

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
