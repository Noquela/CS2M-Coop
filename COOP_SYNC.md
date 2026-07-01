# CS2M — Co-op Gameplay Sync (fork)

Fork of [CitiesSkylinesMultiplayer/CS2M](https://github.com/CitiesSkylinesMultiplayer/CS2M) that adds the
**gameplay synchronization** the upstream mod never implemented, for **Cities: Skylines II 1.5.3f1**.
Upstream connects two worlds, transfers the save and has chat — but syncs no gameplay. This fork syncs
placements, edits, nets, zoning, economy and progression, plus a pause-on-join flow.

> Built overnight, feature by feature, compiling clean at each step with heavy logging. **Not yet
> 2‑PC tested** beyond cursor + the first object round (which reached the apply stage). The point of the
> heavy logs below is to make the debug session with your friends fast.

---

## What's installed / how to run

- **Your PC:** already deployed (latest is `1.0.17.0`), under `…/Mods/CS2M/`.
- **Friends:** `Desktop/CS2M_v17.zip` — everyone must run the **same version** (mismatched versions are
  blocked by the mod's precondition).
- **Connect:** Host from **inside a loaded city**; Join from the **main menu** (port prefilled `1111`).

---

## Feature status

| Feature | State | Command(s) | Log prefix |
|---|---|---|---|
| Player cursor (colored circle) | ✅ confirmed | PlayerCursorCommand | `[Cursor]` |
| Player name over cursor | 🟡 rebuilt in | (UI cursor-labels) | — |
| Object placement (buildings/props/trees/service) | 🔬 code done | ObjectPlaceCommand | `[Place]` |
| Cross‑PC entity id (for edits) | 🔬 code done | (SyncId on commands) | `[Id]` |
| Delete / bulldoze | 🔬 code done | DeleteCommand | `[Del]` |
| Move / relocate | 🔬 code done | MoveCommand | `[Move]` |
| Nets (roads/rails/pipes/power/fences) | 🔬 code done | NetPlaceCommand | `[Net]` |
| Zoning (paint/dezone) | 🧪 experimental | ZonePaintCommand | `[Zone]` |
| Money (city cash) | 🔬 code done | MoneySyncCommand | `[Money]` |
| Milestones / XP / unlocks | 🔬 code done | ProgressionSyncCommand | `[Prog]` |
| Pause-on-join + notice | 🔬 code done | JoinNoticeCommand | `[Join]` |
| UI localization (menu labels) | ✅ moved to C# | (LocaleSource) | `[Loc]` |

✅ confirmed · 🔬 code done, compiles, awaiting 2‑PC test · 🧪 experimental · 🟡 needs a look in-game.

**Protocol validated off-game:** `tests/protocol` round-trips every command through the exact
attributeless-MessagePack setup — **9/9 pass** (incl. the zoning `int[]`/`string[]`). Run:
`cd tests/protocol && dotnet run`.

---

## How it works (one paragraph)

Each action is a flat POCO command (`: CommandBase`, primitives only) sent via `Command.SendToAll`;
a `CommandHandler<T>` (auto-discovered, `TransactionCmd=false`) enqueues it; a `GameSystemBase` in
`CS2M.Sync` applies it on the main thread. Detectors read the game's transient tags (`Applied` for
objects/edges, `Deleted`/`Updated` for edits, `Block+Updated` diff for zoning). The receiver never goes
through the tool pipeline (no `Temp`), so it isn't charged money and isn't re-detected; a
`CS2M_RemotePlaced` tag (+ a net segment-hash + a zoning snapshot) are the echo guards. Cross-PC edits
are addressed by a synced `CS2M_SyncId` (sender allocates, ships it, both stamp it). Objects are built
from the prefab's baked `ObjectData.m_Archetype`; nets are injected as a `NetCourse` definition into
`ModificationBarrier1` for the game's Generate systems to build.

---

## Debug protocol (2‑PC session)

Both PCs on the **same version**, connected. Watch `…/Cities Skylines II/Logs/CS2M.log`. Expected
sender→receiver log flow per feature:

- **Object:** sender `[Place] DETECT+RESOLVE-OK … syncId=…` → `[Place] SEND`; receiver `[Place] RECV`
  → `[Place] APPLIED … hasBuilding=True`. If `APPLIED` prints but nothing appears / `hasBuilding=False`
  for a building, the archetype path didn't fully build it (note the prefab; likely needs extra
  sub-object/Surface wiring). If `RESOLVE-FAIL`: DLCs/mods differ between PCs.
- **Net:** sender `[Net] DETECT+SEND …` → receiver `[Net] RECV` → `[Net] INJECT … len=…`. If INJECT
  logs but no road appears, the `ModificationBarrier1` injection didn't get consumed — try again / check
  timing. `[Net] SKIP reason=remoteEcho` on the placer is normal for its own echo.
- **Delete/Move:** `[Del]/[Move] DETECT+SEND id=…` → `[Del]/[Move] APPLIED id=…`. `SKIP unresolved`
  means the target's SyncId wasn't found (only sync-placed objects have one in v1).
- **Zoning:** `[Zone] DETECT+SEND block=… cells=N` → `[Zone] APPLIED … cells=N`. `SKIP noBlock` =
  the road under that block isn't synced on the receiver yet (sync roads first).
- **Money:** host `[Money] SEND cash=…` → clients `[Money] APPLIED cash=…` (~1/s).
- **Milestones:** host `[Prog] SEND xp=…` → clients `[Prog] APPLIED xp=…`; the client's own game then
  advances the milestone.
- **Pause-on-join:** joiner `[Join] SEND joining=true` → others `[Join] PAUSED` + chat "X is joining…";
  joiner `[Join] SEND joining=false` → others `[Join] RESUMED` + chat "X joined!".

Send me both PCs' `CS2M.log` for any feature that misbehaves — the first missing line localizes it.

---

## Known limitations / fallbacks (v1)

- **UI labels:** provided from C# (`LocaleSource`) for every locale. If the connect menus are ever
  blank, restore the original bundle-localized `CS2M.mjs` from `CS2M_BACKUP_original.zip` (loses the
  cursor name label but keeps menus). The rebuilt `.mjs` (14586 B) adds the cursor name; the original
  (16155 B) bundles localization.
- **Zoning** is experimental (cell-index diff; blocks matched by world position — needs roads synced
  identically first). Flood-fill isn't perfectly reproduced (marquee/paint are).
- **Nets:** free-standing courses only — cross-PC snapping/splitting onto existing nodes and net
  delete/move are deferred (v2, need SyncId on edges).
- **Edits** (delete/move) only work on sync-placed objects (they carry `CS2M_SyncId`); native/growable
  objects need a first-touch reconciliation (deferred).
- **Determinism:** building color/variation uses the synced `PseudoRandomSeed`; growables spawned by
  each PC's own zoning simulation may differ until zoning+money+milestones keep the sims aligned.

---

## Build

Set the CS2 modding toolchain `CSII_*` env vars, then C# only (keeps the deployed `.mjs`):
`dotnet build CS2M/CS2M.csproj -c Release -p:AssemblyVersion=1.0.N.0 -p:FileVersion=1.0.N.0 -p:Version=1.0.N.0`
and copy `CS2M.dll`/`CS2M.API.dll`/`CS2M.BaseGame.dll` into `…/Mods/CS2M/`. To also rebuild the UI
(`.mjs` with cursor labels), run the UI webpack build (outputs straight into the mod folder). Both
players must run the same version.
