using System;
using System.Collections.Generic;
using FrooxEngine;
using HarmonyLib;

namespace HomeTabOverhaul;

// Checks, before anything is patched, that every private engine member the mod touches still exists. We compile
// against publicized assemblies, so a renamed private field only fails when the code first runs; checking by name
// up front turns that into one clear log line and a clean "stay off".
internal static class EngineCheck
{
    public static List<string> FindMissingMembers()
    {
        var missing = new List<string>();
        void Method(Type type, string name, Type[] parameters)
        {
            if (AccessTools.DeclaredMethod(type, name, parameters) == null) missing.Add($"{type.Name}.{name}()");
        }
        void Field(Type type, string name)
        {
            if (AccessTools.DeclaredField(type, name) == null) missing.Add($"{type.Name}.{name}");
        }

        Method(typeof(GridContainerPreset), "RunInitializer", []);
        Field(typeof(GridContainerPreset), "_isModified");
        Field(typeof(GridContainerPreset), "_initializer");
        Method(typeof(HomeScreenInitializer), "InitializeGridContainer", [typeof(GridContainer), typeof(int)]);
        // Desktop dragging (DesktopDrag) borrows the grid's own placement and preview code.
        Method(typeof(FacetContainer), "GetLocalPoint", [typeof(FrooxEngine.UIX.Canvas.InteractionData)]);
        Method(typeof(FacetContainer), "UpdateVisualization", [typeof(Elements.Core.Rect), typeof(Elements.Core.colorX).MakeByRefType(), typeof(bool)]);
        Method(typeof(FacetContainer), "ClearVisualization", [typeof(bool)]);
        Method(typeof(GridContainer), "GetFacetAtPoint", [typeof(Elements.Core.float2)]);
        Method(typeof(GridContainer), "GetCursorRect", [typeof(Elements.Core.float2)]);
        Method(typeof(GridContainer), "FindInsertionPlacement", [typeof(Facet), typeof(Elements.Core.int2), typeof(Elements.Core.int2), typeof(bool), typeof(Elements.Core.int2?)]);
        Method(typeof(FrooxEngine.UIX.Button), "RunPressed", [typeof(ButtonEventData)]);
        Method(typeof(NestedCanvas), "ProcessEvent", [typeof(FrooxEngine.UIX.Canvas.InteractionData)]);
        Method(typeof(GridContainer), "ToVisualRect", [typeof(Elements.Core.IntRect)]);
        Method(typeof(TemplateFacetPreset), "OnLoaded", [typeof(Slot)]);
        Field(typeof(RadiantDash), "_screensContainer");
        Field(typeof(ButtonParentUnderUser), "_originalSpace");
        Method(typeof(RadiantDash), "MarkScreensDirty", []);
        Field(typeof(RadiantDash), "_screenButtons");
        Field(typeof(RadiantDashButton), "_icon");
        Method(typeof(RadiantDashScreen), "BuildBackground", [typeof(FrooxEngine.UIX.UIBuilder), typeof(bool)]);
        return missing;
    }
}
