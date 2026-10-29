using System;
using FrooxEngine;
using FrooxEngine.UIX;
using HarmonyLib;
using ResoniteModLoader;

namespace HomeTabOverhaul.Patches;

// Our button facets carry no behaviour of their own (so they save as plain engine components). Every Button press
// runs through Button.RunPressed; the ones tagged as ours start their action.
internal static class ButtonPatches
{
    public static void Apply(Harmony harmony)
    {
        var method = AccessTools.DeclaredMethod(typeof(Button), "RunPressed", [typeof(ButtonEventData)])
                     ?? throw new MissingMethodException(nameof(Button), "RunPressed");
        harmony.Patch(method, postfix: new HarmonyMethod(typeof(ButtonPatches), nameof(AfterRunPressed)));
    }

    private static void AfterRunPressed(Button __instance)
    {
        var tag = __instance.Slot.Tag;
        if (tag == null || !tag.StartsWith("HomeTabOverhaul.", StringComparison.Ordinal)) return; // the cheap check first
        if (tag.StartsWith(Actions.TagPrefix, StringComparison.Ordinal) && !Guard.Actions.Disabled)
            Guard.Actions.Run("a HomeTabOverhaul button", () => Actions.OnPressed(__instance));
        else if (QuickSettings.IsTintButton(__instance) && !Guard.Actions.Disabled && __instance.World.IsUserspace())
            Guard.Actions.Run("a dash colour swatch", () => QuickSettings.OnTintPressed(__instance));
        else if (TabsController.IsOurs(__instance))
        {
            if (Guard.Home.Disabled || !__instance.World.IsUserspace())
                ResoniteMod.Msg($"Tab control {tag} pressed but not handled (Home part switched off: {Guard.Home.Disabled}).");
            else
                Guard.Home.Run("a tab button", () => TabsController.OnPressed(__instance));
        }
    }
}
