# Valheim_DevCommandsGuard

Valheim BepInEx plugin for Thunderstore.

## Features
- **Server-side Connection Guard** (`BepInEx/config/com.valheim.devcommandsguard.cfg`):
  - `RejectClientsWithDevCommands` (`true` by default): Rejects **non-admin** clients launched with `-console` or with `devcommands` active. Server admins (`adminlist.txt`) are never rejected.
  - `RejectClientsWithoutPlugin` (`true` by default): Rejects **non-admin** clients connecting without `Valheim_DevCommandsGuard` installed.
- **Guarded Commands** (`[Guards]` section — all `true` by default):
  - `GuardConsoleArg` (`-console` launch argument)
  - `GuardDevCommands` (`devcommands` toggle)
  - `GuardGod` (`god` mode)
  - `GuardDebugMode` (`debugmode`)
  - `GuardFly` (`fly` mode / debug fly keybind)
  - `GuardSpawn` (`spawn` command)
  - `GuardTp` (`tp` / `goto` command)
- **Configurable Highlight & Red Tag Visibility** (`[Visibility]` section):
  - `HighlightVisibility` (`Admin` by default): Controls who sees the 3D red character highlight (`All`, `Admin`, `Disabled`).
  - `TagVisibility` (`All` by default): Controls who sees the red tag on overhead player names and in game chat messages (`All`, `Admin`, `Disabled`).
  - `TagFormat` (`"[DEV] {name}"` by default) and `TagColor` (`"red"` by default).
- **Server Console & Game Chat Event Logging** (`[ChatLogs]` section):
  - Every guarded event (`-console` join, or any user/admin enabling, disabling, or executing `devcommands`, `god`, `debugmode`, `fly`, `spawn`, `tp`) is logged to the **Server Console** and broadcast to **Game Chat**.
  - Each command's Game Chat log visibility is independently configurable (`All`, `Admin`, `Disabled`; all default to `Admin`):
    - `ChatLogConsoleArg`, `ChatLogDevCommands`, `ChatLogGod`, `ChatLogDebugMode`, `ChatLogFly`, `ChatLogSpawn`, `ChatLogTp`.
- **Runtime Admin Console Command (`dcg`)**:
  - Server admins can view or update settings in real time from the in-game F5 console; changes are persisted to `com.valheim.devcommandsguard.cfg` and broadcast to connected clients:
    - `dcg status` — Print current configuration.
    - `dcg reject <on|off>` — Enable/disable rejecting non-admin clients with `-console` / `devcommands` (immediately checks and disconnects violating active non-admin connections when enabled).
    - `dcg requireplugin <on|off>` — Enable/disable rejecting non-admin clients missing the plugin.
    - `dcg highlight <all|admin|disabled>` — Set 3D red model highlight visibility.
    - `dcg tagvis <all|admin|disabled>` — Set overhead and chat red tag visibility.
    - `dcg guard <all|console|devcommands|god|debugmode|fly|spawn|tp> <on|off>` — Enable or disable guarding for a command.
    - `dcg log <all|console|devcommands|god|debugmode|fly|spawn|tp> <all|admin|disabled>` — Set game chat log visibility for a guarded command.
    - `dcg tagcolor <color>` — Update and broadcast the tag color.
    - `dcg tagformat <format>` — Update and broadcast the tag format (e.g., `dcg tagformat [CHEATER] {name}`).

## Building & Packaging for Thunderstore
1. Build the project with .NET SDK (`net48`):
   ```bash
   dotnet build -c Release
   ```
2. Copy `bin/Release/net48/Valheim_DevCommandsGuard.dll` into `Thunderstore/plugins/`.
3. Ensure `256x256` `icon.png` is in `Thunderstore/`.
4. Zip the contents of `Thunderstore/` (`manifest.json`, `README.md`, `icon.png`, `plugins/Valheim_DevCommandsGuard.dll`).
