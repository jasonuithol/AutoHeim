# AutoHeim

A BepInEx plugin plus a command-line launcher for fully automating the Valheim game
client, so that Claude Code (or any script) can run the game without a human:

- launch an isolated client and skip the intros
- create, delete and select characters and worlds
- host a world or join a (local) dedicated server
- move, look, jump, crouch, dodge, attack, block, fight, interact, pick up
- teleport anywhere and land safely on the ground
- equip, unequip, use, drop, give and remove items
- read player state, inventory, equipment, skills, surroundings, environment and world locations
- run console commands and chat
- take screenshots and record the game's audio

## How it works

```
tools/autoheim (Python CLI) ──HTTP/JSON──▶ AutoHeim.dll (BepInEx plugin, inside valheim.x86_64)
        │                                       ├─ HttpApi:   localhost listener thread
        │ launches with its own BepInEx         ├─ Runner:    runs each request as a main-thread coroutine
        │ profile + save dir via Doorstop       ├─ InputOverride: replaces PlayerController input with scripted controls
        ▼                                       └─ Patches:   skip intros/Valkyrie/tutorials, never save shared prefs
~/.local/share/AutoHeim/instances/<name>/
```

- **Isolation.** `autoheim launch` points Doorstop at a per-instance copy of BepInEx
  (`~/.local/share/AutoHeim/instances/<name>/BepInEx`), so only AutoHeim (plus whatever
  you drop in that profile's `plugins/`) loads. Characters and worlds live in
  `~/.local/share/AutoHeim/saves`. The Unity log goes to the instance directory, and
  writes to the shared Valheim preferences are suppressed. Your normal game, mods,
  characters and settings are untouched, and the automated client can run alongside it.
- **Dormant by default.** If the DLL is installed into a normal profile, it does nothing
  unless `AUTOHEIM_PORT` is set or `[General] Enabled = true` is configured.
- **Input.** Movement and actions are injected by replacing `PlayerController.FixedUpdate`
  with a `Player.SetControls` call while a scripted action is active. The game sees
  exactly what it would from held keys, and this works with the window unfocused.
- **Threading.** HTTP requests block on a job that runs as a coroutine on the Unity main
  thread. Long actions (loading a world, walking somewhere, recording audio) span frames
  while other requests keep being served.

## Requirements

- Valheim client with BepInExPack installed at `~/.steam/steam/steamapps/common/Valheim`
  (override with `VALHEIM_CLIENT_DIR`)
- Steam running
- .NET SDK (to build), Python 3.8+
- Optional: Valheim dedicated server for `autoheim server-start` (`VALHEIM_SERVER_DIR`)

## Usage

```bash
dotnet build -c Release              # or let `autoheim setup`/`launch` build it
tools/autoheim launch                # isolated client at the main menu
tools/autoheim create_character name=Botty
tools/autoheim create_world name=TestWorld
tools/autoheim start_world world=TestWorld character=Botty
tools/autoheim move_to x=20 z=10
tools/autoheim screenshot width=960
tools/autoheim help                  # every API command with usage
tools/autoheim quit
```

The API is also plain HTTP: `curl -s localhost:7878/player`,
`curl -s -d '{"x":10,"z":5}' localhost:7878/move_to`.

`tools/smoke_test.sh` runs every command family end to end, including joining a throwaway
dedicated server.

The full command reference, along with conventions and gotchas, lives in
[`.claude/skills/valheim-client/SKILL.md`](.claude/skills/valheim-client/SKILL.md), which
Claude Code loads as the `valheim-client` skill.

## Lessons baked into the code

- Don't touch `Application.*` or `QualitySettings` in the plugin's `Awake`: the chainloader
  runs before Unity has finished starting, and doing so double-faults Mono. Those settings
  are applied on the Runner's first frame instead.
- This game's Mono JIT asserts (`fconv_to_r8_x`) on `cond ? floatExpr : (float?)null`
  inside iterator methods. Use `Json.ROrNull` and keep heavy object-building in plain
  helper methods.
- A distant `Player.TeleportTo` parks the player at the requested height and only checks
  that a floor exists below, so without correction they fall and can die. `teleport` snaps
  them to solid ground and resets the fall tracker.
