# Valheim_DevCommandsGuard

Valheim BepInEx plugin for Thunderstore.

## Features
- **Server-side Configuration** (`BepInEx/config/com.valheim.devcommandsguard.cfg`):
  - `RejectClientsWithDevCommands` (`true` by default): Rejects **non-admin** clients launched with `-console` or with `devcommands` active. Server admins (`adminlist.txt`) are never rejected.
  - `RejectClientsWithoutPlugin` (`true` by default): Rejects **non-admin** clients connecting without `Valheim_DevCommandsGuard` installed.
  - `TagFormat` (`"[DEV] {name}"` by default): Custom overhead tag format for non-admin players with `debugmode` or `god` mode enabled (`{name}` is replaced by the player name).
  - `TagColor` (`"red"` by default): Custom overhead tag color (e.g., `red`, `#FF0000`, `orange`, `yellow`) synchronized from server to all clients.
- **Client-side Reporting & Highlighting**:
  - Sends `-console` and `devcommands` status to the server during handshake and upon console command execution.
  - **Tag Name (Visible to Everyone)**: Displays the server-configured tag and color overhead for any non-admin player with `debugmode` or `god` mode enabled.
  - **3D Model Highlight (Visible to Admins Only)**: Tints the 3D character model in red only when viewed by a server admin.
- **Runtime Admin Console Command (`dcg`)**:
  - Server admins can view or update settings in real time from the in-game F5 console; changes are persisted to `com.valheim.devcommandsguard.cfg` and broadcast to connected clients:
    - `dcg status` — Print current configuration.
    - `dcg reject <on|off>` — Enable/disable rejecting non-admin clients with `-console` / `devcommands` (immediately checks and disconnects violating active non-admin connections when enabled).
    - `dcg requireplugin <on|off>` — Enable/disable rejecting non-admin clients missing the plugin (immediately checks and disconnects active non-admin connections missing the plugin when enabled).
    - `dcg tagcolor <color>` — Update and broadcast the overhead tag color.
    - `dcg tagformat <format>` — Update and broadcast the overhead tag format (e.g., `dcg tagformat [CHEATER] {name}`).

## Building & Packaging for Thunderstore
1. Build the project with .NET SDK (`net48`):
   ```bash
   dotnet build -c Release
   ```
2. Copy `bin/Release/net48/Valheim_DevCommandsGuard.dll` into `Thunderstore/plugins/`.
3. Ensure `256x256` `icon.png` is in `Thunderstore/`.
4. Zip the contents of `Thunderstore/` (`manifest.json`, `README.md`, `icon.png`, `plugins/Valheim_DevCommandsGuard.dll`) as `JgDev-Valheim-DevCommandsGuard-{version}.zip`.
