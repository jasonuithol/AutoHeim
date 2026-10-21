# AutoHeim

Automation plugin for the Valheim client plus its launcher CLI. To *use* it, follow the
`valheim-client` skill (`.claude/skills/valheim-client/SKILL.md`). This file is for
changing it.

- Plugin source: `src/`. Commands are registered in the `Register*` methods of the
  partial `Commands` class (one file per family). Each command is an `IEnumerator`
  coroutine taking a `Ctx`. Set `c.Result`, `throw Ctx.Fail("...")` for expected errors,
  and `yield` to span frames.
- After editing: `tools/autoheim deploy` (build + copy into the profile), then
  `tools/autoheim kill && tools/autoheim launch`. Verify with `tools/smoke_test.sh`.
- Game API reference: decompile with
  `ilspycmd -p -o <dir> -r <Managed> <Managed>/assembly_valheim.dll`
  (Managed = `~/.steam/steam/steamapps/common/Valheim/valheim_Data/Managed`).
- Do not call `Application.*`/`QualitySettings` from `Plugin.Awake` (Mono double fault).
  Avoid `cond ? float : (float?)null` in coroutines (JIT assertion). See README "Lessons".
- Destructive commands (delete_*) must stay guarded by `Plugin.Isolated` / `force=true`.
- Version lives in `src/Plugin.cs` (`PluginVersion`). If this is ever published to
  Thunderstore, follow the global publishing checklist (manifest, CHANGELOG, namespace `tarbaby`).
