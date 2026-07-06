# Terraformação — dossiê de sync

## 1. Entradas do jogador

O jogador seleciona um `TerraformingPrefab` (Raise/Lower, Level, Slope, Soften, Paint/Erase Material,
Paint/Erase Resource) — `Prefabs/TerraformingPrefab.cs:9-32`, que carrega `TerraformingData{ m_Type,
m_Target }` (`Prefabs/TerraformingData.cs:5-10`). `m_Type` é o enum `Shift/Level/Soften/Slope`
(`Prefabs/TerraformingType.cs:3-9`) e `m_Target` é `Height/Ore/Oil/FertileLand/GroundWater/Material`
(`Prefabs/TerraformingTarget.cs:3-12`) — **dois eixos ortogonais** do mesmo tool.

`TerrainToolSystem.TrySetPrefab` (`Tools/TerrainToolSystem.cs:328-336`) recebe o prefab via
`SetPrefab` (linhas 211-217). `UpdateActions()` (linhas 276-321) mapeia as `IProxyAction` (Raise/
Lower/Level/SetLevelTarget/Slope/SetSlopeTarget/Soften/FastSoften/PaintMaterial/EraseMaterial/
PaintResource/EraseResource — todas registradas em `OnCreate`, linhas 231-242) para
`applyActionOverride`/`secondaryApplyActionOverride` conforme `prefab.m_Type`/`m_Target`.

`OnUpdate` (`TerrainToolSystem.cs:352-409`) roda todo frame com foco no tool: enquanto o botão está
pressionado chama `Update()` (drag contínuo, linha 386/396), no press/release chama `Apply()` (linha
380/394) ou `Cancel()` (secundário, linha 384/390). Todos os três caminhos terminam em
`UpdateDefinitions(inputDeps)` (linha 571-597), que agenda o job `CreateDefinitionsJob` (linhas
32-95) via `m_ToolOutputBarrier` (linha 587,589).

`CreateDefinitionsJob.Execute()` (linhas 67-94) cria **uma única entidade por frame** com:
- `CreationDefinition{ m_Prefab = m_Brush }` — `m_Brush` é o prefab de **forma** do pincel (redondo
  liso/duro etc.), não o `TerraformingPrefab` (linha 72, 90).
- `BrushDefinition{ m_Tool, m_Line, m_Size, m_Angle, m_Strength, m_Time, m_Target, m_Start }`
  (linhas 73-91) — `m_Tool` é o `TerraformingPrefab` (linha 74); `m_Line` é o segmento arrastado
  desde o último frame (linhas 75-82); `m_Strength = ±brushStrength` do slider da UI, cru, sem
  ease-in (`m_Strength = (m_State == Removing) ? -brushStrength : brushStrength`,
  `TerrainToolSystem.cs:581`); `m_Time = UnityEngine.Time.deltaTime` (linha 582 — **tempo de quadro
  da máquina que está desenhando**); `m_Target = m_TargetPosition` (Level/Slope) ou o raycast atual
  (linha 585); `m_Start = m_ApplyPosition` (o ponto onde o botão foi pressionado, usado só por
  Slope — linha 586, alimentado em `Apply()` linha 480).
- `Updated` (linha 92).

## 2. Fluxo de aplicação

```
ToolUpdate (SystemOrder.cs:707 — TerrainToolSystem)
  └─ CreateDefinitionsJob cria 1 entidade CreationDefinition+BrushDefinition+Updated/frame
     flush via ToolOutputBarrier no fim de ToolUpdate (SystemOrder.cs:692,694)

Modification1 (SystemOrder.cs:101 — GenerateBrushesSystem)
  └─ Query (CreationDefinition & Updated, Any BrushDefinition) — GenerateBrushesSystem.cs:145-154
  └─ Para cada definição: resolve `TerraformingData` do `brushDefinition.m_Tool`
     (linha 70: se `m_Target == None` marca TempFlags.Cancel)
  └─ Subdivide a linha em `num3 = 1 + floor(length / (size*0.25))` estampas
     (linha 76) — pincel arrastado rápido gera VÁRIAS entidades `Brush` no MESMO frame
  └─ Para cada estampa cria uma entidade do arquétipo `BrushData.m_Archetype`
     (definido em `BrushPrefab.RefreshArchetype`, `Prefabs/BrushPrefab.cs:36-52`, contém
     `Brush`+`Created`+`Updated`) com:
       Brush{ m_Tool, m_Angle, m_Size,
              m_Strength = (str²)²·(1-frac) + str²·frac  · sign(str)   [ease-in por sub-passo,
                                                                          linha 82, frac=m_Time/num3]
              m_Opacity = 1/num3, m_Target, m_Start,
              m_Position = ponto ao longo de m_Line (linha 88) }
       + Temp{ Create/Select/Delete + Essential } (a menos que CreationFlags.Permanent)
     flush via ModificationBarrier1 no fim de Modification1 (SystemOrder.cs:78,86)

ApplyTool (SystemOrder.cs:717 — ApplyBrushesSystem)
  └─ Query (Temp & Brush) — ApplyBrushesSystem.cs:239
  └─ Para cada Brush: resolve TerraformingData do brush.m_Tool e despacha por m_Target
     (switch, linhas 269-289):
       Height       → ApplyHeight → TerrainSystem.ApplyBrush (GPU compute, ver abaixo)
       Material     → TerrainMaterialSystem.GetOrAddMaterialIndex (linha 302-305)
       Ore/Oil/
       FertileLand  → ApplyCellMapBrush(NaturalResourceSystem, NaturalResourcesModifier) (271-279)
       GroundWater  → ApplyCellMapBrush(GroundWaterSystem, GroundWaterModifier) (280-282)
  └─ AddComponent(Applied+Deleted) em TODAS as entidades processadas (linha 225,240,291)

Cleanup (fim do frame, GAME_SYSTEMS.md §1) — apaga Deleted, remove tags Created/Updated/Applied/Temp
```

Caso `Height`: `ApplyHeight` (`ApplyBrushesSystem.cs:307-319`) chama
`TerrainSystem.ApplyBrush(type, bounds, brush, brushPrefab.m_Texture)` (`Simulation/TerrainSystem.cs:
3918-3925`), que chama `ApplyToTerrain` duas vezes (playable + world map, linhas 3921-3922) e depois
`UpdateMinMax`+`TriggerAsyncChange`. `ApplyToTerrain` (linhas 3693-3911) **não mexe em ECS** — monta
um `CommandBuffer`, seta parâmetros do compute shader `m_AdjustTerrainCS` (`_Heightmap`,
`_BrushTexture`, `_WorldTexture`, `_WaterTexture`, `_BrushData`, `_Range` etc., linhas 3894-3904) e
dá `DispatchCompute` (linha 3905) sobre a `RenderTexture` `m_Heightmap`/`m_WorldMapEditable` — a
altura é escrita **na GPU**, nunca por um `IComponentData` por-célula.

## 3. Estado persistido tocado

| O quê | Onde mora | Persistência |
|---|---|---|
| Altura do terreno (playable + world map) | `TerrainSystem.m_Heightmap` / `m_WorldMapEditable` — `RenderTexture` `R16_UNorm`, 4096×4096 (`kDefaultHeightmapWidth`/`Height`, `TerrainSystem.cs:2270,2272`), mapa jogável 14336×14336 (`kDefaultMapSize`, linha 2276) | `TerrainSystem` implementa `ISerializable` (classe, `TerrainSystem.cs:40`). `Serialize` (linhas 3038-3048) chama `SerializeHeightmap` (linhas 3019-3036) para `worldHeightmap` e `m_Heightmap`: faz `AsyncGPUReadback...WaitForCompletion()` (readback síncrono GPU→CPU, linha 3030), filtra/comprime (`NativeCompression.FilterDataBeforeWrite`, linha 3032) e escreve o buffer `ushort` inteiro. **Não existe componente `ISerializable` por-célula** — é um blob único de sistema. |
| Material de superfície (pintura) | índice em `TerrainMaterialSystem` (`GetOrAddMaterialIndex`, `ApplyBrushesSystem.cs:302-305`) | `TerrainMaterialSystem : IDefaultSerializable, ISerializable, IPostDeserialize` (`Rendering/TerrainMaterialSystem.cs:26`) — sistema próprio, não verifiquei o corpo do `Serialize` dele (ver NÃO VERIFICADO). |
| Ore / Oil / FertileLand | `NaturalResourceCell` em `CellMapData<NaturalResourceCell>` (`ApplyBrushesSystem.cs:30-68`) | `NaturalResourceSystem : CellMapSystem<NaturalResourceCell>, IJobSerializable, IPostDeserialize` (`Simulation/NaturalResourceSystem.cs:30`) |
| GroundWater (saturação do solo) | `GroundWater` em `CellMapData<GroundWater>` (`ApplyBrushesSystem.cs:71-85`) | `GroundWaterSystem : CellMapSystem<GroundWater>, IJobSerializable` (`Simulation/GroundWaterSystem.cs:18`) |
| `Brush` (transiente) | `Tools/Brush.cs:6-23` — `IComponentData` | **Não é persistido** — vive 1 frame como `Temp`, some no Cleanup. É só o veículo de aplicação, nunca o estado final. |

## 4. Perigos cross-machine

1. **O delta de altura depende do `Time` LOCAL da máquina, duas vezes.**
   - No emissor: `BrushDefinition.m_Time = UnityEngine.Time.deltaTime` (`TerrainToolSystem.cs:582`)
     molda o ease-in por sub-passo de `Brush.m_Strength` em `GenerateBrushesSystem.cs:74-82`
     (`num4 = m_Time/num3`, `m_Strength = (str²)²·(1-num4) + str²·num4`).
   - No consumo real (onde a altura de fato muda): `TerrainSystem.ApplyBrush` passa
     `UnityEngine.Time.unscaledDeltaTime` **recém-lido no momento do dispatch**
     (`TerrainSystem.cs:3921-3922`), usado em `x = delta * brush.m_Strength *
     GetTerrainAdjustmentSpeed(type) / heightScaleOffset.x` (linha 3717) — este `delta` NÃO vem do
     `Brush` componente, é lido de novo. Duas máquinas processando o **mesmo** `Brush` (mesmos
     campos) em frames com FPS diferentes aplicam magnitudes de altura DIFERENTES. Isso é nativo do
     jogo single-player (nunca foi pensado pra ser determinístico entre PCs).
   - `GetTerrainAdjustmentSpeed` é uma constante fixa por tipo (`Soften=1000, Shift=2000,
     default=4000`, linhas 2576-2584) — não é fonte de variação, só confirma que o único fator
     dinâmico é o `Time.unscaledDeltaTime`.

2. **A escrita real acontece num compute shader GPU (`m_AdjustTerrainCS`), não em ECS/Burst-CPU.**
   `ApplyToTerrain` (`TerrainSystem.cs:3693-3911`) despacha `DispatchCompute` (linha 3905) sobre a
   `RenderTexture m_Heightmap`. Operações de ponto flutuante em compute shaders HLSL não têm
   garantia formal de bit-exatidão entre vendors/drivers de GPU diferentes (isso é uma
   característica geral de GPU compute, não algo que dá pra citar arquivo:linha do decomp C# — o
   `.compute` em si não está no assembly decompilado). Combinado com o perigo #1 (delta por-frame
   diferente), o heightmap de cada máquina tende a divergir por design, não por bug pontual.

3. **Sem componente por-célula para diffar.** Como a altura mora inteira numa `RenderTexture`
   (`TerrainSystem.cs` §3 acima), não há um `ISerializable` granular que o mod possa comparar/enviar
   como delta — só o blob inteiro (`SerializeHeightmap`, `TerrainSystem.cs:3019-3036`), caro demais
   para rodar a cada stroke.

4. **Sem uso de `Random`/seed** — confirmado (`grep Random` em `TerrainSystem.cs` só bate em
   `enableRandomWrite` de `RenderTexture`, nada de RNG de gameplay). O drift não vem de sorteio, só
   de tempo de quadro + GPU.

5. **`GenerateBrushesJob` e o job de `ApplyCellMapBrushJob` são `[BurstCompile]`**
   (`GenerateBrushesSystem.cs:19`, `ApplyBrushesSystem.cs:87`) — não verifiquei se código Burst
   Nesse projeto é bit-idêntico entre CPUs diferentes (ver NÃO VERIFICADO); é um risco genérico já
   conhecido do projeto, não exclusivo de terreno.

## 5. O que o CS2M faz hoje

O mod **não replica a `BrushDefinition`/`CreationDefinition`** — ele lê o estado transiente e chama
`TerrainSystem.ApplyBrush` diretamente no receptor (comentado explicitamente como "melhor esforço"):

- **`CS2M/Sync/TerrainDetectorSystem.cs`** — poll a ~5 Hz (`_throttle < 12`, linhas 42-47) na query
  `EntityQuery(BrushDefinition)` (linha 30). Para a primeira entidade encontrada (linha 73: "one
  stroke sample per tick"), lê `bd.m_Strength` (CRU, sem ease-in — linha 54,70), `bd.m_Size`, e
  `type = TerraformingData.m_Type` de `bd.m_Tool` (linhas 61-64) — **nunca lê `.m_Target`**. Envia
  `TerrainCommand{ Type, PosX/Y/Z = bd.m_Target, Size, Strength }` via `Command.SendToAll` (linhas
  67-72).
- **`CS2M/Commands/Data/Game/TerrainCommand.cs`** — payload com `Type, PosX, PosY, PosZ, Size,
  Strength` apenas. Classificado `SyncClass.WorldContract` em `CS2M/Sync/SyncContract.cs:66` e
  mapeado como a cobertura única de `TerrainToolSystem` (`SyncContract.cs:117`).
- **`CS2M/Commands/Handler/Game/TerrainSyncHandler.cs`** — `Handle` só enfileira em
  `RemoteTerrainQueue` (linha 16).
- **`CS2M/Sync/RemoteTerrainQueue.cs`** — fila `ConcurrentQueue<TerrainCommand>` simples.
- **`CS2M/Sync/TerrainApplySystem.cs`** — drena até 3/frame (linha 59) e descarta backlog acima de
  30 (linhas 47-51, comentado como fix v50 da "torre de terreno": strokes empilhavam com o jogo
  pausado e todas caíam no primeiro frame despausado, cada `ApplyBrush` escalando com tempo de
  quadro → montanha instantânea). `ApplyOne` (linhas 66-86) monta um `Brush` com
  `m_Position = m_Target = m_Start = pos` (MESMA posição pros três campos, linhas 72-74),
  `m_Angle = 0f` (hardcoded, linha 75), textura de pincel = `Texture2D.whiteTexture` uniforme
  (linha 32, comentário linhas 30-32) e chama `_terrain.ApplyBrush((TerraformingType)cmd.Type, area,
  brush, _brushTex)` (linha 84) — **sempre** o caminho de `Height`.
- **Nada em `StateHashSystems.cs`/`StateHashCommand.cs`** cobre terreno — ver GAP #6 abaixo.

## 6. GAPS e recomendação

1. **`TerraformingTarget` nunca é lido nem despachado — Material/Ore/Oil/FertileLand/GroundWater
   viram edição de ALTURA no receptor.** `TerrainDetectorSystem` só olha `.m_Type`
   (`TerrainDetectorSystem.cs:60-64`); `TerrainApplySystem.ApplyOne` sempre chama
   `TerrainSystem.ApplyBrush` (`TerrainApplySystem.cs:84`), que no jogo só é chamado pelo caso
   `TerraformingTarget.Height` (`ApplyBrushesSystem.cs:283-285`). Se o jogador estiver pintando
   fertilidade/minério/petróleo/material no host, o cliente aplica um brush de altura sem sentido
   E a edição real nunca chega no cliente. **Checklist**: adicionar `Target` (int) no
   `TerrainCommand`; no receptor, despachar pelo mesmo switch de `ApplyBrushesSystem.cs:269-289`
   (chamando `ApplyCellMapBrush`-equivalente ou `TerrainMaterialSystem` conforme o alvo, não só
   `TerrainSystem.ApplyBrush`).
2. **Slope é estruturalmente quebrado no fio.** `Brush.m_Start` (ponto inicial da inclinação,
   distinto de `m_Target`) não é enviado; `TerrainApplySystem` usa a MESMA posição pros três campos
   (`TerrainApplySystem.cs:72-74`) — o vetor de direção `brush.m_Target - brush.m_Start`
   (`TerrainSystem.cs:3839`) fica zero no receptor. **Checklist**: enviar `StartX/Y/Z` separado de
   `PosX/Y/Z` no `TerrainCommand` (a origem já tem os dois: `BrushDefinition.m_Start`/`m_Target`,
   `TerrainToolSystem.cs:87-88`).
3. **Forma/falloff do pincel descartada.** `TerrainApplySystem` usa `Texture2D.whiteTexture`
   uniforme (linha 32) em vez de resolver o `BrushPrefab` (identidade por nome, não índice — lei do
   projeto) e sua `m_Texture` (`Prefabs/BrushPrefab.cs:13`). **Checklist**: enviar o nome do
   `BrushPrefab` selecionado e resolver via `PrefabSystem` no receptor.
4. **Ângulo do pincel descartado.** `Brush.m_Angle` alimenta a rotação do footprint no shader
   (`quaternion.RotateY(m_Brush.m_Angle)`, `TerrainSystem.cs:3822`) mas `TerrainCommand` não tem
   campo `Angle`; `TerrainApplySystem.cs:75` hardcoda `0f`. **Checklist**: adicionar `Angle`.
5. **Valor de força enviado ≠ valor realmente aplicado localmente.** O detector manda
   `BrushDefinition.m_Strength` CRU (`TerrainDetectorSystem.cs:70`, fonte:
   `TerrainToolSystem.cs:581`), mas o que de fato mudou o terreno do EMISSOR foi o
   `Brush.m_Strength` já com ease-in por sub-passo (`GenerateBrushesSystem.cs:74-82`). Some a isso a
   amostragem de 1 stroke a cada ~12 frames (`TerrainDetectorSystem.cs:42-44,73`) enquanto
   localmente cada frame do arrasto gera 1+ sub-passos — o receptor vê uma versão muito mais grossa
   e com números diferentes do que realmente aconteceu no emissor. **Checklist**: ou (a) enviar cada
   `Brush` real pós-`GenerateBrushesSystem` (não a `BrushDefinition` crua, e não 1/12 frames), ou (b)
   calcular no emissor o delta de altura já pronto (multiplicando por `Time` local do emissor) e
   mandar um "aplicar X metros absolutos" que ignore o `Time.unscaledDeltaTime` do receptor
   (`TerrainSystem.cs:3717,3921-3922` — hoje recalculado local a cada `ApplyBrush`).
6. **Terreno é invisível para o detector de divergência (StateHash), apesar de classificado
   `WorldContract`.** O comentário do próprio contrato diz que todo `WorldContract` "must be
   reflected in the StateHash contract" (`CS2M/Sync/SyncContract.cs:27-29`), e `TerrainCommand` é
   `WorldContract` (linha 66) — mas `HashBundle`/`StateHashCommand` não têm nenhum campo de altura/
   terreno (`CS2M/Sync/StateHashSystems.cs:30-51`, `CS2M/Commands/Data/Game/StateHashCommand.cs:
   17-56`), e `SyncContract.Verify()` não checa essa invariante (só classificação/handler/cobertura
   de tool, `SyncContract.cs:134-187`) — não há teste automático que pegue esse buraco.
   **Checklist**: acrescentar um fingerprint de terreno ao `HashBundle` (ex.: soma comutativa de uma
   grade de amostras de `TerrainSystem.GetHeightData()`/`m_TerrainMinMax`, seguindo o padrão de
   "hash por posição, ordem-independente" já usado pelas outras entradas, linhas 24-28 do comentário
   de `StateHashSystems.cs`), para que drift persistente pelo menos ACENDA o alerta de sync — hoje só
   o `/resync` manual (recarga total via `WorldTransferCommand`, que reusa o mesmo
   `TerrainSystem.Serialize/Deserialize` do save, `TerrainSystem.cs:3038-3124`) corrige de fato.
7. **Custo estrutural do blob inteiro.** Como não há componente por-célula (§3), qualquer correção
   fina de drift (fora do `/resync` total) exigiria transferir um PATCH retangular do heightmap
   (a `Bounds2 area` já é calculada em `ApplyBrushesSystem.cs:309`/`TerrainSystem.cs:3746`) em vez de
   tentar re-simular o stroke — mais caro por evento, mas exato, ao contrário do replay atual.

## 7. NÃO VERIFICADO

- Conteúdo do `.compute` (`m_AdjustTerrainCS`) em si — não está no assembly C# decompilado, só os
  parâmetros que o `CommandBuffer` seta (`TerrainSystem.cs:3894-3904`). Não pude confirmar quais
  operações HLSL específicas rodam nem se usam FMA/aproximações que variam por GPU.
- Se o código Burst de `GenerateBrushesJob`/`ApplyCellMapBrushJob` produz bit-idêntico entre CPUs
  host/cliente com microarquiteturas diferentes (SSE vs AVX2 etc.) — risco genérico do projeto, não
  testei especificamente para terreno.
- Corpo do `Serialize`/`Deserialize` de `TerrainMaterialSystem`, `NaturalResourceSystem` e
  `GroundWaterSystem` — confirmei apenas as interfaces implementadas
  (`Rendering/TerrainMaterialSystem.cs:26`, `Simulation/NaturalResourceSystem.cs:30`,
  `Simulation/GroundWaterSystem.cs:18`), não li os métodos para saber se é blob inteiro ou
  por-célula.
- Comportamento exato de `math.normalize` sobre vetor zero (`brush.m_Target - brush.m_Start` quando
  ambos coincidem, GAP #2) nesta versão do Unity.Mathematics/Burst — não testei em runtime.
- Efeito visual/numérico real em jogo do fluxo Level/Slope via `TerrainCommand` (ex.: o pincel do
  receptor pode nascer na posição do alvo fixado em vez da posição atual do arrasto, já que
  `TerrainApplySystem` usa o mesmo `pos` para `m_Position`/`m_Target`/`m_Start`) — é um risco que a
  leitura do código sugere (§4 combinado com GAP #2), mas não rodei as duas simulações lado a lado
  para confirmar o resultado exato na tela.
- A ordem GLOBAL de execução das fases nomeadas do frame (`ToolUpdate`/`ClearTool`/`ApplyTool` vs.
  `Modification1..5`/`ModificationEnd`) — confirmei EM QUAL fase cada sistema está registrado
  (`Common/SystemOrder.cs:101,303,707,717`) e inferi a sequência de um único frame a partir do ciclo
  de vida de 1 frame das tags `Temp`/`Created`/`Updated`/`Applied` (GAME_SYSTEMS.md §1), mas não achei
  no decomp a lista/array literal que o engine itera para ordenar os valores de `SystemUpdatePhase`
  em tempo de execução.
