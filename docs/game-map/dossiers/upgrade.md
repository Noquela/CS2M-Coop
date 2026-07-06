# Upgrades/extensões de prédio — dossiê de sync

> Mecânica vanilla: `Game.Tools.UpgradeToolSystem` — a ferramenta que planta uma "extensão" (asa de
> hospital, sala extra de escola etc.) ou um "sub-prédio" ligado a um prédio de serviço já existente.
> A UI de gestão desses upgrades instalados (`UpgradesSection`) expõe mais três ações sobre o objeto
> já plantado: desabilitar/habilitar, realocar e apagar. As quatro ações são mecanicamente distintas
> no jogo e são tratadas por partes DIFERENTES do CS2M — o dossiê cobre as quatro.

## 1. Entradas do jogador

- **Plantar uma extensão nova**: `Game.Tools.UpgradeToolSystem` (`kToolID = "Upgrade Tool"`,
  `decomp/Game/Game/Tools/UpgradeToolSystem.cs:38`). Só aceita como prefab um objeto cujo asset tenha
  o componente `Game.Prefabs.ServiceUpgrade` — `TrySetPrefab` rejeita qualquer prefab sem esse
  componente (`UpgradeToolSystem.cs:141-154`). O jogador seleciona o prédio-alvo primeiro
  (`m_UpgradingObject = m_ToolSystem.selected`, `UpgradeToolSystem.cs:164`), e o mouse-click dispara a
  action `"Place Upgrade"` (ou `"Rebuild"` se não há prefab selecionado — reconstrução pós-incêndio/
  abandono), ligadas via `InputManager` em `UpgradeToolSystem.cs:102-103` e despachadas em
  `Apply()` (`UpgradeToolSystem.cs:210-228`).
  - Nota: `Game.Prefabs.ServiceUpgrade` também é anexável a `NetPrefab`/`RoutePrefab`
    (`decomp/Game/Game/Prefabs/ServiceUpgrade.cs:8-14`, atributo `ComponentMenu`), então a mesma
    ferramenta pode em tese alvejar rede/linha de transporte, não só prédio — não verifiquei um
    asset concreto desse tipo (ver §7).
- **Desabilitar/habilitar uma extensão instalada**: painel de upgrades do prédio, binding `"toggle"`
  (`decomp/Game/Game/UI/InGame/UpgradesSection.cs:70-76` e `:98-104`). Não mexe direto no componente —
  chama `PoliciesUISystem.SetSelectedInfoPolicy(entity, prefabDaPolicy"Out of Service", !flag)`.
- **Realocar uma extensão instalada**: binding `"relocate"` (`UpgradesSection.cs:60-64`) chama
  `m_ObjectToolSystem.StartMoving(entity)` e ativa o `ObjectToolSystem` — ou seja, entra no MESMO
  pipeline de mover objeto comum, não é um comando dedicado.
- **Apagar uma extensão instalada**: binding `"delete"` (`UpgradesSection.cs:52-59`) faz
  `AddComponent<Deleted>(entity)` **diretamente na entidade da extensão** (não no prédio-pai).

## 2. Fluxo de aplicação

### 2.1 Plantar (novo)
1. `UpgradeToolSystem.CreateTempObject`/`UpdateDefinitions` monta 1 `ControlPoint` na posição/rotação
   do prédio selecionado (offset local do prefab via `BuildingExtensionData.m_Position`,
   `UpgradeToolSystem.cs:261-268`) e chama
   `CreateDefinitions(objectPrefab, …, owner: m_UpgradingObject, original: Entity.Null, …)`
   (`UpgradeToolSystem.cs:271-289`; assinatura em
   `decomp/Game/Game/Tools/ObjectToolBaseSystem.cs:2309`).
2. O job `CreateDefinitionsJob.Execute` (`ObjectToolBaseSystem.cs:446-643`) resolve `entity = m_Owner`
   (o prédio), monta `ownerDefinition` a partir do `Transform` do prédio, verifica que o prefab da
   extensão é elegível ao prédio via o buffer `ServiceUpgradeBuilding` do prefab
   (`ObjectToolBaseSystem.cs:549`, `558-564`) e então chama `UpdateObject(...)` duas vezes: uma para
   o PRÉDIO (flag `upgrade: true`, linha `:571`, força reprocessar geometria do prédio-pai) e outra
   para a EXTENSÃO em si (linha `:626`, com `original = Entity.Null` → cria entidade nova).
3. `UpdateObject` (`ObjectToolBaseSystem.cs:1088-…`) cria uma entidade com `CreationDefinition` +
   `ObjectDefinition` + `OwnerDefinition` (`OwnerDefinition` é só transitório — não é `ISerializable`,
   `decomp/Game/Game/Tools/OwnerDefinition.cs:7`, então não faz parte do estado que precisa
   convergir). `component.m_Original = original` (`ObjectToolBaseSystem.cs:1100`) é o mecanismo geral
   do jogo para "atualizar entidade existente" vs "criar nova" — aqui é `Entity.Null`, então é criação
   nova.
4. `Game.Tools.GenerateObjectsSystem.CreateObject` (`decomp/Game/Game/Tools/GenerateObjectsSystem.cs:960-1104`)
   consome a definição. Quando `m_Original == Entity.Null` cai no ramo `TempFlags.Create`
   (`GenerateObjectsSystem.cs:1075-1086`) e o objeto final nasce com `Owner` apontando pro prédio (via
   `ownerDefinition`/attach helpers mais abaixo no mesmo método). O componente real
   `Game.Buildings.ServiceUpgrade` (vazio, `decomp/Game/Game/Buildings/ServiceUpgrade.cs:8`) vem do
   arquétipo do prefab, pois `Prefabs.ServiceUpgrade.GetArchetypeComponents` o adiciona
   (`decomp/Game/Game/Prefabs/ServiceUpgrade.cs:56-59`).
5. No MESMO frame (a extensão nasce com `Created`), `ServiceUpgradeReferencesSystem`
   (`decomp/Game/Game/Buildings/ServiceUpgradeReferencesSystem.cs:45-67`) reage a
   `All={ServiceUpgrade, Owner, Object}` `Any={Created,Deleted}` e adiciona
   `InstalledUpgrade(upgrade, optionMask)` no buffer do PRÉDIO dono.
6. `ServiceUpgradeSystem.UpgradeInstalled` (`decomp/Game/Game/Buildings/ServiceUpgradeSystem.cs:138-156`)
   reage a `All={ServiceUpgrade, Object} Any={Created,Deleted}` e adiciona ao PRÉDIO os componentes de
   efeito (`IServiceUpgrade.GetUpgradeComponents`) — é aqui que a capacidade extra (leitos, alunos
   etc.) realmente aparece no prédio.

### 2.2 Desabilitar/habilitar
1. `PoliciesUISystem.SetSelectedInfoPolicy` cria uma entidade com `Event` + `Modify(target, policy,
   active, adjustment)` no `EndFrameBarrier` (`decomp/Game/Game/UI/InGame/PoliciesUISystem.cs:252-262`).
2. `Game.Policies.ModifiedSystem` consome o evento: quando o alvo NÃO é `Building` mas tem
   `Extension` e a policy tem `BuildingOptionData` com a opção `Inactive`, ele escreve diretamente
   `Extension.m_Flags |= / &= ~ExtensionFlags.Disabled` (`decomp/Game/Game/Policies/ModifiedSystem.cs:174-186`).
3. `Game.Buildings.BuildingPoliciesSystem` também reage ao mesmo `Modify` e propaga o estado
   ativo/inativo pro `InstalledUpgrade.m_OptionMask` do prédio dono
   (`decomp/Game/Game/Buildings/BuildingPoliciesSystem.cs:70-114`) — usado por ícone/serviço, não é
   fonte de estado adicional.

### 2.3 Realocar
`ObjectToolSystem.StartMoving` entra em `Mode.Move`
(`decomp/Game/Game/Tools/ObjectToolSystem.cs:2993-3003`) e no `Apply()` do próprio
`ObjectToolSystem` (`ObjectToolSystem.cs:3430-3446`) chama o MESMO pipeline `CreateDefinitions` com
`original = entidadeDaExtensão`. No consumidor final (`GenerateObjectsSystem.CreateObject`), o ramo
`oldEntity != Entity.Null` (`GenerateObjectsSystem.cs:1100-1104`) faz
`RemoveComponent<Deleted>` + `AddComponent<Updated>` + `SetComponent(Transform)` **na entidade já
existente** — ou seja, mover NÃO recria a extensão, só atualiza `Transform` (mesmo padrão validado do
MoveIt, ver `GAME_SYSTEMS.md` §12.1).

### 2.4 Apagar
`AddComponent<Deleted>(entity)` direto na extensão (`UpgradesSection.cs:57`). No fim do frame o
`CleanupSystem` remove a entidade. `ServiceUpgradeReferencesSystem` reage ao `Deleted` (ramo `else` de
`ServiceUpgradeReferencesSystem.cs:68-76`) e remove a entrada `InstalledUpgrade` do buffer do prédio;
`ServiceUpgradeSystem.UpgradeRemoved` (`ServiceUpgradeSystem.cs:158-235`) remove do prédio os
componentes de efeito que não são mais cobertos por nenhum upgrade restante.

## 3. Estado persistido tocado

| Componente | `ISerializable`? | Papel |
|---|---|---|
| `Game.Objects.Transform` (na extensão) | sim (`decomp/Game/Game/Objects/Transform.cs:8`) | posição/rotação — autorado no plantio e na realocação |
| `Game.Common.Owner` (na extensão) | sim (`decomp/Game/Game/Common/Owner.cs:6`) | link pro prédio-pai |
| `Game.Prefabs.PrefabRef` (na extensão) | sim (`decomp/Game/Game/Prefabs/PrefabRef.cs:6`) | qual prefab de extensão |
| `Game.Buildings.Extension.m_Flags` | sim (`decomp/Game/Game/Buildings/Extension.cs:6-19`) | bit `Disabled` — autorado pelo toggle "Out of Service" |
| `Game.Buildings.InstalledUpgrade` (buffer no PRÉDIO) | **NÃO** — `IEmptySerializable` (`decomp/Game/Game/Buildings/InstalledUpgrade.cs:8`) | é DERIVADO (recomputado por `ServiceUpgradeReferencesSystem` a partir de quem tem `Owner==prédio && ServiceUpgrade`) — não precisa ir pelo fio, só a extensão real (Owner+Transform+PrefabRef) precisa |
| `Game.Objects.SubObject` (buffer no prédio) | **NÃO** — `IEmptySerializable` (`decomp/Game/Game/Objects/SubObject.cs:8`) | idem — derivado |
| componentes de efeito adicionados ao prédio por `IServiceUpgrade.GetUpgradeComponents` | não verifiquei caso a caso (§7) | derivados automaticamente pelo `ServiceUpgradeSystem` a partir da extensão existir — não precisam ir pelo fio |

Conclusão: o que REALMENTE precisa convergir pelo fio é só a extensão em si — `Transform` + `Owner`
+ `PrefabRef` (+ `PseudoRandomSeed`/`Elevation` genéricos de objeto) na criação/realocação, e
`Extension.m_Flags` no toggle. Tudo o resto (`InstalledUpgrade`, `SubObject`, efeitos no prédio) é
recomputado localmente pelos sistemas vanilla acima em cada máquina — bate com a lei 13/14 do
`GAME_SYSTEMS.md:153-157`.

## 4. Perigos cross-machine

- **`Entity`/índice de prefab não são estáveis entre máquinas** (`PrefabRef.m_Prefab.Index`) — regra
  geral do jogo (`GAME_SYSTEMS.md:80-89`); a extensão precisa ser endereçada por nome de prefab, nunca
  por índice.
- **Extensão não tem um id cross-machine "de graça"** — ao contrário do prédio (que ganha
  `CS2M_SyncId` na criação), a extensão só existe DEPOIS que o prédio já existe; o próprio comentário
  do mod reconhece isso: *"Extensions carry no shared SyncId (remotes derive them from the owner)"*
  (`CS2M/Sync/PolicyDetectorSystem.cs:213`). Isso é FALSO pela metade — ver §5/§6, o mod na verdade
  ALOCA um `SyncId` pra extensão no momento do plantio, só não o usa depois de forma consistente.
- **Resolução por proximidade** aparece em pelo menos dois pontos do próprio jogo/mod para achar a
  entidade certa da extensão: `PolicyApplySystem.FindNearestExtension` (nome do prefab + raio 3 m,
  `CS2M/Sync/PolicyApplySystem.cs:135-180`) e `RemoteEditApplySystem.ApplyOwnedUpgradeMove` (nome do
  prefab + raio 3 m dentro do buffer `SubObject` do prédio já resolvido,
  `CS2M/Sync/RemoteEditApplySystem.cs:287-320`). Duas extensões do MESMO prefab a menos de 3 m uma da
  outra (prédio com `m_ForbidMultiple=false`, `decomp/Game/Game/Prefabs/ServiceUpgrade.cs:27`, ou duas
  instâncias do mesmo prédio de serviço próximas) causam resolução errada.
- **`InstalledUpgrade`/`SubObject` são buffers derivados** (`IEmptySerializable`) — se algum dia
  alguém tentar sincronizar o BUFFER em si (em vez da entidade real da extensão), vai divergir sempre,
  porque a ordem de inserção/remoção não é garantida igual entre máquinas (não testei a ordem real,
  ver §7).
- **`RandomSeed`/variação de placeholder**: `CreateDefinitionsJob` escolhe entre variações de um
  grupo de prefab (`GetVariationData`, `ObjectToolBaseSystem.cs:645-680`) usando
  `m_RandomSeed.GetRandom(1000000)` quando o prefab selecionado tem buffer
  `PlaceholderObjectElement`. Isso só afeta a extensão se o prefab QUE O JOGADOR ESCOLHEU na UI for,
  ele mesmo, um grupo de variação — não confirmei se algum `ServiceUpgrade`/`BuildingExtensionData`
  real do jogo base é assim (§7); se for, o CS2M (que não manda o `RandomSeed` do tool, só o
  `PseudoRandomSeed` resultante — ver §5) reproduziria a variação ERRADA na recepção.

## 5. O que o CS2M faz hoje

- **Plantar**: `PlacementDetectorSystem.DetectExtensions` (`CS2M/Sync/PlacementDetectorSystem.cs:235-319`)
  detecta qualquer objeto `Applied`+`Owner` cujo prefab tenha `ServiceUpgradeData` OU
  `BuildingExtensionData` (linhas `260-261`) — filtro deliberado pra não confundir com sub-objetos
  derivados do prédio (comentário `:252-264`, bug de campo real documentado: "farm placeholder"
  duplicado). Aloca e registra um `CS2M_SyncId` pra extensão (`:306-307`) e manda `ObjectPlaceCommand`
  com `OwnerSyncId`/`OwnerPrefabName`/`OwnerX,Y,Z` (`CS2M/Commands/Data/Game/ObjectPlaceCommand.cs:57-65`).
  No receptor, `RemotePlacementApplySystem.ApplyOne` clona o objeto **direto do arquétipo do prefab**
  (`CS2M/Sync/RemotePlacementApplySystem.cs:107-120`, abordagem "Option B" documentada no cabeçalho do
  arquivo, `:18-37`) em vez de injetar `CreationDefinition`+`ObjectDefinition` — atalho deliberado
  (mesmo padrão híbrido citado na arquitetura do mod). Resolve o dono por `OwnerSyncId` OU
  nome+posição mais próxima (`ResolveOwner`, `:477-525`), seta `Owner` e marca `Updated` no dono pra
  os sistemas vanilla (`ServiceUpgradeReferencesSystem`/`ServiceUpgradeSystem`) reagirem no mesmo
  frame (`RemotePlacementApplySystem.cs:195-230`, roda `UpdateBefore<RemotePlacementApplySystem>
  (Modification1)`, `CS2M/Mod.cs:134`).
- **Desabilitar/habilitar**: `PolicyDetectorSystem.DetectScopedPolicies`, ramo `kind=4`
  (`CS2M/Sync/PolicyDetectorSystem.cs:206-229`) — só dispara se o alvo tem `Extension`+`Transform`+
  `PrefabRef`; endereça por nome do prefab + posição mundial (não por `SyncId`, apesar de a extensão
  TER um `CS2M_SyncId` desde o plantio — ver GAP em §6). `PolicyApplySystem.ApplyOne` recria o evento
  `Modify` no receptor (`CS2M/Sync/PolicyApplySystem.cs:74-80`) — o MESMO mecanismo vanilla, não
  reimplementa a lógica de flag; resolve o alvo via `FindNearestExtension` (nome+posição, raio 3 m,
  `:118-180`).
- **Realocar**: `MoveDetectorSystem.DetectOwnedUpgradeMoves` (`CS2M/Sync/MoveDetectorSystem.cs:190-263`)
  só dispara para entidades vistas primeiro em `_preMove` (cache do drag real do move-tool — echo
  guard), filtra por `ServiceUpgradeData`/`BuildingExtensionData` (`:216-223`) e manda posição
  ANTIGA+NOVA junto com identidade do dono. `RemoteEditApplySystem.ApplyOwnedUpgradeMove`
  (`CS2M/Sync/RemoteEditApplySystem.cs:264-330`) resolve o dono, acha o filho certo no buffer
  `SubObject` do dono por nome+posição antiga, e faz `SetComponentData(Transform)` +
  `Updated`+`BatchesUpdated` na entidade EXISTENTE — sem recriar, correto pelo padrão MoveIt.
- **Apagar**: **NENHUM sistema do CS2M cobre isso** — ver GAP concreto em §6.
- **Auto-teste do mod**: `AutopilotSystem.cs` tem casos dedicados só para `ext-disable`
  (`case 41/42`, `CS2M/Sync/AutopilotSystem.cs:504-505`, `2808-2875`) e `ext-move`
  (`case 50/51`, `:513-514`, `3083-3151`). Não existe `ActExtPlace`/`VerifyExtPlace` nem qualquer teste
  de "apagar upgrade instalado" — busquei por esses nomes e por `ExtDelete` no arquivo inteiro, zero
  ocorrências.
- **`SyncContract.cs`** — a "garantia nada escapa" do próprio mod
  (`CS2M/Sync/SyncContract.cs:10-24`) mapeia `ToolCoverage["UpgradeToolSystem"] = ["NetUpgradeCommand"]`
  (`:115`). Isso está ERRADO: `NetUpgradeCommand`/`NetUpgradeDetectorSystem` é sobre
  `Game.Net.Edge`/`Node`.`Upgraded.m_Flags` (composição de rede — faixa, semáforo, cruzamento —
  `CS2M/Sync/NetUpgradeDetectorSystem.cs:16-51`), SEM NENHUMA relação com
  `Game.Tools.UpgradeToolSystem`/extensão de prédio. Os comandos REAIS de `UpgradeToolSystem` são
  `ObjectPlaceCommand` (plantio), `PolicyCommand` (toggle) e `MoveCommand` (realocar) — nenhum dos três
  está listado pra essa tool no `ToolCoverage`. Verifiquei que `SyncContract.Verify()` só checa se a
  tool aponta pra ALGUM comando não-Infra existente (`SyncContract.cs:164-184`) — não checa se o
  mapeamento é o CORRETO nem se é EXAUSTIVO — então essa incoerência não é pega pelo próprio
  "guard-rail" que o mod promete ("Verify() reports it. There is no silent hole.",
  `SyncContract.cs:23-24`).

## 6. GAPS e recomendação

1. **[ALTO] Apagar um upgrade instalado não sincroniza — divergência de estado autorado permanente.**
   `UpgradesSection.OnDelete` marca `Deleted` DIRETO na entidade da extensão, que tem `Owner`
   (`UpgradesSection.cs:57`/`117`). As duas queries de delete do CS2M excluem `Owner` explicitamente:
   `_deletedQuery` (`CS2M/Sync/DeleteDetectorSystem.cs:38-57`, `None` inclui `Owner` na linha `54`) e
   `_deletedNativeQuery` (`:79-99`, `Owner` na linha `95`) — comentário do próprio arquivo confirma a
   intenção geral ("only top-level objects (no Owner) are sent", `:18-20`), mas essa regra
   INCLUI sem querer o caso de upgrade instalado, que é justamente uma ação de jogador top-level (não
   um sub-objeto derivado automático). Cenário concreto: host clica em "apagar" numa asa de hospital
   pelo painel de upgrades → nada é detectado/enviado → o client mantém a asa pra sempre (até um
   `/resync` completo, se existir). Checklist do fix: nova query (ou exceção na existente) para
   `Deleted`+`Owner`+`(ServiceUpgradeData ou BuildingExtensionData no prefab)`, endereçada pelo mesmo
   `CS2M_SyncId` já alocado no plantio (`PlacementDetectorSystem.cs:306-307`) — o id já existe, só
   falta usá-lo aqui.
2. **[ALTO] Toggle disable/enable resolve por proximidade+nome, sem tentar o `CS2M_SyncId` que a
   extensão já tem.** `PolicyApplySystem.FindNearestExtension` (`CS2M/Sync/PolicyApplySystem.cs:118-180`)
   não tenta `CS2M_SyncIdSystem.Map` em nenhum momento — vai direto pra busca por nome+posição (raio
   3 m), ao contrário do branch `kind==1` (prédio) que tenta `TargetSyncId` PRIMEIRO
   (`PolicyApplySystem.cs:102-108`). Como a extensão RECEBE um `CS2M_SyncId` no plantio
   (`PlacementDetectorSystem.cs:306-307`) e ele é preservado no receptor
   (`RemotePlacementApplySystem.cs:248`), a informação existe — só não é carregada no
   `PolicyCommand`/usada no apply. Cenário concreto: duas extensões do mesmo prefab dentro de 3 m
   (prédio com `m_ForbidMultiple=false`) → desabilitar uma no host desabilita a errada no client.
   Checklist: `PolicyDetectorSystem.DetectScopedPolicies` (kind=4) ler `CS2M_SyncId` da extensão se
   ela tiver um (mesmo padrão do kind=1, linhas `201-204`) e mandar em `TargetSyncId`;
   `PolicyApplySystem.ResolveTarget` tentar `TargetSyncId` antes de cair em `FindNearestExtension`.
3. **[MÉDIO] Plantio por clone direto de arquétipo não replica a limpeza de área (`ClearAreaHelpers`)
   que o `CreateDefinitionsJob` faz no jogador que planta.** No jogo real,
   `ObjectToolBaseSystem.CreateDefinitionsJob.Execute` roda `ClearAreaHelpers.FillClearAreas`/
   `InitClearAreas` (`ObjectToolBaseSystem.cs:551-564`) pra remover sub-objetos/áreas que ficariam sob
   a pegada da nova extensão. `RemotePlacementApplySystem.ApplyOne` não chama nada equivalente — só
   replica o `ClearLotFor` para spawns de sim (`cmd.Source==1`, `RemotePlacementApplySystem.cs:127-130`),
   não para extensões plantadas por jogador. Como sub-objetos decorativos já divergem por design (lei
   13/14), o efeito prático é cosmético (planta/prop que sumiu no host continua vivo no client sob a
   extensão nova) — mas é uma divergência visual real, não zero.
4. **[BAIXO/observação] Nenhum teste automatizado (`AutopilotSystem`) cobre plantio nem exclusão de
   upgrade instalado** — só `ext-disable` e `ext-move` têm casos dedicados
   (`CS2M/Sync/AutopilotSystem.cs:504-505`, `513-514`). O gap #1 acima não teria sido pego pela suíte
   de bot mesmo com o autopilot rodando sozinho — recomendo casos `ext-place`/`ext-delete` no mesmo
   padrão dos existentes.
5. **[BAIXO/meta] `SyncContract.ToolCoverage["UpgradeToolSystem"]` está semanticamente errado**
   (aponta pra `NetUpgradeCommand`, que é de composição de rede — `NetUpgradeDetectorSystem.cs:16-51`
   — não de extensão de prédio) e o `Verify()` não tem poder de pegar isso (só confere existência de
   mapeamento, não corretude — `SyncContract.cs:164-184`). Recomendo corrigir a entrada pra
   `["ObjectPlaceCommand", "PolicyCommand", "MoveCommand", "DeleteCommand"]` (esse último só depois do
   fix do gap #1) — puramente documental, mas é a âncora que o próprio mod usa pra alegar
   "nada escapa".

## 7. NÃO VERIFICADO

- Não confirmei se algum `ServiceUpgradeData`/`BuildingExtensionData` do jogo BASE (sem mods) usa
  buffer `PlaceholderObjectElement` (grupo de variação aleatória) — se algum usar, o perigo de
  `RandomSeed`/variação do §4 é real; não abri um asset de extensão específico pra testar.
- Não abri um exemplo concreto de `Game.Prefabs.ServiceUpgrade` anexado a `NetPrefab`/`RoutePrefab`
  (o `ComponentMenu` permite, `Prefabs/ServiceUpgrade.cs:8-14`) — não sei se existe algum upgrade de
  rede/linha que passa pela mesma `Game.Tools.UpgradeToolSystem` em vez de `NetToolSystem`; se
  existir, o comentário do CS2M em `PlacementDetectorSystem.cs` (que assume tudo owned+ServiceUpgrade
  é "extensão de prédio") precisaria ser revisto para esse caso.
- Não tracei os componentes exatos que `IServiceUpgrade.GetUpgradeComponents` adiciona ao prédio pra
  casos reais do jogo base (ex.: qual componente concreto vira "mais leitos" num hospital) — assumi,
  por leitura do `ServiceUpgradeSystem.cs`, que são todos derivados/recomputáveis localmente, mas não
  vi caso a caso se algum desses componentes tem estado que só existe uma vez (ex.: contador
  acumulado) e que poderia divergir mesmo sendo "derivado".
- Não testei ao vivo (2 sims) nenhum dos 4 fluxos — o dossiê é 100% leitura de código (decomp do jogo
  + código do mod), não validação em tela. Os GAPS 1 e 2 são inferências fortes a partir das queries
  ECS lidas, mas não têm confirmação de comportamento observado em jogo.
- Não verifiquei se existe um `/resync` completo que reconstruiria a extensão apagada (mascarando o
  GAP 1 até o próximo resync) — não achei código de resync específico para `InstalledUpgrade`/
  `Extension` na busca feita, mas não fiz uma varredura exaustiva do fluxo de resync geral.
- Não confirmei a ordem exata de fases (`UpdateInGroup`/`UpdateBefore`/`UpdateAfter`) de
  `ServiceUpgradeSystem`, `ServiceUpgradeReferencesSystem`, `BuildingPoliciesSystem` e
  `Game.Policies.ModifiedSystem` dentro de `Modification1..5` — os atributos de fase não aparecem
  nesta decompilação (parecem removidos pelo ILSpy nessas classes específicas); confiei no comentário
  já validado do próprio CS2M (`RemotePlacementApplySystem.cs:26-32`, `CS2M/Mod.cs:134`) de que
  `RemotePlacementApplySystem` roda antes de `Modification1` e por isso os sistemas vanilla acima
  reagem no mesmo frame — não abri o `PlayerLoop`/`ComponentSystemGroup` pra confirmar a ordem interna
  entre eles.
