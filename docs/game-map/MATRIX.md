# MATRIZ DE COBERTURA — game-map (06/07/2026)

> **⚠️ STATUS 07/07/2026 — vários P0/P1 abaixo JÁ FORAM FECHADOS (esta matriz é de 06/07, o código avançou).**
> Fechados desde então (ver memory/bug-juncao-sync.md): **ServiceDistrict** (commit 8d0bb8e — ServiceDistrictCommand/Detector/Apply, cobre removeDistrict do painel), **VehicleModel/SelectVehicles** (mesmo 8d0bb8e — VehicleModelCommand), **delete-de-extensão pelo painel** (v56 — DeleteDetector `_deletedExtensionQuery`), **fazenda/Extractor** (anchor por identidade de placeholder — areas IDÊNTICO validado 2-sim), **zone paint diverge** (ZoneOrderTiebreak PosHash32 + ZoneBlockAuthority; flags Blocked/Visible são derivadas, statediff refinado), **net BuildOrder** (ZoneOrderTiebreak). Novos fixes gated (OFF por padrão) prontos: CS2M_DELFIX (delete-de-remoto água/distrito), ROUTEFIX (reroute save-line), TAXFIX (tax concorrente granular), POLICYFIX (policy prédio por prefab), DEVTREEFIX, MOVEFIX (SubNet/SubArea no move), NODEHEAL, OVERDRAWFIX. **chirper.addLike = confirmado EMERGENTE (Chirp não tem identidade cross-machine), não é gap sincável.** Enumeração de UI recontada 07/07 = 249 TriggerBindings. Validado 2-sim ao vivo: fazenda, overdraw, delete-de-remoto, tax, move. **NÃO re-descobrir os gaps riscados acima como abertos.**

> Síntese dos 13 dossiês (`dossiers/`) + classificação dos 555 tipos serializados (`state/*.json`)
> + varredura dos 196 triggers de UI (`ui-triggers.md`). **Verificada por
> `tools/autotest/coverage_check.py` = COMPLETUDE OK em 06/07** — o script re-enumera os 3 conjuntos
> fechados direto do decomp e FALHA se qualquer item ficar órfão (tool nova de update do jogo
> quebra o check sozinha). Toda afirmação dos dossiês cita arquivo:linha; o que não foi confirmado
> está nas seções "NÃO VERIFICADO" de cada dossiê — a matriz não esconde incerteza.

## Os números

| Conjunto fechado | Total | Cobertura |
|---|---|---|
| ToolSystems concretas | 11 | 13 dossiês (10 tools + economia-UI + cidade-UI + sweep) |
| Tipos ISerializable (estado persistido) | 555 | 558 linhas classificadas: **31 AUTHORED**, 80 DERIVED, 323 EMERGENT, 109 STATIC, 15 META |
| TriggerBindings de UI | 196 | 100% classificados MUTA-MUNDO / SÓ-LEITURA / LOCAL-COSMÉTICO |

Dos 31 AUTHORED, **25 têm syncPath** no mod hoje; os 6 sem syncPath estão na lista P2/P3 abaixo
(maioria é configuração de criação de cidade, provavelmente imutável em runtime — verificar).

## Estado por mecânica

| Mecânica (dossiê) | Estado do sync hoje | Pior gap aberto |
|---|---|---|
| net | **AtomicBatch validado na tela** (rua+terreno) | BuildOrder por-processo não sincado → cascata em zonas (P0); upgrade same-frame sem retry |
| zone | Paint syncado por nome; **diverge na tela** | Duas causas codificadas (ver abaixo) (P0) |
| area | Extractor-scoped, host-authority | Fazenda: RNG `DateTime.Now.Ticks` no AreaSpawn/AreaConnection + decoração nunca sincada (P0) |
| object | SyncId + first-touch identity | `Recent` não replicado (reembolso 0); anexo a edge por proximidade (P1) |
| upgrade | place/move/disable cobertos | **Deletar extensão pelo painel NUNCA detectado** (DeleteDetector exclui Owner) (P0) |
| bulldoze | Identidade p/ novos, proximidade p/ save | NodeReduction não roda no receptor; delete AtomicBatch sem retry (P1) |
| terrain | Comando só Height, 1 stroke/12 frames | TerraformingTarget/Start/Angle/brush ignorados → Ore/Oil/Slope errados (P1); sem hash no radar |
| water | Place/move/delete por proximidade | ~~`m_Modifier=0` = fonte morta~~ **CORRIGIDO 06/07**; editar raio/altura sem mover nunca sinca (P1) |
| route | Create/reroute/delete + número corrigido | WorkRoute invisível ao detector; VehicleModel diverge persistido (P1) |
| default-selection | Provado: não muta mundo, EXCETO ServiceDistrict | **ServiceDistrict (distritos de um prédio de serviço) = zero cobertura** (P0) |
| ui-economy | Tax/fee/budget/loan/policy cobertos | Radar sem BudgetHash/LoanHash; política de distrito/extensão por proximidade (P1) |
| ui-city | Rename/devtree/tiles/milestone espelhados | ServiceDistrictLink (removeDistrict do painel) sem comando (P0, mesmo do acima) |
| ui-sweep | 196 triggers classificados | chirper.addLike (Chirp é persistido!); SelectVehicles/VehicleModel sem comando (P2) |

## GAPS consolidados por prioridade

**P0 — visível na tela / divergência permanente de estado autorado:**
1. **Zonas divergem** — DUAS causas com código citado:
   (a) *radar mente + join corrompe*: `Cell.m_Zone.m_Index` é índice por-boot nunca remapeado
   (`Prefabs/ZoneSystem.cs:131-219`), serializado cru no save (`Zones/Cell.cs:22-43`) que o join
   transfere sem tradução (`CS2M/Helpers/SaveLoadHelper.cs:201-287`); e o radar folda esse índice
   cru (`CS2M/Sync/StateHashSystems.cs:517`).
   (b) *derivação diverge de verdade*: `Net.BuildOrder` (contador por-processo,
   `GenerateEdgesSystem.cs:2093`, não sincado no apply — decisão em `NetBatchApplySystem.cs:417-419`)
   → `Zones.BuildOrder` → prioridade de overlap (`CellOverlapJobs.cs:245-540`), com desempate final
   em `Entity.Index` cru (`Zones/CellCheckHelpers.cs:38-43`, **auditado por Fable**) → bloco muda de
   forma sem mudar contagem. Instrumento discriminador: dump por célula com NOME nos 2 lados.
2. **Fazenda** — RNG raiz `DateTime.Now.Ticks` por processo (`Common/RandomSeed.cs:8`) alimenta
   `AreaSpawnSystem`/`AreaConnectionSystem`; decoração (fileiras/fardos) nunca é sincada e a
   supressão (`AreaSpawnSuppressSystem`) está env-gated e não repõe conteúdo.
3. **Deletar extensão de prédio pelo painel** — nunca detectado (`DeleteDetectorSystem.cs:38-57,79-99`
   excluem Owner; extensão sempre tem Owner). Precisa query espelho restrita a ServiceUpgradeData.
4. **ServiceDistrict** — atribuir distritos a um prédio de serviço: zero cobertura (buffer
   persistido `Areas/ServiceDistrict.cs:10` guarda Entity cru; precisa comando por identidade).
5. ~~Água remota nasce morta (`m_Modifier=0`)~~ — **corrigido 06/07** (`WaterApplySystem.cs`, +1 linha).

**P1 — divergência real mas menos visível / com janela:** terrain (Target/Slope/Angle/brush + sem
hash no radar), water edit-sem-mover não detectado + id por proximidade, WorkRoute invisível,
VehicleModel persistido divergente, `Recent` (reembolso), upgrade same-frame sem retry, radar sem
BudgetHash/LoanHash, política de distrito/extensão só por proximidade, `TaxApplySystem` sem try/catch.

**P2 — cosmético/raro/config:** chirp likes, RouteNumber com janela de 3 frames, echo-hash de rota
ignora Y, `CityConfigurationSystem` (defaultTheme/leftHandTraffic), `PlanetarySystem`
lat/long, `TimeData`, `City.m_OptionMask` (os 3 últimos = config de criação; verificar se são
editáveis em runtime — se não, moot), `TriggerType.ObjectCreated` não dispara no receptor,
`EditorContainer` (Editor de Mapas, fora do escopo coop).

## Arquitetura proposta (decisão pendente do Bruno)

Brainstorm 06/07: **Documento (CRDT de intenções, materializador simétrico — mata a classe de eco
por construção) + derivação-uma-vez-só (cliente suprime derivação dos domínios sincados e recebe o
derivado do host — mata a classe P0.1b/P0.2 na raiz) + cura por snapshot de domínio (serialização
do próprio jogo como /resync cirúrgico automático)**. AtomicBatch vira o materializador.
**Probe barato:** só zonas — suprimir derivação de bloco no cliente + host manda Block/Cell +
radar por nome → um teste de tela decide a arquitetura.

## Regra de processo

Nenhuma afirmação sobre o jogo entra em spec/código sem citar arquivo do decomp (`decomp/Game/`,
regerável via `ilspycmd` — ver GAME_SYSTEMS.md). Afirmações dos dossiês marcadas NÃO VERIFICADO
precisam de leitura direta antes de virarem base de fix. Auditadas por Fable até agora:
`Cell`/`ZoneType` cru (✓), `ZoneSync` manda nomes (✓), radar folda índice (✓), desempate
`Entity.Index` em `CellCheckHelpers` (✓), BuildOrder ignorado no apply (✓), `m_Modifier` ausente (✓).
