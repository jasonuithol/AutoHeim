# AutoHeim

Automation plugin for the Valheim client plus its launcher CLI. To *use* it, follow the
`valheim-client` skill (`.claude/skills/valheim-client/SKILL.md`). This file is for
changing it.

- Plugin source: `src/`. Commands are registered in the `Register*` methods of the
  partial `Commands` class (one file per family). Each command is an `IEnumerator`
  coroutine taking a `Ctx`. Set `c.Result`, `throw Ctx.Fail("...")` for expected errors,
  and `yield` to span frames.
- After editing: `tools/autoheim quit` (or `kill`), `tools/autoheim deploy` (build + copy into
  the profile; it refuses while the client runs), `tools/autoheim launch`. Verify with
  `tools/smoke_test.sh` (it launches the client if needed, and uses a throwaway server).
- Command layout: `Commands.cs` (registry, state, general), `MenuCommands.cs`, `PlayerCommands.cs`,
  `ControlCommands.cs` (movement/combat/interact), `WorldCommands.cs`, `ObserveCommands.cs`
  (detailed describers, containers, world search), `BuildCommands.cs`, `MediaCommands.cs`, `UiCommands.cs` (keys and buttons through ZInput, UI clicks, typing).
  `InputOverride.cs` owns all synthetic input; `Patches.cs` holds the unattended-client patches.
- New command: add it to a `Register*` method, add a line to `tools/smoke_test.sh`, and
  regenerate the command table in the skill from `tools/autoheim help`. Bump `PluginVersion`
  and add a `CHANGELOG.md` entry.
- Game API reference: decompile with
  `ilspycmd -p -o <dir> -r <Managed> <Managed>/assembly_valheim.dll`
  (Managed = `~/.steam/steam/steamapps/common/Valheim/valheim_Data/Managed`).
- Gotchas that cost real time (details in README "Lessons"): no `Application.*`/`QualitySettings`
  in `Plugin.Awake`; no `cond ? float : (float?)null` in coroutines; never overwrite the DLL under
  a running client; the game ignores input during the post-spawn animation; the hammer's
  placement ghost is a real object; tool clicks are timestamp fields, not input events.
- Priorities: this exists so Claude can drive clients while building and testing mods. Keep
  gameplay features (building, combat, pathing) working but don't sink time into perfecting them.
- Destructive commands (delete_*) must stay guarded by `Plugin.Isolated` / `force=true`.
- Version lives in `src/Plugin.cs` (`PluginVersion`), history in `CHANGELOG.md`. If this is ever published to
  Thunderstore, follow the global publishing checklist (manifest, CHANGELOG, namespace `tarbaby`).
