# Contributing to Auxilium

## Getting Started

1. Fork and clone the repo
2. Update game assembly paths in `Vertigo2Multiplayer.csproj` to match your Vertigo 2 install
3. Build: `dotnet build -c Release`
4. Deploy: copy `bin/Release/net472/Vertigo2Multiplayer.dll` to `Vertigo 2/BepInEx/plugins/`
5. Close the game before copying (the DLL is locked while running)

## Current State

Only **player sync** is confirmed working (transforms, VRIK, hand animation, finger tracking, scene sync). The `Sync/` folder contains scaffolded code for enemies, interactables, inventory, story, and world sync — none of it has been tested. Contributions to get those systems working are welcome.

## Code Style

- C# conventions: `PascalCase` public members, `_camelCase` private fields
- XML doc comments on public methods
- Use `Plugin.Log.LogInfo/LogWarning/LogError` for logging
- Prefix log messages with a tag like `[SYNC]`, `[RECV]`, etc. (see README for the full list)

## How the Mod is Structured

### Networking Layer (`Networking/`)

All multiplayer communication goes through three files:

- `NetworkManager.cs` — Steam lobby creation/joining, P2P send/receive loop
- `PacketBuilder.cs` — Serializes data into `byte[]` packets. Each method corresponds to a `PacketType`.
- `PacketHandler.cs` — Deserializes incoming packets and routes them to the right handler. The `PacketType` enum lives here.

### Harmony Patches (`Patches/`)

Patches hook into the game's methods to detect events (player moved, door opened, weapon fired, etc.) and trigger network sync. When adding or modifying patches:

- Always verify the target method exists in the game's `Assembly-CSharp` — decompile it or check the decompiled sources
- Parameter names in `[HarmonyPatch]` prefixes/postfixes must match the actual method signature exactly
- Test that `_harmony.PatchAll()` succeeds by checking `BepInEx/LogOutput.log` for `HarmonyX` errors on startup

### Sync Systems (`Sync/`)

Each sync system is a `MonoBehaviour` singleton instantiated in `Plugin.InitGameObjects()`. They receive data from `PacketHandler` and apply it to the game world.

### Adding a New Sync Feature

1. Add a `PacketType` entry in `PacketHandler.cs`
2. Add a builder method in `PacketBuilder.cs`
3. Add a handler in `PacketHandler.cs` and register it in `RegisterDefaultHandlers()`
4. Add a Harmony patch in `Patches/` to detect the game event
5. Add or update a sync class in `Sync/` to apply received state

## Testing

1. Build the mod
2. Close the game
3. Copy the DLL to `Vertigo 2/BepInEx/plugins/`
4. Launch two game instances (two Steam accounts required)
5. One hosts, one joins
6. Check `BepInEx/LogOutput.log` for debug output

## Known Issues

- Some Harmony patches in `WorldPatches.cs` and `StoryPatches.cs` target the same methods (e.g., `Dialogarithm_Player.Play`, `Elevator.CallElevator`). These duplicate patches should be consolidated.
- The `Sync/` systems are instantiated on startup even though they're untested. They shouldn't cause issues since they no-op when not connected, but be aware they exist.

## Pull Requests

- Keep PRs focused on a single feature or fix
- Test with at least two connected players
- Include relevant log output if fixing a bug

## Questions

Open an issue for questions or feature discussion.
