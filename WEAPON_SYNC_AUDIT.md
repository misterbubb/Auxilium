# Vertigo 2 weapon sync audit

Audited the installed Unity 2021.3.45f2 build's Addressables weapon profiles and their prefab component trees, then compared those with the local `Assembly-CSharp.dll` behavior and Auxilium's weapon sync path. The install contains 19 `EquippableProfile` assets. Game files were only read; no files were changed in the install.

## Profiles and behavior

| Profile | Main behavior found | Current Auxilium coverage / caveat |
| --- | --- | --- |
| `AK47` | `AK47` + one `Shooter`; magazine and bolt actions | Bullet spawn and integer ammo state are covered. Remote internal bolt/magazine animation is not driven. |
| `Altershock` | `Revolver` + two `Shooter` prefabs (`Revolver_shot`, `Revolver_shot_accumulated`) | Projectile variant index now selects the matching shooter. Revolver loading/cylinder animation is not driven remotely. |
| `Annihilator` | `Annihilator` + two eight-pellet `Shooter` prefabs; alternate continuous beam | Pellet projectiles and their shooter variants are covered. Superbeam activation/damage is not networked. |
| `Annihilator Prototype` | Prototype gun + one 8-pellet `Shooter` | Each bullet callback is sent separately, so standard projectile creation covers the pellets. Prototype reload mechanics are only represented by ammo count and hands. |
| `Blafaladaciousnesticles` | Melee blade, physical damage, and projectile reflection | Melee hit/parry/reflection state is not replicated by projectile packets. |
| `Bumblebee` | One `Shooter`; drum magazine | Bullet spawn and integer ammo state are covered. Drum/magazine animation is not driven remotely. |
| `Enlightenment` | `Enlighten` + `ContinuousBeam`; energy is a private float `power` | Beam state/damage and energy are not synchronized by the current bullet/rounds path. |
| `FingerGun` | One `Shooter`; syringe loading and self-damage on reload | Projectile creation is covered. The remote clone does not run its reload animation; self-damage behavior needs separate player-state verification. |
| `GordlePun` | Two `Shooter` components with distinct blue/orange projectile prefabs | Shooter index now chooses the firing mode's projectile. Mode/beam-like visual state is not synced beyond firing/projectiles. |
| `HammerSickle` | `DamageTrigger` and physical melee blade | Melee hit/damage sync is not implemented. |
| `HammerSickleOtherHand` | Other-hand profile using the same physical melee behavior | Same limitation as `HammerSickle`. |
| `Ionizer` | Four `Shooter` variants for normal/overclocked and homing/non-homing shots | Shooter index now selects the matching prefab. The current packet does not transmit the weapon's homing target state, if applied after bullet creation. Magazine/charge animation is not driven remotely. |
| `Kauboi's Revolver` | One `Shooter`; manually loaded revolver | Projectile creation and integer ammo count are covered. Loading/cylinder animation is not driven remotely. |
| `MeatNailer` | One `Shooter`; multi-holder chamber/reload logic | Projectile creation and rounds are covered. Individual holder/chamber states are not; remote reload animation is not driven. |
| `NostalgicPistol` | `ErrolGun` + one `Shooter`; resource is private float `power` | Projectile creation is covered. Resource level/recharge is not represented by `Gun.rounds`. |
| `PhageLauncher` | `PhageLauncher` directly creates a grenade prefab and sets Rigidbody velocity; no `Shooter` | Firing/projectile is not captured by `Shooter` callbacks. Integer ammo count alone does not make it functional over the network. |
| `QuadBow` | Charged and uncharged `Shooter` components; arrow speed is set by a per-shot delegate | Shooter index and actual bullet speed are now sent, preserving charged speed. Bow draw/nock state is not separately synced. |
| `Trident` | One slow shooter and three fast-fire shooters | Shooter index now preserves the selected barrel/shot. Ammo count is synced; fire mode UI/reload animation is not. |
| `VoidGrappler` | Custom `Grappler` state machine; no `Shooter` | Grapple launch, attachment point, rope, and player pull are not synchronized. |

## Changes from this audit

- Projectile packets now include the firing `Shooter` index and the bullet's actual speed. This is required for weapons whose prefab selects among multiple projectile templates and for the bow's charge-dependent speed.
- Host and clients resolve that index against the same weapon profile prefab before creating their projectile.
- Hand pose sync sends both hands' 24 non-wrist bone local positions and rotations at 30 Hz. The sender samples live `HandAnimator` bones, which include SteamVR curls and the active weapon's `HandSkeletonPoser` pose. The receiver interpolates between timestamped full-pose snapshots so packet timing does not make fingers chase each new sample abruptly.
- Hand wrist targets come from `BodyAnimator.handLeft.handTarget` and `handRight.handTarget`, with controller transforms used only before the game's hand targets initialize. Vertigo 2's `BodyAnimator.UpdateHandIK` applies held-equippable skeleton poser position and rotation to those targets; raw controller rotation omits that offset and misaligns the remote wrist, weapon, and fingers.
- Remote `HandAnimator` components are retained for their canonical bone mapping but disabled to prevent local SteamVR input. The remote mod applies received finger transforms after IK via a late script execution order.
- Version `1.0.9` restores full position-plus-rotation poses after the rotations-only 1.0.8 build caused left-hand mangling. It keeps the corrected stock VRIK settings and applies a short snapshot interpolation buffer; both players must install the same build.

## Remaining work for complete weapon sync

The current build is a testable projectile and weapon-aware finger-pose sync slice, not complete weapon sync for all 19 profiles. Next distinct sync paths are: PhageLauncher grenade spawn and launch velocity; beam start/stop and authoritative hit/damage for Enlightenment and Annihilator; melee hit/parry and reflected-projectile ownership; VoidGrappler attachment/reel state; and weapon-specific resource/reload visuals. These require separate event hooks and should be tested one weapon family at a time. Remote weapon scripts remain disabled to prevent a second local weapon simulation.
