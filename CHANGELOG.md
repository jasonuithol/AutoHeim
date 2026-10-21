# Changelog

## 0.3.0 - 2026-10-21

- `key` and `button`: press a keyboard key (Unity KeyCode) or one of the game's input buttons
  (Use, Map, Inventory, Jump ...), optionally held. Injected where the game reads input (ZInput's
  GetKey/GetKeyDown/GetKeyUp and GetButton/GetButtonDown/GetButtonUp), so menus, panels and mods
  that poll keys see them, with the window unfocused.
- `click label=`: click a visible UI button by its label (lists the visible ones on a miss).
- `type text=`: type into the game's text prompt (or the focused input field) and press Enter.
- `ui`: the visible buttons, input fields and text prompt.

## 0.2.0 - 2026-10-21

- Building through the real Hammer/Hoe: `build`, `destroy`, `repair`, `build_pieces`
  (aiming, rotation steps, snapping, materials, workbench range and creator handled by the game).
- Richer observation: facing, speed, health %, stars and AI state (alerted, target, fleeing,
  sleeping, ...) for creatures; health, support, material and creator for pieces; facing for
  drops and interactables.
- Containers: `container`, `container_put`, `container_take`.
- `world_objects`: search every saved object in the world, including unloaded areas.
- `fight`: chase and attack a target every frame.
- Windows support in the launcher (Steam library discovery, Doorstop v3/v4 switches,
  Windows process handling, `tools\autoheim.cmd`).
- `server-start` / `server-stop`: throwaway vanilla dedicated server for join tests.
- Fixes: safe distant teleports (no fall death), readiness waits for the post-spawn animation,
  `quit` waits for exit, `deploy` refuses while the client runs, `interact` survives consumed
  targets, `nearby` ignores the placement ghost.

## 0.1.0 - 2026-10-21

- Initial release: isolated launcher, localhost JSON API, intro skipping, menu automation
  (characters, worlds, host/join), movement, combat, items, player/world state, console,
  chat, screenshots, audio recording, smoke test, Claude Code skill.
