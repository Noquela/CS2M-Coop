# Economia via UI (impostos, orçamento, empréstimo, políticas, taxas de serviço) — dossiê de sync

> Fonte: `decomp/Game/Game/UI/InGame/{TaxationUISystem,ServiceBudgetUISystem,LoanUISystem,PoliciesUISystem}.cs`
> + sistemas de simulação acionados (`Game.Simulation.TaxSystem` e `Game.Simulation.ServiceFeeSystem` — **ausentes
> do decomp, ver §7**; `Game.Simulation.CityServiceBudgetSystem`; `Game.Tools.LoanSystem`; `Game.Policies.ModifiedSystem`)
> + mod em `CS2M/Sync/*` e `CS2M/Commands/*`.

## 1. Entradas do jogador

Nenhuma dessas quatro telas passa pelo pipeline padrão de ferramenta (`CreationDefinition`/`Temp`/`Applied`)
— são todas `TriggerBinding`s que mutam componente/buffer diretamente ou disparam um evento leve.

| Painel | Trigger | O que dispara |
|---|---|---|
| Impostos (geral) | `setTaxRate(int rate)` | `TaxationUISystem.cs:189-197` → `m_TaxSystem.TaxRate = rate` |
| Impostos (por área) | `setAreaTaxRate(int areaType, int rate)` | `TaxationUISystem.cs:198-205` → `m_TaxSystem.SetTaxRate((TaxAreaType)areaType, rate)` |
| Impostos (por recurso) | `setResourceTaxRate(int resource, int areaType, int rate)` | `TaxationUISystem.cs:206-232` → `SetResidentialTaxRate`/`SetCommercialTaxRate`/`SetIndustrialTaxRate`/`SetOfficeTaxRate` conforme `areaType` |
| Orçamento de serviço | `setServiceBudget(Entity service, int percentage)` | `ServiceBudgetUISystem.cs:155-158` → `m_CityServiceBudgetSystem.SetServiceBudget(service, percentage)` |
| Taxa de serviço (fee) | `setServiceFee(PlayerResource resource, float amount)` | `ServiceBudgetUISystem.cs:159-166` → `ServiceFeeSystem.SetFee(resource, cityFeeBuffer, amount)` direto no buffer da City (bloqueia `PlayerResource.Parking`, linha 161) |
| Resetar serviço | `resetService(Entity service)` | `ServiceBudgetUISystem.cs:167-179` → `SetServiceBudget(service,100)` + `SetServiceFee` pro default de cada fee coletado |
| Empréstimo (simular oferta) | `requestLoanOffer(int amount)` | `LoanUISystem.cs:45,64-69` → só calcula `m_RequestedOfferDifference` local, **não muta estado persistido** |
| Empréstimo (confirmar) | `acceptLoanOffer()` | `LoanUISystem.cs:46,71-75` → `m_LoanSystem.ChangeLoan(amount)` |
| Empréstimo (cancelar oferta) | `resetLoanOffer()` | `LoanUISystem.cs:47,77-80` → zera `m_RequestedOfferDifference`, sem mutação |
| Política (cidade) | `setCityPolicy(Entity policy, bool active, float adjustment)` | `PoliciesUISystem.cs:178-182,211-215` → `ModifyPolicy(m_CitySystem.City, policy, active, adjustment)` |
| Política (entidade selecionada: prédio/distrito/rota) | `setPolicy(Entity policy, bool active, float adjustment)` | `PoliciesUISystem.cs:174-177,206-209` → `ModifyPolicy(m_InfoSystem.selectedEntity, ...)` |

## 2. Fluxo de aplicação

### 2.1 Impostos
1. O trigger (`TaxationUISystem.cs:189-232`) chama direto `m_TaxSystem.{TaxRate, SetTaxRate, SetResidentialTaxRate,
   SetCommercialTaxRate, SetIndustrialTaxRate, SetOfficeTaxRate}` — sem `CreationDefinition`, é mutação de estado
   escalar, não geometria.
2. O resultado fica em `Game.City.TaxRates` — buffer `ISerializable` indexado pelo enum fixo `Game.City.TaxRate`
   (`Main=0`, `ResidentialOffset=1`, ..., `Count=92`, `TaxRate.cs:3-14`; struct do buffer em `TaxRates.cs:6-19`).
   O offset por recurso usa `EconomyUtils.GetResourceIndex(Resource)`/`GetResource(int)=1L<<index`
   (`EconomyUtils.cs:26+,213-220`) — mapeamento por `switch`/bit-shift **fixo em código**, não depende de ordem de
   carga de asset.
3. `Game.Simulation.TaxSystem` roda em `SystemUpdatePhase.GameSimulation` (`SystemOrder.cs:483`) — ou seja, ANTES
   das fases de Modification do mesmo frame; o valor gravado só afeta o cálculo de receita a partir do próximo
   `GameSimulation`.
4. A classe `TaxSystem` em si **não está no decomp** (ver §7): só pude confirmar o contrato via call-sites
   (`TaxationUISystem.cs`, `EconomyUtils.cs:446`, `SystemOrder.cs:483`) e via o próprio mod (`CS2M/Sync/TaxDetectorSystem.cs:35`
   chama `_taxSystem.GetTaxRates()`).

### 2.2 Orçamento de serviço (funding %)
1. `ServiceBudgetUISystem.setServiceBudget` (`ServiceBudgetUISystem.cs:155-158`) chama
   `ICityServiceBudgetSystem.SetServiceBudget(Entity servicePrefab, int percentage)` (`ICityServiceBudgetSystem.cs:25`).
2. `CityServiceBudgetSystem.SetServiceBudget` (`CityServiceBudgetSystem.cs:1080-1116`): procura/atualiza
   `DynamicBuffer<ServiceBudgetData>` num **entity singleton dedicado** (criado em `OnGameLoaded` se ausente —
   `CityServiceBudgetSystem.cs:832-838`); a chave é o **`Entity` do PREFAB** da categoria de serviço
   (`m_Service == servicePrefab`), o valor é o percentual; marca o singleton `Updated` via `m_EndFrameBarrier`
   (linhas 1112-1115).
3. O próprio `CityServiceBudgetSystem` roda em `SystemUpdatePhase.ModificationEnd`
   (`SystemOrder.cs:301`, via `UpdateAfter`) recalculando upkeep/eficiência a partir do novo percentual
   (`GetEstimatedServiceUpkeep`, `CityServiceBudgetSystem.cs:1123-1137`).

### 2.3 Taxa de serviço (fee)
1. `ServiceBudgetUISystem.setServiceFee` (`ServiceBudgetUISystem.cs:159-166`) chama o método estático
   `Game.Simulation.ServiceFeeSystem.SetFee(resource, buffer, amount)` diretamente sobre
   `DynamicBuffer<ServiceFee>` da City.
2. `ServiceFee` é buffer element `ISerializable` na City (`Game.City.ServiceFee`, `ServiceFee.cs:6-51`), indexado
   por `PlayerResource` — enum fixo com 13 valores + `Count` (`PlayerResource.cs:6-22`).
3. `ServiceFeeSystem` roda em `SystemUpdatePhase.GameSimulation` (`SystemOrder.cs:415`) consumindo esse buffer pra
   calcular consumo/felicidade/renda (efeitos expostos via `ServiceFeeSystem.GetConsumptionMultiplier`/
   `GetEfficiencyMultiplier`/`GetHappinessEffect`, usados em `ServiceBudgetUISystem.cs:299-304`). A classe
   `ServiceFeeSystem` em si **também não está no decomp** (§7).

### 2.4 Empréstimo (loan)
1. `LoanUISystem.AcceptLoanOffer` (`LoanUISystem.cs:71-75`) chama `m_LoanSystem.ChangeLoan(amount)`.
2. `LoanSystem.ChangeLoan` (`LoanSystem.cs:129-136`) enfileira um `LoanAction{m_Amount}` (`LoanAction.cs:3-6`) num
   `NativeQueue` persistente (`m_ActionQueue`).
3. `LoanSystem.OnUpdate` (`LoanSystem.cs:107-122`) agenda `LoanActionJob` (`LoanSystem.cs:19-48`), que desenfileira
   e, por ação: soma a diferença (novo−antigo) em `Game.City.PlayerMoney` da City (`PlayerMoney.Add`,
   `PlayerMoney.cs:33-36`) e grava `Game.Simulation.Loan{m_Amount, m_LastModified}` na City
   (`LoanSystem.cs:41-46`).
4. `Game.Tools.LoanSystem` roda em `SystemUpdatePhase.Modification1` (`SystemOrder.cs:104`).
5. O componente `Loan` em si **não tem arquivo próprio no decomp** (§7) — só o vi via uso em `LoanSystem.cs` e no
   mod (`CS2M/Sync/LoanSyncSystems.cs:73,78,130-157`).

### 2.5 Políticas (cidade / prédio / distrito / rota)
1. `PoliciesUISystem.ModifyPolicy` (`PoliciesUISystem.cs:257-262`) cria uma entidade nova via
   `m_EndFrameBarrier.CreateCommandBuffer()`, com `Event` + `Modify(target, policy, active, adjustment)`
   (`Modify.cs:5-22`).
2. `Game.Policies.ModifiedSystem` (**não** existe uma classe `PolicyModifiedSystem` separada — é este mesmo tipo,
   `ModifiedSystem.cs`) consome toda entidade `Event`+`Modify` em `SystemUpdatePhase.Modification4`
   (`SystemOrder.cs:146`). `ModifyPolicyJob.Execute` (`ModifiedSystem.cs:103-196`):
   - Se o target tem buffer `Policy`: procura a entrada com `m_Policy == modify.m_Policy`; se está ativa e o
     evento desativa, remove (ou reseta pro default de slider, linhas 122-148); senão atualiza
     `m_Flags`/`m_Adjustment` (linhas 150-152) ou adiciona nova `Policy` (linhas 159-162).
   - `RefreshEffects` (`ModifiedSystem.cs:250-299`) recalcula `DistrictModifier`/`BuildingModifier`/`RouteModifier`/
     `CityModifier` e as opções embutidas em `District`/`Building`/`Route`/`City` a partir do buffer `Policy`
     atualizado, e marca o target `Updated` (linha 298).
   - Dois casos fora do buffer `Policy`: `Extension` (liga/desliga upgrade de prédio de serviço, linhas 174-186) e
     `ServiceUpgrade`/`Owner` (propaga `Updated` pro dono, linhas 187-194).
3. O buffer resultante é `Game.Policies.Policy` (`Policy.cs:7-36`) — `ISerializable`; guarda `m_Policy` como
   **`Entity` de PREFAB** + `m_Flags` (`PolicyFlags.cs:6-9`, só tem `Active=1`) + `m_Adjustment` float.

## 3. Estado persistido tocado
- `Game.City.TaxRates` (buffer, `ISerializable`, `TaxRates.cs:6-19`) — na City, indexado por `Game.City.TaxRate` (fixo).
- `Game.Simulation.ServiceBudgetData` (buffer, `ISerializable`, `ServiceBudgetData.cs:6-27`) — em singleton dedicado
  (`CityServiceBudgetSystem.cs:812,832-838`); `m_Service` é **`Entity` de PREFAB**.
- `Game.City.ServiceFee` (buffer, `ISerializable`, `ServiceFee.cs:6-51`) — na City; `m_Resource` é `PlayerResource`
  (enum fixo), `m_Fee` float.
- `Game.Simulation.Loan` (componente na City; quase certamente `ISerializable` — arquivo ausente do decomp, §7):
  `m_Amount`, `m_LastModified` (usados em `LoanSystem.cs:41-46,76-88` e no mod `LoanSyncSystems.cs:78,135-157`).
- `Game.City.PlayerMoney` (componente, `ISerializable`, `PlayerMoney.cs:7-57`) — na City; `m_Money` privado +
  `m_Unlimited`.
- `Game.Policies.Policy` (buffer, `ISerializable`, `Policy.cs:7-36`) — no target (City/District/Building/Route);
  `m_Policy` é **`Entity` de PREFAB** + `m_Flags` + `m_Adjustment`.
- Efeitos derivados (recalculados a partir do acima, não são fonte de verdade independente, mas mudam junto):
  buffers `DistrictModifier`/`BuildingModifier`/`RouteModifier`/`CityModifier` e as opções embutidas nos structs
  `District`/`Building`/`Route`/`City` (`ModifiedSystem.cs:252-297`).

## 4. Perigos cross-machine
- **`Entity` de prefab cru como chave.** `ServiceBudgetData.m_Service` (`ServiceBudgetData.cs:8`) e
  `Policy.m_Policy` (`Policy.cs:9`) guardam um `Entity` que aponta pro PREFAB (categoria de serviço / prefab de
  política). `Entity.Index`/`Version` de um prefab só bate nas duas máquinas se `PrefabSystem` criar os prefabs
  NA MESMA ORDEM nas duas — não garantido em geral (mesma classe de risco do `ZoneType.m_Index` já suspeito em
  `architecture-decision.md`). O PRÓPRIO mod documenta esse risco: comentário de `AccPolicies` — "keyed by policy
  prefab NAME (cross-machine stable, unlike the prefab entity index)" (`StateHashSystems.cs:209-211`).
- **`TaxSystem`/`ServiceFeeSystem`: classes ausentes do decomp.** Busca exaustiva na árvore `decomp/` não achou
  `TaxSystem.cs`, `ServiceFeeSystem.cs`, `ITaxSystem.cs` nem `IServiceFeeSystem.cs` em lugar nenhum — só existem via
  referência (`SystemOrder.cs:483,415,870,876`; `EconomyUtils.cs:446`). O mod's `TaxApplySystem` escreve **direto**
  no `NativeArray<int>` devolvido por `GetTaxRates()` (`CS2M/Sync/TaxApplySystem.cs:38-48`), contornando qualquer
  clamp/validação que `SetTaxRate`/`SetResidentialTaxRate`/etc. façam internamente (não visível — §7). Se o setter
  vanilla fizer algo além de gravar o array (ex.: invalidar um cache), esse efeito colateral é pulado.
- **Resolução de política por PROXIMIDADE quando falta identidade.** Em `PolicyApplySystem.ResolveTarget`
  (`PolicyApplySystem.cs:83-132`):
  - `kind=1` (prédio): tenta `CS2M_SyncId` primeiro; **sem** SyncId cai em `FindNearest` por `Transform` dentro de
    9 unidades² (~3 m) — `PolicyApplySystem.cs:102-116`.
  - `kind=2` (distrito, ramo `else` default do método): **sempre** por proximidade — `FindNearest` por
    `Geometry.m_CenterPosition` dentro de 2500 unidades² (**~50 m de raio**) — `PolicyApplySystem.cs:126-131`. Não
    há caminho de SyncId pra distrito aqui.
  - `kind=4` (extensão de prédio de serviço): **sempre** por proximidade — `FindNearestExtension` por nome de
    prefab + posição dentro de 9 unidades² — `PolicyApplySystem.cs:118-122,135-180`.
  É exatamente o padrão já registrado em `bug-juncao-sync.md` ("client ADIVINHA junção por proximidade"). Dois
  distritos/prédios/extensões do mesmo tipo dentro do raio podem ser confundidos, ou nada é achado e o toggle é
  silenciosamente descartado (log `"[Policy] SKIP noTarget"`, `PolicyApplySystem.cs:48-53`).
- **`PrefabID` com `Hash128` zerado.** `BudgetApplySystem` (`BudgetApplySystem.cs:44`) e `PolicyApplySystem`
  (`PolicyApplySystem.cs:56`) resolvem o prefab via `new PrefabID(type, name, default(Colossal.Hash128))` — se dois
  prefabs (ex.: de conteúdo de mods diferentes) compartilharem tipo+nome mas tiverem GUID diferente, o hash zerado
  pode resolver o prefab errado. NÃO VERIFICADO se `PrefabSystem.TryGetPrefab(PrefabID)` de fato ignora/exige o
  `Hash128` quando há ambiguidade (§7).
- **`StateHash` não cobre orçamento de serviço nem empréstimo.** `HashBundle` tem `Money`/`TaxHash`/`PolicyHash`/
  `FeeHash` (`StateHashSystems.cs:44-49`) mas **nenhum campo** pro percentual de orçamento de serviço nem pro
  valor do empréstimo — confirmado por busca (`BudgetHash`/`LoanHash`/`AccBudget`/`AccLoan` = 0 ocorrências em
  `StateHashSystems.cs`). O comentário do próprio `AccFees` explica por que isso importa: "Fees drive consumption/
  happiness/income but never move any entity, so a fee-only divergence was invisible to the radar before this"
  (`StateHashSystems.cs:264-265`) — o mesmo argumento vale pro % de orçamento (mexe upkeep/eficiência) e pro
  empréstimo (mexe `PlayerMoney` via `LoanActionJob`), mas nenhum dos dois tem hash dedicado.
- **Empréstimo: mirror de dinheiro só no HOST.** `LoanApplySystem` só soma o delta em `PlayerMoney` quando
  `NetworkInterface.Instance.LocalPlayer.PlayerType == PlayerType.SERVER` (`LoanSyncSystems.cs:144-153`); depende
  de o `LoanSystem` vanilla já ter rodado LOCALMENTE e mexido o `PlayerMoney` local de quem originou a ação (mesmo
  se for um client) — a reconciliação final do saldo do client depende do money-sync geral (não auditado neste
  dossiê, §7).

## 5. O que o CS2M faz hoje
Todos classificados `WorldContract` no manifesto (`SyncContract.cs:73-77`): `PolicyCommand`, `TaxSyncCommand`,
`FeeCommand`, `BudgetCommand`, `LoanCommand`.

- **Impostos** — `TaxDetectorSystem` (`CS2M/Sync/TaxDetectorSystem.cs:28-77`) faz diff do array inteiro
  `_taxSystem.GetTaxRates()` (~92 posições) contra um snapshot estático (`TaxSync.cs`) todo frame; manda o array
  INTEIRO via `TaxSyncCommand{Rates}` se algo mudou. `TaxApplySystem` (`TaxApplySystem.cs:26-61`) escreve o array
  recebido direto no `NativeArray` local e resincroniza o snapshot (echo guard). Fases: detector
  `UpdateBefore<TaxDetectorSystem>(ModificationEnd)`, apply `UpdateAt<TaxApplySystem>(Modification5)` —
  `Mod.cs:141-142`.
- **Orçamento de serviço** — `BudgetDetectorSystem` (`BudgetDetectorSystem.cs:34-85`) varre toda `ServiceData` com
  `m_BudgetAdjustable`, resolve o NOME via `PrefabSystem` (linhas 52-57), faz diff contra `BudgetSync.Snapshot`
  (chave = nome do prefab, não `Entity`) e manda `BudgetCommand{ServiceType, ServiceName, Percentage}`.
  `BudgetApplySystem` resolve de volta via `PrefabID(type,name)` → `TryGetEntity` (`BudgetApplySystem.cs:44-55`) e
  chama `SetServiceBudget`. Fases: `Mod.cs:151-152`.
- **Taxa de serviço (fee)** — `FeeDetectorSystem` (`FeeDetectorSystem.cs:31-65`) faz diff do buffer `ServiceFee` da
  City por `PlayerResource` (int) contra `FeeSync.Snapshot`, manda `FeeCommand{Resource, Fee}`. `FeeApplySystem`
  chama `ServiceFeeSystem.SetFee` — a MESMA API que a UI usa (`FeeApplySystem.cs:51-56`). Cobre também o botão
  Reset (reescreve o buffer inteiro; o diff pega cada recurso mudado individualmente — comentário em
  `FeeDetectorSystem.cs:14-15`). Fases: `Mod.cs:156-157`.
- **Empréstimo** — `LoanDetectorSystem` faz *polling* de `Loan.m_Amount` a cada 30 frames
  (`LoanSyncSystems.cs:46,58-93`) e manda `LoanCommand{Amount}` se mudou. `LoanApplySystem` grava `Loan` direto +
  espelha o delta em `PlayerMoney` **só no host** (`LoanSyncSystems.cs:101-160`). Fases: `Mod.cs:252-253`.
- **Políticas** — `PolicyDetectorSystem` faz diff do buffer `Policy` da City por NOME de prefab (não `Entity`)
  contra `PolicySync.Snapshot` (`PolicyDetectorSystem.cs:51-114`), MAIS um caminho separado
  (`DetectScopedPolicies`, linhas 118-255) que lê eventos `Event`+`Modify` crus pra pegar toggles de
  prédio/distrito/rota/extensão (`kind` 1/2/3/4), endereçando por `CS2M_SyncId` quando existe, senão por
  posição/nome. `PolicyApplySystem` recria o mesmo par `Event`+`Modify` que a UI cria
  (`PolicyApplySystem.cs:74-78`), resolvendo o target por SyncId/proximidade conforme `kind`
  (`ResolveTarget`, linhas 83-132). Fases: `Mod.cs:147-148` — apply em `Modification3`, ANTES do `ModifiedSystem`
  do jogo em `Modification4` (`SystemOrder.cs:146`), ou seja consumido no MESMO frame.
- **Radar de drift** — `StateHashSystems` inclui `TaxHash`/`PolicyHash`/`FeeHash`/`Money` no fingerprint comparado
  a cada ~10 s (`StateHashSystems.cs:44-49,187-207,212-282`).

## 6. GAPS e recomendação
1. **Adicionar `BudgetHash` e `LoanHash` ao `StateHash`.** Hoje um % de orçamento de serviço ou um valor de
   empréstimo divergente só aparece via o efeito lento em `Money` (ou nunca, se as duas simulações convergirem em
   outro número por coincidência) — o mesmo raciocínio que já motivou `TaxHash`/`FeeHash`/`PolicyHash`
   (`StateHashSystems.cs:264-265`) vale igual aqui. Checklist: (a) dobrar `ServiceBudgetData` por NOME do prefab
   (não `Entity`), do jeito que `AccPolicies` já faz; (b) dobrar `Loan.m_Amount` (e talvez a taxa de juros
   calculada) num `LoanHash`.
2. **Endereçamento de política por distrito/extensão nunca tem fallback de identidade.** Ao contrário de prédio e
   rota (que preferem `CS2M_SyncId`), distrito (`kind=2`) e extensão (`kind=4`) em
   `PolicyApplySystem.ResolveTarget` dependem 100% de proximidade — 50 m de raio pra distrito é bastante espaço
   pra ambiguidade numa cidade densa. Checklist: dar um `CS2M_SyncId` (ou equivalente) a `District` e a
   `Extension`, igual já existe pra `Building`/`Route`, e preferir esse id antes do `FindNearest`.
3. **Confirmar clamps de `TaxSystem`/`ServiceFeeSystem`.** Como as classes não estão no decomp, não dá pra garantir
   que escrever direto no array (`TaxApplySystem.cs:38-48`) preserva 100% do comportamento vanilla (clamp aos
   limites de `TaxParameterData`, possível invalidação de cache). Recomendo redecompilar especificamente esses
   dois tipos antes de mexer nesse código de novo.
4. **`PrefabID` com `Hash128` default.** Se o parque de conteúdo algum dia tiver dois prefabs de serviço/política
   com mesmo tipo+nome mas GUID diferente, `BudgetApplySystem`/`PolicyApplySystem` podem resolver o prefab errado
   (hash zerado não desambiguiza). Risco baixo hoje (sem esse cenário nos 3 jogadores), mas vale nota se algum dia
   entrar conteúdo de mod concorrente.
5. **`TaxApplySystem` sem `try/catch`.** Ao contrário de Fee/Budget/Loan/Policy apply (todos com `try/catch` por
   comando recebido), `TaxApplySystem.OnUpdate` (`TaxApplySystem.cs:26-61`) não protege a escrita — uma exceção ali
   derruba o resto do `OnUpdate` daquele frame. Já tolera tamanho de array diferente via
   `Math.Min(rates.Length, incoming.Length)` (linha 44), mas não envolve a escrita num `try/catch` como os outros
   quatro sistemas de aplicação.

## 7. NÃO VERIFICADO
- Código-fonte de `Game.Simulation.TaxSystem` — a classe não existe em nenhum arquivo da árvore `decomp/` (busca
  exaustiva por nome de classe e pela interface `ITaxSystem`, ambas ausentes); só pude inferir comportamento pelos
  call-sites em `TaxationUISystem.cs`, `EconomyUtils.cs:446` e `SystemOrder.cs:483,876`.
- Código-fonte de `Game.Simulation.ServiceFeeSystem` e da interface `IServiceFeeSystem` — mesma situação (ausentes
  do decomp); só vi call-sites em `ServiceBudgetUISystem.cs` e `SystemOrder.cs:415,870`.
- Código-fonte de `Game.Simulation.Loan` e `Game.Simulation.Creditworthiness` (os COMPONENTES, não o sistema) —
  não encontrei `Loan.cs`/`Creditworthiness.cs` na árvore decompilada; só vi uso via `LoanSystem.cs` e
  `CS2M/Sync/LoanSyncSystems.cs`. Não pude confirmar se são `ISerializable` nem o layout exato de serialização
  (embora seja quase certo que sim, dado que o valor do empréstimo persiste no save).
- Semântica exata de `PrefabSystem.TryGetPrefab(PrefabID)` quanto ao campo `Hash128` — não localizei essa
  implementação no decomp pra confirmar se um `Hash128` default/zerado casa com qualquer prefab do mesmo
  tipo+nome ou se causa falha de resolução quando há ambiguidade.
- Definição do enum `PlayerType` (usado em `LoanSyncSystems.cs:144`) — não encontrado no código do mod disponível
  (deve vir de `CS2M.API.Networking`, fora do escopo lido neste dossiê).
- `MoneySyncApplySystem`/`MoneySyncSenderSystem` — citados como a via de reconciliação final do saldo do client
  depois de um empréstimo remoto, mas não abri esses arquivos neste dossiê (fora do escopo direto de "economia via
  UI", mas relevante pra quem for investigar o GAP #1).
- Corpo interno de `CityServiceBudgetSystem` fora dos métodos públicos citados — a classe tem 1221 linhas; li
  `OnCreate`/`OnGameLoaded`/`SetServiceBudget`/`GetServiceBudget`/`GetEstimatedServiceBudget`, mas não os jobs
  `IJobChunk` de recomputação de upkeep linha a linha (`CityServiceBudgetSystem.cs:~1-780`).
