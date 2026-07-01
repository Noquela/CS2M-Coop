# CS2M — Co-op Gameplay Sync (fork)

Fork of [CitiesSkylinesMultiplayer/CS2M](https://github.com/CitiesSkylinesMultiplayer/CS2M) that adds the
actual **gameplay synchronization** the upstream mod never implemented. Upstream connects two worlds,
transfers the save and has chat — but syncs **no gameplay**. This fork fills that in, feature by feature,
for **Cities: Skylines II 1.5.3f1**.

Target: play co-op with friends where what one player builds appears for everyone.

---

## Status (live)

| Feature | State | Notes |
|---|---|---|
| Player cursor (colored circle) | ✅ working, confirmed in-game | Overlay circle per remote player |
| Player name over cursor | ⚠️ needs UI rebuild fix | Name label needs the React UI; our UI rebuild currently drops localization (see below) |
| Object placement (buildings/props/trees/service buildings) | 🔬 needs in-game test | Detect+send+recv+resolve **confirmed via logs**; final materialization switched to direct-archetype (Option B) |
| Roads / nets | 🚧 in progress | NetTool pipeline (NetCourse/Bezier) |
| Pipes / power | 🚧 planned | Net subtypes |
| Zoning | 🚧 planned | |
| Bulldoze / move | 🚧 planned | |
| Money / milestones (level) | 🚧 planned | |
| Pause-on-join + join notice | 🚧 planned | Pause the sim for everyone while a player joins |

Legend: ✅ confirmed · 🔬 code done, awaiting 2-PC test · 🚧 in progress/planned · ⚠️ known issue.

---

## Architecture

- **Commands**: POCOs extending `CS2M.API.Commands.CommandBase` (MessagePack, attributeless via
  `BetterGraphOf` — all subclasses auto-register). Sent with `Command.SendToAll?.Invoke(cmd)`.
  Handlers extend `CommandHandler<C>` (set `TransactionCmd=false` for immediate apply), auto-discovered
  in the main `CS2M.dll` assembly. No loopback; `SenderId`/`PlayerId` is always 0 — never key by it.
- **ECS systems** live in `CS2M.Sync`, registered in `Mod.cs` via `updateSystem.UpdateAt<T>(phase)`.
  Gated on `NetworkInterface.Instance.LocalPlayer.PlayerStatus == PLAYING`.
- **Heavy logging** everywhere under prefixes `[Cursor]`, `[Place]`, `[Connect]`, so a 2-PC test log
  pinpoints exactly which stage works.

### Object placement flow
```
PC-A places building (Object/Line tool)
  → ApplyObjectsSystem tags it Game.Common.Applied (1 frame)
  → PlacementDetectorSystem (query on Applied, Anarchy's verified AnarchyPlopSystem query)
       reads PrefabRef→PrefabID + Transform + PseudoRandomSeed → ObjectPlaceCommand → SendToAll
── network ──
PC-B ObjectPlaceHandler → RemotePlacementQueue (thread-safe)
  → RemotePlacementApplySystem (main thread): PrefabID→prefab entity,
       CreateEntity()+SetArchetype(ObjectData.m_Archetype), set Transform/PrefabRef/PseudoRandomSeed,
       tag CS2M_RemotePlaced (echo guard). Archetype carries Created+Updated → post-processing spawns it.
```

**Why direct-archetype (Option B):** the first test showed the definition-based approach (Option A:
feed `{CreationDefinition(Permanent),ObjectDefinition,Updated}` to the standing `GenerateObjectsSystem`)
logged `COMMIT-DEF` but the object never appeared (`APPEARED-MISS`) — a frame-ordering problem where our
cleanup destroyed the definition before `GenerateObjectsSystem` consumed it. Option B builds the entity
ourselves from the prefab's baked archetype, fully synchronous, no timing/duplicate risk.

---

## Build

Needs the official CS2 modding toolchain env vars (`CSII_*`) set to point at the game's
`.ModdingToolchain`. Then:

```bash
# C# only (keeps the working original UI .mjs — our UI rebuild currently breaks localization):
dotnet build CS2M/CS2M.csproj -c Release -p:AssemblyVersion=1.0.N.0 -p:FileVersion=1.0.N.0 -p:Version=1.0.N.0
# deploy CS2M.dll + CS2M.API.dll + CS2M.BaseGame.dll into  <userdata>/Mods/CS2M/
```

Both players must run the **same version** (the mod's precondition blocks mismatched versions).

### ⚠️ Known build issue: UI rebuild drops localization
The original mod's `CS2M.mjs` (16155 bytes) has working localized text. Rebuilding the UI with our
webpack setup produces a smaller `.mjs` (~14586 bytes) whose menus render **blank labels** — the game
loads the lang JSON but our rebuilt bundle doesn't resolve the keys. **Workaround in use:** ship the
**original** `CS2M.mjs`/`.css`/`lang` and only rebuild the C# DLL. Downside: features that need new UI
(cursor name label, join notice, player list) are blocked until the UI build is fixed. TODO: diff the
original vs rebuilt bundle / align the webpack+localization setup with the official toolchain.

---

## Debug checklist (2-PC session)

Both PCs on the **same version**, connected (Host from in-game, Join from main menu, port 1111).
Grep `CS2M.log` for `[Place]`:

**Sender (placer):** `DETECT+RESOLVE-OK` → `SEND` — placement detected & broadcast.
**Receiver:** `RECV` → `APPLIED name=… entity=… hasBuilding=True` — object materialized.

Failure signposts:
- `RESOLVE-FAIL` — prefab id didn't resolve on the receiver (different DLCs/mods/assets between PCs).
- `APPLY-FAIL … no ObjectData` — prefab isn't a placeable object.
- `APPLIED … hasBuilding=False` for a building prefab — archetype didn't include building components.
- No `APPLIED` after `RECV` — creation threw; check the exception right after in the log.

If the building appears but looks wrong (no mesh / not connected / floating), that's a post-processing
gap — note it; likely needs `Surface`/sub-object wiring added to the apply step.
