using System;
using FrooxEngine;
using FrooxEngine.UIX;
using HarmonyLib;

namespace HomeTabOverhaul.Patches;

// Every pointer event over a facet grid in UI Edit mode goes through FacetContainer.ProcessEvent. DesktopDrag takes
// the ones that make up a desktop mouse drag; everything else (VR, clicks, hovering) runs the engine's code as normal.
internal static class DragPatches
{
    public static void Apply(Harmony harmony)
    {
        var method = AccessTools.DeclaredMethod(typeof(FacetContainer), nameof(FacetContainer.ProcessEvent), [typeof(Canvas.InteractionData)])
                     ?? throw new MissingMethodException(nameof(FacetContainer), nameof(FacetContainer.ProcessEvent));
        harmony.Patch(method, prefix: new HarmonyMethod(typeof(DragPatches), nameof(BeforeProcessEvent)));
        var nested = AccessTools.DeclaredMethod(typeof(NestedCanvas), nameof(NestedCanvas.ProcessEvent), [typeof(Canvas.InteractionData)])
                     ?? throw new MissingMethodException(nameof(NestedCanvas), nameof(NestedCanvas.ProcessEvent));
        harmony.Patch(nested, prefix: new HarmonyMethod(typeof(DragPatches), nameof(BeforeNestedProcessEvent)));
    }

    // Returning false from the NestedCanvas (without running it) passes the event on to the grid behind it.
    private static bool BeforeNestedProcessEvent(NestedCanvas __instance, Canvas.InteractionData eventData, ref bool __result)
    {
        // Cheap checks first: this runs for every pointer event on every facet, in every world.
        if (Guard.Drag.Disabled || eventData.source is not RelayTouchSource || !__instance.World.IsUserspace()) return true;
        var skip = false;
        Guard.Drag.Run("routing the mouse to a facet grid", () => skip = DesktopDrag.ShouldSkipFacet(__instance, eventData));
        if (!skip) return true;
        __result = false;
        return false;
    }

    private static bool BeforeProcessEvent(FacetContainer __instance, Canvas.InteractionData eventData, ref bool __result)
    {
        if (Guard.Drag.Disabled || eventData.source is not RelayTouchSource || __instance is not GridContainer grid ||
            !grid.World.IsUserspace()) return true;
        var handled = false;
        var result = false;
        Guard.Drag.Run("moving a facet with the mouse", () => handled = DesktopDrag.Handle(grid, eventData, ref result));
        if (!handled) return true;
        __result = result;
        return false;
    }
}
