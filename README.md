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
<home>/instances/<name>/      (<home> = ~/.local/share/AutoHeim or %LOCALAPPDATA%\AutoHeim)
```

- **Isolation.** `autoheim launch` points Doorstop at a per-instance copy of BepInEx
  (`<home>/instances/<name>/BepInEx`), so only AutoHeim (plus whatever
  you drop in that profile's `plugins/`) loads. Characters and worlds live in
  `<home>/saves`. The Unity log goes to the instance directory, and
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

- Linux or Windows
- Valheim client with [BepInExPack_Valheim](https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/)
  installed into the game folder
- Steam running
- .NET SDK (to build) and Python 3.8+
- Optional: Valheim Dedicated Server (Steam > Library > Tools) for `autoheim server-start`

The game and dedicated-server installs are found automatically in any Steam library:
- **Windows:** the Steam path from the registry plus every library listed in
  `libraryfolders.vdf`.
- **Linux:** `~/.steam/steam`, `~/.local/share/Steam`, Debian and Flatpak Steam, plus their
  extra libraries.

Override with `VALHEIM_CLIENT_DIR` / `VALHEIM_SERVER_DIR`. `autoheim paths` shows what was found.

| | Linux | Windows |
|---|---|---|
| Run the CLI | `tools/autoheim ...` | `tools\autoheim.cmd ...` or `py tools\autoheim.py ...` |
| State, profiles, saves | `~/.local/share/AutoHeim` | `%LOCALAPPDATA%\AutoHeim` |
| How BepInEx is pointed at the profile | Doorstop env vars + `LD_PRELOAD` | Doorstop command-line switches (v3 or v4, auto-detected) |
| Smoke test | `tools/smoke_test.sh` | `PYTHON=py tools/smoke_test.sh` in Git Bash |

On Windows, `server-stop` force-ends the throwaway server: a detached server has no console
to receive Ctrl+C, so its world isn't saved on exit. That's fine for a test world.

## Usage

```bash
tools/autoheim setup                 # builds against your detected install, creates the profile
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
