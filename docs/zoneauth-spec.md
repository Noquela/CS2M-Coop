# SPEC — ZoneBlockAuthority (healer de blocos de zona, host-autoritativo)

**Por quê (evidência de 06/07, 2-sim):** com ruas idênticas, a derivação local criou blocos de
largura diferente em cada máquina (host 9x6 vs client 8x6 em 4 blocos no miolo de um loop com X —
`statediff.py` localizou; client logou `[Zone] DROP noBlock ... size=(9x6) ... the road block
diverged`). Causa raiz mapeada nos dossiês: desempate de overlap por `BuildOrder`/`Entity.Index`
(decomp `Zones/CellCheckHelpers.cs:38-43`, cascata em `docs/game-map/dossiers/net.md`). Nenhum
command-sync conserta desempate de derivação → o CLIENTE passa a receber a geometria+células
autoritativas do HOST e ajustar os blocos locais. É o probe do pilar "derivação-uma-vez" da
arquitetura nova.

**Gate:** classe estática `ZoneAuthority` com `public static bool Enabled` lendo env
`CS2M_ZONEAUTH == "1"`, cache em campo — copiar o padrão exato de `AtomicBatch`
(CS2M/Sync/NetBatchCaptureSystem.cs:22-38). Default OFF (DLL continua seguro pros amigos).

## Arquivos NOVOS

### 1. CS2M/Commands/Data/Game/ZoneBlockAuthorityCommand.cs
`public class ZoneBlockAuthorityCommand : CommandBase` — arrays paralelos, TODOS
`{ get; set; }` (padrão MessagePack do repo, igual NetBatchCommand.cs / ZonePaintCommand.cs):
- `ulong[] EdgeStartIds`, `ulong[] EdgeEndIds` — identidade da aresta dona (CS2M_NodeSyncId dos 2 nós)
- `sbyte[] Sides` — +1/-1, lado do bloco em relação à aresta (fórmula abaixo)
- `int[] Ordinals` — posição do bloco entre os blocos do MESMO lado da mesma aresta, ordenados por t (projeção do centro do bloco no eixo start→end)
- `float[] PosX, PosY, PosZ` — Block.m_Position autoritativo
- `float[] DirX, DirZ` — Block.m_Direction
- `int[] SizeX, SizeY` — Block.m_Size
- `int[] CellsOffset`, `int[] CellsCount` — janela por bloco no array achatado
- `string[] ZonePool` — nomes de zona únicos ("" = None/Unzoned)
- `int[] CellZonePool` — achatado, células de todos os blocos em row-major, valor = índice no ZonePool

### 2. CS2M/Sync/RemoteZoneBlockQueue.cs
Clone exato de RemoteZoneQueue.cs trocando o tipo (ConcurrentQueue<ZoneBlockAuthorityCommand>).

### 3. CS2M/Commands/Handler/Game/ZoneBlockAuthorityHandler.cs
Clone de NetBatchHandler.cs: `TransactionCmd = false`, log `[ZoneAuth] RECV blocks=N`, enqueue.

### 4. CS2M/Sync/ZoneBlockAuthoritySystems.cs
Contém `ZoneAuthority` (gate) + 2 sistemas:

**`ZoneBlockAuthoritySystem` (detector, roda SÓ no host):**
- Guards no OnUpdate: `!ZoneAuthority.Enabled` → return; `NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING` → return (padrão ZoneDetectorSystem.cs:53); `NetworkInterface.Instance.LocalPlayer.PlayerType != PlayerType.SERVER` → return (padrão AreaEditSystems.cs:251). `ZoneSync.EnsureBuilt(EntityManager, _prefabSystem)`.
- Cadência: contador de frames, executa a varredura a cada 240 frames.
- Query: All = {Block, Cell, Game.Common.Owner}, None = {Temp, Deleted}.
- Por bloco: `Owner.m_Owner` → precisa `Edge` (Game.Net) senão skip. Nós da aresta: `Edge.m_Start/m_End` → componente `CS2M_NodeSyncId.m_Id` de cada (sem componente → skip bloco; cobre só rua sincada, resto fica pro futuro).
- **Side/Ordinal (a MESMA função vai rodar no applier — escrever uma vez, estática):**
  `axis = endNode.m_Position.xz - startNode.m_Position.xz` (Game.Net.Node.m_Position);
  `perp = float2(axis.y, -axis.x)`;
  `side = (sbyte)(math.dot(block.m_Direction, perp) >= 0 ? 1 : -1)`;
  `t = math.dot(block.m_Position.xz - startNode.m_Position.xz, axis)` (sem normalizar — só ordena);
  agrupar blocos por (aresta, side), ordenar por t crescente, `ordinal` = índice no grupo.
  IMPORTANTE: o detector precisa agrupar TODOS os blocos da mesma aresta antes de emitir (dicionário edge→lista), porque o ordinal depende do grupo.
- Assinatura por bloco (dirty-tracking): hash simples (long, unchecked) de SizeX/SizeY + pos quantizada (0.1) + índices de zona das células. `Dictionary<Entity, long> _lastSig`; envia só os que mudaram/nasceram. Primeira varredura pós-PLAYING → tudo muda → heal de join de graça.
- Cap de 256 blocos por comando; sobrou → próxima varredura pega (sig só atualiza pros enviados).
- Envio: `Command.SendToAll?.Invoke(cmd)` (padrão ZoneDetectorSystem.cs:114). Log `[ZoneAuth] SEND blocks=N (sweep=M)`.

**`ZoneBlockAuthorityApplySystem` (applier, roda SÓ no client):**
- Guards: `!ZoneAuthority.Enabled` → return; PlayerType == SERVER → return; PlayerStatus != PLAYING → return.
- Drena 1 comando por frame de RemoteZoneBlockQueue. Comando com aresta não-resolvida → re-enfileirar com contador (campo int no wrapper local, máx ~20 tentativas ≈ retry por posições da fila) → depois DROP logado `[ZoneAuth] DROP edge unresolved`.
- Resolver aresta: copiar `FindEdgeById` de NetEditApplySystem.cs:273-305 (TryResolve dos 2 ids + walk de ConnectedEdge).
- Blocos do client daquela aresta: buffer `Game.Zones.SubBlock` da aresta (HasBuffer guard). Calcular (side, ordinal) com A MESMA função estática do detector. Match exato (side, ordinal); sem match → fallback: mesmo side, menor |t| de diferença; sem candidato → log `[ZoneAuth] MISS`.
- Idempotência: se Size igual E células (índices de zona via ZoneSync.Index(nome)) iguais → skip silencioso.
- **Heal:**
  1. `EntityManager.SetComponentData(target, new Block{ m_Position, m_Direction, m_Size })` com os valores autoritativos;
  2. Buffer Cell: `ResizeUninitialized(SizeX*SizeY)`; cada célula = `new Cell{ m_State = default, m_Zone = new ZoneType{ m_Index = ZoneSync.Index(pool[cellZone]) }, m_Height = short.MaxValue }` — flags/altura o CellCheckSystem local recomputa no Updated (é o pipeline normal dele);
  3. `EntityManager.AddComponent<Updated>(target)` se não tiver;
  4. `ZoneEcho.Mark(target)` + `ZoneSync.Snapshot[target] = <ushort[] com os índices recém-escritos>` (anti-eco, padrão ZoneDetectorSystem.cs:79-86);
  5. Log `[ZoneAuth] HEAL block=({x:F0},{z:F0}) {oldW}x{oldH}->{newW}x{newH} cells=N`.
- **Detector de oscilação (o dado que o probe existe pra colher):** `Dictionary<Entity,int> _healCount`; heal repetido no mesmo bloco com assinatura local mudando de volta → a partir do 5º: `CS2M.Log.Warn("[ZoneAuth] OSCILLA block=... heals=N — jogo local re-deriva por cima")`.

## EDITS (cirúrgicos, nada além disso)
1. **CS2M/Mod.cs** (~linha 228, junto dos outros de zona):
   `updateSystem.UpdateBefore<ZoneBlockAuthoritySystem>(SystemUpdatePhase.ModificationEnd);`
   `updateSystem.UpdateAt<ZoneBlockAuthorityApplySystem>(SystemUpdatePhase.Modification5);`
2. **CS2M/Sync/SyncContract.cs**: no Manifest, `{ "ZoneBlockAuthorityCommand", SyncClass.HostAuthoritative },` (é espelho de derivado do host, não ação de jogador).
3. **CS2M/Networking/LocalPlayer.cs**: `RemoteZoneBlockQueue.Clear();` ao lado do `RemoteZoneQueue.Clear();` existente.

## Critérios de aceite
- `dotnet build CS2M/CS2M.csproj -c Release` → 0 erros/0 warnings novos.
- Com env não setada: NENHUM comportamento novo (os 2 sistemas retornam no primeiro if).
- Sem tocar em nenhum outro sistema/arquivo. Comentários em inglês, estilo do repo (prefixo de log `[ZoneAuth]`).
- Contrato: selftest `Result("contract")` continua PASS (a entrada no Manifest garante).
