using System;
using HarmonyLib;
using HomeTabOverhaul.Patches;
using ResoniteModLoader;

namespace HomeTabOverhaul;

// Reworks the dash's Home screen: a fixed panel of big everyday facets on the left, and fold-out tabs of facets on the
// right. Everything the mod builds is made of ordinary engine parts (grid containers, facets, buttons and drivers), so
// it saves with the dash, works with UI Edit mode, and keeps working even if the mod is removed later.
public class HomeTabOverhaul : ResoniteMod
{
    internal const string HarmonyId = "molly.HomeTabOverhaul";

    public override string Name => "HomeTabOverhaul";
    public override string Author => "Miss-Molly";
    public override string Version => "0.1.0";

    [AutoRegisterConfigKey]
    internal static readonly ModConfigurationKey<bool> Enabled = new("enabled",
        "Use the new Home screen. On the first run your current Home is kept as a dock tab called \"Home (old)\". " +
        "Turned off, \"Home (old)\" becomes the Home again (or, without it, the Home loses its tabs); turned back on, " +
        "the new Home returns as you left it. Dock tabs and folders stay either way.", () => true);

    [AutoRegisterConfigKey]
    internal static readonly ModConfigurationKey<bool> ShowTabs = new("home_tabs",
        "The tabs on the Home. Off: one Home page with the game's facets in their usual places and the mod's around " +
        "them. Each layout keeps its own changes when you switch.", () => true);

    [AutoRegisterConfigKey]
    internal static readonly ModConfigurationKey<bool> ShowDividers = new("show_dividers",
        "The divider lines between the Home's top, its tabs and the side area.", () => true);

    internal static ModConfiguration? Config;

    public override void OnEngineInit()
    {
        Config = GetConfiguration();

        var missing = EngineCheck.FindMissingMembers();
        if (missing.Count > 0)
        {
            Warn("The engine's dash has changed, so HomeTabOverhaul is staying off. Missing: " + string.Join(", ", missing));
            return;
        }

        var harmony = new Harmony(HarmonyId);
        try
        {
            PresetPatches.Apply(harmony);
            DragPatches.Apply(harmony);
            ButtonPatches.Apply(harmony);
            TemplatePatches.Apply(harmony);
            ScreenPatches.Apply(harmony);
            DockPatches.Apply(harmony);
            Msg("Patched the dash: Home screen, desktop facet dragging, install buttons, facet icons.");
            // Switching settings in the mod settings applies them straight away.
            if (Config != null) Config.OnThisConfigurationChanged += _ => HomeModes.OnSettingsChanged();
        }
        catch (Exception ex)
        {
            harmony.UnpatchAll(HarmonyId);
            Error("Failed to patch the dash; all HomeTabOverhaul patches were removed: " + ex);
        }
    }
}
