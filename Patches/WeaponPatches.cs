using HarmonyLib;
using Vertigo2;

namespace Vertigo2Multiplayer.Patches
{
    /// <summary>
    /// Prevents replicated visual projectiles from applying damage on non-host clients.
    /// </summary>
    public static class WeaponPatches
    {
        [HarmonyPatch(typeof(Bullet), "DealDamage")]
        public static class Bullet_DealDamage_Patch
        {
            [HarmonyPrefix]
            public static bool Prefix(Bullet __instance, out HitInfo hitInfo, out bool playerHitSuccess)
            {
                if (!WeaponSync.IsVisualOnly(__instance))
                {
                    hitInfo = null;
                    playerHitSuccess = false;
                    return true;
                }

                hitInfo = null;
                playerHitSuccess = false;
                return false;
            }
        }

        [HarmonyPatch(typeof(Bullet), "OnDisable")]
        public static class Bullet_OnDisable_Patch
        {
            [HarmonyPostfix]
            public static void Postfix(Bullet __instance)
            {
                WeaponSync.ForgetBullet(__instance);
            }
        }

    }
}
