using System;
using System.Collections.Generic;
using System.Linq;
using FrooxEngine;
using HarmonyLib;

namespace HomeTabOverhaul.Patches;

// RadiantDash.Screens only lists the screens on the dock. Tabs put in a dock folder (Dock) are moved off it, so they
// are added to the end of the list: the game looks screens up through it (GetScreen: "open the inventory", the
// contacts from a notification…) and keeps their size in step with the dash.
// RadiantDash.RegisterScreen puts a screen on the dock; some mods do that with their tab every session (Mod
// Settings), which would take it out of its folder, so the folders take back the tabs they remember.
internal static class DockPatches
{
    public static void Apply(Harmony harmony)
    {
        var getter = AccessTools.PropertyGetter(typeof(RadiantDash), nameof(RadiantDash.Screens))
                     ?? throw new MissingMethodException(nameof(RadiantDash), nameof(RadiantDash.Screens));
        harmony.Patch(getter, postfix: new HarmonyMethod(typeof(DockPatches), nameof(AfterScreens)));
        var register = AccessTools.DeclaredMethod(typeof(RadiantDash), nameof(RadiantDash.RegisterScreen), [typeof(RadiantDashScreen)])
                       ?? throw new MissingMethodException(nameof(RadiantDash), nameof(RadiantDash.RegisterScreen));
        harmony.Patch(register, postfix: new HarmonyMethod(typeof(DockPatches), nameof(AfterRegisterScreen)));
    }

    private static void AfterScreens(RadiantDash __instance, ref IEnumerable<RadiantDashScreen> __result)
    {
        if (Guard.Home.Disabled) return;
        __result = __result.Concat(Dock.Foldered(__instance));
    }

    // A few updates later: the mod registering it may still be setting it up (and naming it).
    private static void AfterRegisterScreen(RadiantDash __instance)
    {
        if (Guard.Home.Disabled || !__instance.World.IsUserspace() || !HomeTabOverhaul.Active) return;
        var dash = __instance;
        dash.RunInUpdates(5, () =>
        {
            if (!dash.IsRemoved) Guard.Home.Run("putting tabs back in their dock folders", () => Dock.Regather(dash));
        });
    }
}
