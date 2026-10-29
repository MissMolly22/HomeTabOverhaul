using System;
using FrooxEngine;
using HarmonyLib;
using ResoniteModLoader;

namespace HomeTabOverhaul.Patches;

// Most built-in button facets are cloud templates (TemplateFacetPreset). Each one has a dynamic variable
// "flip_horizontal" that decides which side the icon is on (New World and Open Home ship with it on = icon left,
// Paste, Nameplates, Freeform Dash, 2FA, Debug and Seated Mode with it off = icon right). When a template finishes
// loading into the dash, we switch it on so every icon sits left of its text. It's a saved value on the facet, so it
// stays; a game update that reloads the template runs this again.
internal static class TemplatePatches
{
    public static void Apply(Harmony harmony)
    {
        var method = AccessTools.DeclaredMethod(typeof(TemplateFacetPreset), "OnLoaded", [typeof(Slot)])
                     ?? throw new MissingMethodException(nameof(TemplateFacetPreset), "OnLoaded");
        harmony.Patch(method, postfix: new HarmonyMethod(typeof(TemplatePatches), nameof(AfterOnLoaded)));
    }

    private static void AfterOnLoaded(Slot root)
    {
        if (Guard.Home.Disabled) return;
        Guard.Home.Run("putting a facet's icon on the left", () =>
        {
            if (root == null || !root.World.IsUserspace()) return;
            var facet = root.GetComponentInParents<Facet>();
            var changed = false;
            foreach (var flip in (facet?.Slot ?? root).GetComponentsInChildren<DynamicValueVariable<bool>>(v => v.VariableName.Value == "flip_horizontal"))
            {
                if (flip.Value.IsDriven || flip.Value.Value) continue;
                flip.Value.Value = true;
                changed = true;
            }
            if (changed) ResoniteMod.Debug($"Icon moved to the left on {facet?.Slot.Name}.");
        });
    }
}
