using System;
using FrooxEngine;
using HarmonyLib;

namespace HomeTabOverhaul.Patches;

// The dash is a saved workspace, so the Home screen is loaded rather than rebuilt each session. Its GridContainerPreset
// runs the Home initializer once the grid is laid out (every load); that is our cue that the Home grid is ready.
internal static class PresetPatches
{
    public static void Apply(Harmony harmony)
    {
        var method = AccessTools.DeclaredMethod(typeof(GridContainerPreset), "RunInitializer", [])
                     ?? throw new MissingMethodException(nameof(GridContainerPreset), "RunInitializer");
        harmony.Patch(method,
            prefix: new HarmonyMethod(typeof(PresetPatches), nameof(BeforeRunInitializer)),
            postfix: new HarmonyMethod(typeof(PresetPatches), nameof(AfterRunInitializer)));
    }

    // A duplicated screen (our "Home (old)" copy) gets its preset removed straight away, but the copy's load has
    // already queued the initializer. Without this it would wipe the copy back to the default Home facets.
    private static bool BeforeRunInitializer(GridContainerPreset __instance) => !__instance.IsRemoved;

    private static void AfterRunInitializer(GridContainerPreset __instance)
    {
        if (Guard.Home.Disabled || HomeTabOverhaul.Config?.GetValue(HomeTabOverhaul.Enabled) == false) return;
        if (__instance.IsRemoved || __instance.InitializerType != typeof(HomeScreenInitializer)) return;
        Guard.Home.Run("setting up the Home screen", () => HomeBuilder.OnHomeReady(__instance));
    }
}
