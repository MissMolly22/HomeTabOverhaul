using System;
using System.Runtime.CompilerServices;
using FrooxEngine;
using HarmonyLib;

namespace HomeTabOverhaul.Patches;

// When a dash screen is shown: if it's the Home, the tab column is brought up to date (UI Edit mode may have been
// switched while the Home was hidden). The first time any screen of a dash is shown (each time the dash loads), the
// Home is put in the state the settings ask for (HomeModes): the Home's own load hook doesn't fire while its grid is
// hidden (no tabs) or parked (mod switched off).
internal static class ScreenPatches
{
    private static readonly ConditionalWeakTable<RadiantDash, object> Seen = new();

    public static void Apply(Harmony harmony)
    {
        var method = AccessTools.DeclaredMethod(typeof(RadiantDashScreen), nameof(RadiantDashScreen.Show), [])
                     ?? throw new MissingMethodException(nameof(RadiantDashScreen), nameof(RadiantDashScreen.Show));
        harmony.Patch(method, postfix: new HarmonyMethod(typeof(ScreenPatches), nameof(AfterShow)));
    }

    private static void AfterShow(RadiantDashScreen __instance)
    {
        if (!__instance.World.IsUserspace()) return;
        if (!Guard.Actions.Disabled)
            Guard.Actions.Run("tinting a dash screen", () => QuickSettings.OnScreenShown(__instance));
        if (Guard.Home.Disabled) return;
        var dash = __instance.Slot.GetComponentInParents<RadiantDash>();
        if (dash != null && !Seen.TryGetValue(dash, out _))
        {
            Seen.Add(dash, new object());
            Guard.Home.Run("applying the Home settings", () => HomeModes.Apply(dash));
            Guard.Home.Run("setting up the dock folders", () => Dock.Setup(dash));
            Guard.Home.Run("opening the Home", () => HomeModes.OpenHome(dash));
            Guard.Home.Run("starting the live facets", () => LiveFacets.Start(dash));
        }
        // A folder's buttons are rebuilt each time it's opened (they must point at this session's dash).
        if (Dock.IsFolder(__instance))
            Guard.Home.Run("opening a dock folder", () => Dock.OnFolderShown(__instance));
        if (__instance is GridContainerScreen)
            Guard.Home.Run("showing the Home", () => TabsController.OnScreenShown(__instance));
    }
}
