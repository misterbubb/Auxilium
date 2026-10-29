# Vertigo 2 multiplayer modding plan

- Install: `D:\SteamLibrary\steamapps\common\Vertigo 2` (Steam app 843390); Unity build data reports 2021.3.45f2.
- Engine/runtime: Unity Mono, 64-bit Windows.
- Anti-cheat: none found by the local game scan.
- Loader: BepInEx is installed. Auxilium config files are present, but `BepInEx/plugins` is currently empty; the test DLL has not been installed in the game folder.
- Logs: `D:\SteamLibrary\steamapps\common\Vertigo 2\BepInEx\LogOutput.log`.
- Existing project: Auxilium, cloned locally from `https://github.com/misterbubb/Auxilium`; initial commit dated 2026-02-22. MIT license.
- Project state from README and CONTRIBUTING: player sync is the only system claimed as confirmed working; enemy, interactable, inventory, story and world synchronization are scaffolded and untested.
- Proposed route: continue the existing BepInEx 5 C# plugin and Steam P2P architecture. Build against the assemblies in this local game install. Do not deploy a build into the game until the user asks to test it.
- Current local weapon-sync implementation: join-time loadout snapshots, remote muzzle flash and shot audio, host-authoritative Shooter/Bullet projectile spawning (including selected Shooter variants and actual bullet speed), client-only visual projectiles, enemy health mirroring, and integer held-gun ammo/reload-completion updates. A 19-profile audit is in `WEAPON_SYNC_AUDIT.md`; beams, PhageLauncher, melee, grappling, several resource types, and internal weapon-specific animation still need separate synchronization paths.
- Lab: keep source changes in `work/Auxilium`; use the game install only as a local reference until deployment/testing is explicitly requested. Do not push commits or create a PR.
- Open questions: which co-op behavior should be first; whether a second player/game copy is available for network testing.

## Local inspection notes

- `um scan` identified Unity Mono, x64, game version 2021.3.45f2, BepInEx, and no anti-cheat in the install.
- `BepInEx/config/com.auxilium.vertigo2multiplayer.cfg` identifies the installed plugin GUID as `com.auxilium.vertigo2multiplayer`, version 1.0.0.
- A filtered read of the existing `BepInEx/LogOutput.log` showed repeated Unity `NullReferenceException` entries and Auxilium `[STORY]` / `[WORLD]` initialization messages. Need the relevant stack traces and a clean reproduction to attribute them.
- No save/config files were changed; no game files were written; no network push was performed.
