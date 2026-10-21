---
name: valheim-client
description: Launch and drive an automated Valheim game client (AutoHeim) - start the client, create/delete/select characters and worlds, host a world or join a local dedicated server, move/teleport/fight/use items, read player state and inventory, take screenshots and record audio. Use whenever a task needs the Valheim client running or a human-free in-game check (e.g. testing a mod, reproducing a bug, verifying a server).
---

# Driving the Valheim client with AutoHeim

Everything goes through one CLI, `tools/autoheim`, at the root of the AutoHeim repo
(the repo this skill lives in). Run it from there. It prints JSON; the exit code is 0 when `"ok": true`.
On Windows use `tools\autoheim.cmd` (or `py tools\autoheim.py`) wherever `$A` appears below.

The client runs **isolated**: its own BepInEx profile
(`<home>/instances/<name>/BepInEx`), its own saves (`<home>/saves`), its own Unity log, and it never writes the
shared Valheim preferences. The human's characters/worlds/settings are not touched.
`<home>` is `~/.local/share/AutoHeim` on Linux and `%LOCALAPPDATA%\AutoHeim` on Windows;
`$A paths` prints every location, including the detected game install.

## Typical session

```bash
A=tools/autoheim                            # from the repo root
$A launch                                   # blocks until the main menu is up (~20-40s); intros are skipped
$A characters; $A worlds                    # what exists already
$A create_character name=Botty              # 3-15 chars
$A create_world name=TestWorld seed=abc     # 5-20 chars
$A start_world world=TestWorld character=Botty   # blocks until the player can act (~15-30s)
$A player                                   # position, vitals, foods, status effects, flags
$A screenshot width=960                     # returns a path; open it with the Read tool to look
$A move_to x=10 z=-20                       # blocks until arrival/timeout
$A logout                                   # back to the main menu
$A quit                                     # or: $A kill
```

Testing against a server: `$A server-start` starts a throwaway vanilla dedicated
server (own save dir, port 2476, password `secret12`) and waits until it accepts
players; then `$A join_server address=127.0.0.1:2476 password=secret12 character=Botty`.
`$A server-stop` when done. For the human's real server, ask before connecting.

Testing a mod: copy its DLL into `<home>/instances/default/BepInEx/plugins/`,
then `$A kill && $A launch`. After changing AutoHeim itself: `$A deploy` then relaunch.

## Conventions

- Arguments are `key=value`; values parse as JSON when possible (`count=3`, `run=false`).
- Coordinates: x/z are horizontal, y is up. `yaw` 0 = +z, 90 = +x. `pitch` positive looks down.
- Item arguments accept prefab names (`AxeStone`) or display names (`Stone Axe`); `give`
  needs a prefab name and lists close matches when it can't find one.
- Long commands block (`start_world`, `join_server`, `move_to`, `teleport`, `fight`,
  `record_audio`); pass `timeout=` to change their budget.
- Synthetic input works with the window unfocused; real mouse/keyboard is ignored while a
  scripted action is running. `stop` releases everything.
- To look at the game, take a `screenshot` (use `width=` 640-960 to keep it small) and Read
  the returned path. `mode=camera hud=false` renders the 3D view only (no post-processing).
- `record_audio` returns `peak`/`rms`/`silent`, so you can tell whether sound played without listening.
- `console text="..."` runs any in-game console command; cheats need `console text=devcommands` first.
- Problems: `$A status` (state + `not_ready_reason`), `$A log contains=Error`, `$A popups dismiss=true`,
  `$A tail -n 100` (BepInEx log file, survives crashes), `$A paths`.

## Gotchas

- Valheim worlds generate on first start: the first `start_world` on a new world takes longer.
- `teleport` far away waits for the area to load and then puts the player on the ground.
- `god` prevents damage but not stamina use; `heal` refills both. Attacks fail at 0 stamina.
- Observation: `nearby` gives characters (position, facing, speed, health, stars, AI: alerted/target/
  targeting_me/fleeing/sleeping), pieces (facing, health, support, material, creator), drops and
  interactables, all within the loaded area. `world_objects name=...` searches every saved object in the
  world (unloaded too) when hosting. `container` lists a chest's contents without opening it.
- Building is real tool use: `build` equips the Hammer/Hoe, selects the piece, walks into reach, aims the
  crosshair at the surface point and clicks; the game decides snapping, validity, materials and creator.
  Rotation comes in 22.5° steps; `snap=false` = holding Alt. A refusal names the game's reason (slope,
  overlapping rock/tree, no-build zone, missing workbench...); just try a nearby spot.
  Tips: build away from the spawn stones (no-build zone); flatten first with the Hoe
  (`build piece=mud_road_v2 x= z=`); fresh characters only unlock wood pieces after a workbench exists.
  `destroy` / `repair` aim at the piece and use the hammer's remove / repair modes.
  Building is a side feature for mod testing; don't burn time perfecting placements.
- `fight` targets the nearest hostile (or `name=`), chases and swings each frame. Greydwarf shamans heal allies.
- `locations` and `set_time` only work when hosting (`start_world`), not on a dedicated server.
- `delete_character` / `delete_world` refuse to run outside the isolated save dir unless `force=true`.
  Never pass force=true without the user asking.
- Several clients at once: `--instance NAME --port P` on `launch`, then `--instance NAME` on commands.

## Command reference (`$A help` prints the same, `$A help name=<cmd>` for one)

| Usage | What it does |
|---|---|
| `attack [secondary=false] [count=1] [interval=0.9] [hold=0]` | Primary or secondary attack with the current weapon. hold=seconds keeps the button held (bows, charged attacks). |
| `block [seconds=1]` | Hold block for a duration. |
| `build piece=<prefab or name> x=<x> z=<z> [y=<surface y>] [rotation=<deg>] [snap=true] [snap_point=-1] [approach=true] [timeout=30]` | Place a piece the way a player does: aim the crosshair at the surface point (x, y, z), rotate in 22.5° scroll steps, snap to nearby pieces unless snap=false (Alt), optional snap_point (Tab). Uses materials and records you as creator. |
| `build_pieces [name=<text>] [all=false]` | Pieces your build tools (Hammer, Hoe, Cultivator...) can place: unlocked?, buildable right now?, materials, station. |
| `characters` | List character profiles in the active save directory. |
| `chat text=<message> [shout=false]` | Send a chat message as the player. |
| `console text="<command line>"` | Run an in-game console command (e.g. 'devcommands', 'spawn Boar 2', 'tod 0.5'). Returns new console output. |
| `container [name=<text>] [radius=5] \| container x=<x> z=<z>` | List the contents of the nearest chest/cart/ship container (optionally matching name, or nearest to a point). |
| `container_put item=<prefab or name> [count=all] [name=<container>] [radius=5]` | Move items from your inventory into the nearest container. |
| `container_take item=<prefab or name> [count=all] [name=<container>] [radius=5]` | Move items from the nearest container into your inventory. |
| `create_character name=<name>` | Create a new character (local save, default starting items). Must be at the main menu. |
| `create_world name=<name, 5+ chars> [seed=<seed>]` | Create a new local world (random seed if omitted). Must be at the main menu. |
| `crouch [on=<bool>]` | Toggle (or set) crouch/sneak. |
| `delete_character name=<name> [force=false]` | Delete a character. Refused outside an isolated save dir unless force=true. |
| `delete_world name=<name> [force=false]` | Delete a world. Refused outside an isolated save dir unless force=true. |
| `destroy [name=<text>] [x=<x> z=<z>] [radius=3] [approach=true] [timeout=30]` | Deconstruct the nearest matching piece with the hammer (middle-click): aims at it, removes it, resources drop. |
| `dodge [forward=1] [right=0]` | Dodge roll in a direction relative to the view. |
| `drop item=<prefab or name> [count=all]` | Drop items on the ground in front of the player. |
| `emote name=<wave\|sit\|challenge\|cheer\|nonono\|thumbsup\|point\|...>` | Play an emote. |
| `env` | Day number, time of day, weather, wind, biome. |
| `equip item=<prefab or name>` | Equip an inventory item. |
| `equipment` | Currently equipped item per slot. |
| `fight [name=<text>] [radius=20] [timeout=30] [reach=2.2] [secondary=false]` | Chase and attack the nearest hostile (or nearest character matching name) every frame until it dies, it escapes, or timeout. |
| `ghost [on=true]` | Toggle ghost mode (monsters ignore you). |
| `give item=<prefab> [count=1] [quality=1]` | Add items straight into the inventory (cheat; prefab names like Wood, AxeStone, CookedMeat). |
| `god [on=true]` | Toggle god mode (no damage taken; stamina is still used, see heal). |
| `ground x=<x> z=<z>` | Terrain height at a point (only for loaded areas). |
| `heal` | Restore health and stamina to full. |
| `help [name=<cmd>]` | List commands, or show one command's usage. |
| `hotbar slot=<1-8>` | Use the item in a hotbar slot. |
| `interact [name=<text>] [radius=3] [alt=false] [hold=false]` | Interact (E) with the hovered object, or the nearest interactable whose name/prefab contains 'name'. |
| `inventory` | Inventory contents with prefab names, stacks, equipped flags and durability. |
| `join_server address=<host:port> [character=<name>] [password=] [wait=true] [timeout=240]` | Connect to a (local) dedicated server. wait=true blocks until the player is ready. |
| `jump` | Jump once. |
| `locations [name=<text>] [limit=10]` | Generated world locations (StartTemple, Vendor_BlackForest, Eikthyrnir, Crypt...) nearest first. Host only. |
| `log [after=<seq>] [max=200] [contains=<text>]` | Recent BepInEx/Unity log lines. Pass the returned 'seq' as 'after' next time to get only new lines. |
| `logout [save=true] [wait=true] [timeout=120]` | Leave the world and return to the main menu. |
| `look [yaw=<deg>] [pitch=<deg>] \| look x=<x> y=<y> z=<z> \| look turn=<deg>` | Set view direction. yaw 0=+Z(north) 90=+X(east); pitch positive looks down. Or face a world point. |
| `move [forward=1] [right=0] [seconds=1] [run=false]` | Hold movement keys relative to the look direction for a duration (forward/right in -1..1). |
| `move_to x=<x> z=<z> [run=true] [tolerance=1.5] [timeout=60] [jump_when_stuck=true]` | Walk/run to a world position (straight line with simple stuck avoidance). Blocks until arrival or timeout. |
| `nearby [radius=30] [kinds=characters,items,interactables,pieces] [name=<text>] [limit=40]` | Things around the player, nearest first. |
| `pickup [name=<text>] [radius=5] [all=true]` | Pick up item drops lying nearby. |
| `player` | Full local player state: position, look, vitals, food, status effects, flags. |
| `popups [dismiss=false]` | List visible popups/dialogs; dismiss=true closes them. |
| `quit` | Quit the game process cleanly (saves if in game). |
| `record_audio [seconds=5] [path=<file.wav>]` | Record the game's final audio mix to a 16-bit WAV. Also returns peak/RMS levels. |
| `remove_item item=<prefab or name> [count=all]` | Delete items from the inventory. |
| `repair [name=<text>] [x=<x> z=<z>] [radius=3] [approach=true] [timeout=30]` | Repair the nearest matching damaged piece with the hammer's repair tool (needs its crafting station in range). |
| `save` | Save the character (and the world when hosting). |
| `screenshot [path=<file.png\|.jpg>] [width=<px>] [mode=screen\|camera] [hud=true] [quality=85]` | Capture the game view. width downscales (keeps aspect). mode=camera renders the 3D view without UI. |
| `set_time fraction=<0..1>` | Set time of day (0=midnight, 0.25=dawn, 0.5=noon). Host only. |
| `skills` | Skill levels. |
| `start_world world=<name> [character=<name>] [open=false] [password=] [public=false] [crossplay=false] [wait=true] [timeout=240]` | Host and enter a local world with a character. wait=true blocks until the player is ready. |
| `status` | Game state (menu/loading/connecting/ingame/dead), window, network and player summary. |
| `stop` | Release all synthetic input and cancel move_to. |
| `teleport x=<x> z=<z> [y=<ground>] [timeout=60]` | Teleport to a world position and wait until the area has loaded. y defaults to ground height. |
| `unequip item=<prefab or name> \| slot=<right\|left\|chest\|legs\|helmet\|shoulder\|utility\|trinket\|ammo> \| all=true` | Unequip an item, a slot, or everything. |
| `use_item item=<prefab or name>` | Use an item as if clicked in the inventory: eat food, drink a potion, toggle equip. |
| `wait state=<menu\|ingame> [timeout=120]` | Block until the game reaches a state. 'ingame' also waits for the player to be ready (out of intro, area loaded). |
| `world_objects name=<prefab text> [radius=<m>] [x=<x> z=<z>] [limit=50]` | Search every saved object in the world (loaded or not) by prefab name: position, facing, health, creator. Covers the whole world when hosting; on a dedicated server only the area synced to this client. |
| `worlds` | List worlds in the active save directory. |
