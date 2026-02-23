# Auxilium — Vertigo 2 Multiplayer Mod

A BepInEx mod that adds cooperative multiplayer to [Vertigo 2](https://store.steampowered.com/app/843390/Vertigo_2/) VR using Steam P2P networking.

Created by **misterbubb** · Licensed under [MIT](LICENSE)

> **Early Development** — Only player sync is confirmed working. Everything else is scaffolded but untested. Expect bugs.

## What Works

- Steam P2P networking (friends-only lobbies, no port forwarding)
- Full VR player sync: head, hands, body (VRIK), finger tracking
- Remote player model rendering using the game's character models
- Scene sync: clients follow the host between levels
- In-game multiplayer menu (host/join from main menu or pause menu)
- Join notifications

## Installation

1. Install [BepInEx 5.4.23.4](https://github.com/BepInEx/BepInEx/releases) (Unity Mono, x64)
2. Copy `Vertigo2Multiplayer.dll` to `Vertigo 2/BepInEx/plugins/`
3. Launch the game

## How to Play

1. Launch Vertigo 2
2. Open **MULTIPLAYER** from the main menu or pause menu
3. **Host**: Click "Host Game" — friends can join through Steam
4. **Join**: Enter the host's Steam ID or select a friend

Both players need the mod installed. The host runs the game; the client syncs to the host's session.

## Building from Source

```
git clone https://github.com/misterbubb/Auxilium.git
cd Auxilium
dotnet build -c Release
```

Output: `bin/Release/net472/Vertigo2Multiplayer.dll`

The project references DLLs from your Vertigo 2 install. If your game path differs from the default, update the `<HintPath>` entries in `Vertigo2Multiplayer.csproj`.

### Requirements

- .NET SDK (targets net472)
- Vertigo 2 installed (for game assembly references)
- BepInEx 5.x

## Project Structure

```
├── Plugin.cs                 # BepInEx entry point, initializes all systems
├── Networking/
│   ├── NetworkManager.cs     # Steam P2P connection and lobby management
│   ├── PacketBuilder.cs      # Packet serialization
│   └── PacketHandler.cs      # Packet deserialization and routing
├── Player/
│   ├── PlayerManager.cs      # Local/remote player lifecycle
│   └── RemotePlayer.cs       # Remote player model, VRIK, hand animation
├── Patches/
│   ├── MenuPatches.cs        # Main menu button injection
│   ├── PlayerPatches.cs      # Player, weapon, and world event hooks
│   ├── StoryPatches.cs       # Story progression and trigger hooks
│   └── WorldPatches.cs       # NPC dialog, vehicle, and physics hooks
├── Sync/
│   ├── EnemySync.cs          # Enemy state sync (scaffolded, untested)
│   ├── InteractableSync.cs   # Door/lever/button sync (scaffolded, untested)
│   ├── InventorySync.cs      # Weapon pickup sync (scaffolded, untested)
│   ├── StorySync.cs          # Checkpoint/timeline sync (scaffolded, untested)
│   ├── WorldSync.cs          # Vehicle/physics sync (scaffolded, untested)
│   └── RemotePlayerEntity.cs # Entity wrapper for enemy targeting (scaffolded, untested)
└── UI/
    ├── MultiplayerMenu.cs    # In-game multiplayer UI
    └── NotificationUI.cs     # Join/leave notifications
```

## Architecture

- **Host-authoritative**: The host runs the game. Clients receive state updates.
- **Star topology**: The host relays packets between clients.
- **Steam Networking Sockets**: P2P via Steam's relay network.
- Player transforms sync at ~30 Hz (unreliable channel). State changes sync reliably on change.

## Debugging

Mod output goes to `BepInEx/LogOutput.log`. Log messages are prefixed with tags like `[SYNC]`, `[RECV]`, `[MODEL]`, etc. to help filter by system.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md).
