# MODLOG — Vertigo 2 multiplayer

## 2026-10-25 — Initial reconnaissance

- User wants to work on a Vertigo 2 multiplayer mod using Auxilium and explicitly said not to push anything.
- Local game: `D:\SteamLibrary\steamapps\common\Vertigo 2`, Steam app 843390, Unity Mono 2021.3.45f2, x64.
- BepInEx is already installed; the game folder contains `BepInEx/plugins/Vertigo2Multiplayer.dll` and Auxilium config.
- Local Auxilium clone: `work/Auxilium`, upstream `https://github.com/misterbubb/Auxilium`, initial commit `3e5360abafd5cfe0c9e2a2d2065b665efbc4a546` (2026-02-22).
- Public README and CONTRIBUTING describe player sync as confirmed and enemy/interactable/inventory/story/world sync as untested scaffolding.
- Existing log contains repeated Unity `NullReferenceException`s. Exact cause is unknown; inspect full stack traces and reproduce before changing code.
- No game files or saves changed. No push, PR, or deployment performed.

## 2026-10-25 — Weapon sync, step 1: loadout snapshots

- Game code inspection (local `Assembly-CSharp.dll`, decompiled only under `work/Vertigo2-decomp/`) confirmed `EquippablesManager` exposes each hand's current profile, and Auxilium already sends changed left/right weapon profiles with `WeaponEquip`.
- Found a late-join gap: state was broadcast only when the cached loadout changed, so a newly joined peer could miss an unchanged loadout; `WeaponEquip` was also omitted from the host's reliable relay packet list.
- Added `RequestWeaponState` packet type 55. On a host-side join, Auxilium sends its own loadout directly to the new peer and asks connected clients to resend theirs. Clients answer with their current left/right profile names; the host relay distributes responses.
- Added a bounded 5-second wait for the local equippables manager to be ready before sending the join snapshot.
- Decompiled game API facts: `Vertigo2.Shooter.Shoot` creates bullets before raising static `Shooter.OnAnyShoot`; `Gun.UpdateFiring` decrements `rounds` after shooting; common ammo loading goes through `Gun.TryLoad(Ammo)`, while weapon subclasses provide their own `Load` logic.
- Firing presentation update: the existing `WeaponFire` packet now includes the muzzle position as well as direction, and the receiver creates a short-lived muzzle flash at that transmitted world position. Added a minimum packet-size check.
- Game source inspection found multiple firearm subclasses override `Gun.UpdateFiring`; ammo state is therefore polled from the currently equipped hand weapons rather than relying on a base-method Harmony hook.
- Projectile step: subscribe to `Shooter.OnInstantateBullet` for each shooter. The firing client keeps a local visual projectile with damage disabled, sends a reliable projectile packet, the host creates the damage-capable projectile from the locally installed weapon prefab, and other peers create non-damaging visual copies. Projectiles are accepted by the host only when the sender has that weapon equipped.
- Ammo/reload step: sync current/capacity ammo for held guns when it changes, send current ammo in join snapshots, and mark increases as reload completion events. The host now sends enemy health values to clients; clients mirror the public `VertigoEntity.health` field to reflect authoritative hits.
- Added `[AMMO]` and `[RELOAD]` log entries for incoming ammo and reload-completion state to make the two-player test easier to diagnose.
- Added remote weapon shot audio using the local weapon profile's gun clips. Reload hand movement remains represented by the existing VR hand transforms; weapon-specific internal reload animations are not driven on remote clones because their MonoBehaviours stay disabled.
- Bumped plugin version to 1.0.2 because the wire protocol now includes projectile and ammo packets; peers need to run matching builds.
- Build: `dotnet build work/Auxilium/Vertigo2Multiplayer.csproj -c Release` succeeded with 0 warnings and 0 errors. `git diff --check` passed. No tests or in-game run were performed. Deliverable: `outputs/Vertigo2Multiplayer-1.0.2.dll`.
- Remaining verification: two-peer in-game testing for projectile spawning, enemy damage/death mirroring, firing audio, join snapshots, reload completion, and ammo counts. Custom projectiles, weapon-specific reload animations, and boss/scene edge cases may need per-weapon fixes.

## 2026-10-25 — Weapon profile audit and projectile variants

- Scanned the installed build's 1,603 Addressables bundles and the serialized `.assets` files for all `EquippableProfile` assets; found 19 player weapon profiles. Mapped each profile's prefab component tree and Shooter projectile references. Full findings and per-profile limitations are in `WEAPON_SYNC_AUDIT.md`.
- Found that several guns have multiple `Shooter` components and select among them at runtime (Altershock, Annihilator, GordlePun, Ionizer, QuadBow, and Trident). The previous receiver always chose the first prefab Shooter, which could create the wrong projectile.
- Projectile packets now carry the firing Shooter index and actual `Bullet.speed`. This preserves the selected projectile template and the QuadBow's charge-dependent speed when the host creates the authoritative bullet.
- Audit confirmed important non-bullet paths still need separate work: PhageLauncher grenade/Rigidbody launch, Enlightenment and Annihilator continuous beams, melee hit/parry/reflection, and VoidGrappler attachment/pull. Remote weapon scripts remain disabled, so weapon-specific reload/resource visuals also need explicit replication.
- Build: `dotnet build work/Auxilium/Vertigo2Multiplayer.csproj -c Release` succeeded with 0 warnings and 0 errors. No tests or in-game run were performed. Protocol version is now 1.0.3; both players need the matching DLL.

## Next

Next step: user verification of the 1.0.8 remote rig and finger update changes with two peers. Weapon projectile work is paused; the logs show the fired PhageLauncher uses a custom grenade path outside the existing Shooter projectile hook.

## 2026-10-29 — Remote rig and hand alignment correction

- Compared the two user-provided 1.0.4 logs. `HandPose` packets were sent and routed, with no plugin exceptions. The remote equipped weapon visuals and ammo state also arrived. The host log showed PhageLauncher rounds dropping, but no projectile packets; game source confirms PhageLauncher creates a grenade directly rather than using `Shooter`.
- Decompiled `BodyAnimator.UpdateHandIK` confirms Vertigo 2 computes each wrist IK target (`BodyHand.handTarget`) from its hand collider and, while holding a weapon, blends in the active `HandSkeletonPoser` position and rotation. The previous network sender used raw `LController` / `RController` transforms, omitting the same weapon-specific wrist offset used by the local player's VRIK rig.
- Changed head/hand sync to use `BodyAnimator.handLeft.handTarget` and `handRight.handTarget` when initialized, falling back to controller transforms during startup. Added periodic controller-to-rig angle diagnostics to the log.
- Kept the 24-bone local finger pose payload, which is based on Vertigo 2's canonical 25-bone ordering and its `HandAnimator.ApplyToSkeleton` local transforms. Set `RemotePlayer` execution order late so it applies received finger bones after the body's IK pass. This should align the wrist target, weapon visual, and weapon-shaped fingers in the same coordinate chain.
- Bumped the plugin version to `1.0.5`. Build: `dotnet build work/Auxilium/Vertigo2Multiplayer.csproj -c Release` succeeded with 0 warnings and 0 errors; `git diff --check` passed. In-game verification remains necessary. Deliverable: `outputs/Vertigo2Multiplayer-1.0.5.dll`.
- Follow-up logs from both PCs show version `1.0.5`, received hand rigs initialize with 25 bones, and controller-to-game-target rotation differences are about 90–105 degrees on both sides. This confirms the hand-target offset is substantial and being captured. No invalid hand-pose packet warnings were found in the reviewed excerpts.
- Increased hand-pose transmission from 15 Hz to 30 Hz to match the transform update rate, raised finger target easing from 15 to 45 per second to reduce lag, and added periodic per-side received bone diagnostics. Version `1.0.6`; same-peer version required. Need inspect these new diagnostics after the next buddy test to see whether the left pose target/name diverges from the right.
- Completed an asset audit across all 1,603 installed Addressables bundles: all five VR character prefabs have 25-bone mirrored-left/unmirrored-right HandAnimators; the flat-screen character has no VR rig. Found 73 unique HandPose assets (25 bone positions and rotations on both sides) and 44 unique HandSkeletonPoser configurations; full findings are in `work/PLAYER_RIG_AUDIT.md`.
- Compared all five stock VRIK rig settings with `RemotePlayer.ConfigureVRIK`: the character assets use shoulder rotation weight 0.7 and twist weight 0.5 on both sides, with mirrored left/right palm axes. The mod had overwritten both shoulder values to 1.0, bypassing authored hand-side tuning. Removed those overrides so the cloned prefab keeps its native settings; added a startup log of preserved values. Bumped to `1.0.7`.
- Build of 1.0.7 succeeded with 0 warnings/errors; no in-game run yet.
- User reports 1.0.7 preserved the left-hand shape and hand poses, but the hand changed size and jittered. The 1.0.8 rotations-only attempt made the hand substantially worse and brought back left-hand mangling. Reverted that payload design.
- Version `1.0.9` restores full local position and rotation data for all 24 non-wrist bones per hand, with a two-snapshot timestamped interpolation buffer at 30 Hz. This preserves the full pose data that produced correct shapes in 1.0.7 while smoothing movement between received snapshots. Kept the stock VRIK shoulder settings and late post-IK application.
- Build and package 1.0.9 for the user to test with a buddy; do not install into the game folder. The two peers must run matching versions. In-game validation is still required.

## 2026-10-29 — Weapon-aware finger pose sync

- Replaced the active generic grip-curl send path with a `HandPose` packet carrying local positions and rotations for the 24 non-wrist bones of each hand. Samples come from Vertigo 2's live `HandAnimator` bones, after the held weapon's `HandSkeletonPoser` has shaped the fingers.
- Remote avatars retain their `HandAnimator` component to preserve the game's bone map, disable its local input-driven behavior, and blend received bone transforms after IK. Wrist placement remains controlled by the existing hand transform sync.
- Legacy grip-curl packet parsing remains available, but current builds send the new pose packet. Protocol/plugin version is `1.0.4`; both peers need the same build.
- Build: `dotnet build work/Auxilium/Vertigo2Multiplayer.csproj -c Release` succeeded with 0 warnings and 0 errors. `git diff --check` passed. No in-game test or game-folder deployment was performed. Deliverable: `outputs/Vertigo2Multiplayer-1.0.4.dll`.
- Two-peer verification needed: open/close each hand, trigger and grip poses across pistols/revolvers and long guns, and reload motions. If poses do not appear, inspect `[HAND POSE SEND]`, `[HAND POSE ROUTE]`, and `[HAND ANIM]` log entries on both peers.
