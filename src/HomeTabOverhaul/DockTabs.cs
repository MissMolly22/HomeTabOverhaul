using System;
using System.Collections.Generic;
using System.Linq;
using FrooxEngine;
using FrooxEngine.UIX;
using ResoniteModLoader;

namespace HomeTabOverhaul;

// Buttons that add a dock tab the mod builds itself (the dash mirror): the button installs it and then becomes
// "Uninstall …", which removes that dock tab again (press twice). An installed tab is recognised by the tag
// "HomeTabOverhaul.Installed:<id>" on its screen slot, which saves with the dash.
internal static class DockTabs
{
    public const string InstalledTag = "HomeTabOverhaul.Installed:";
    private const double ConfirmSeconds = 3;

    private static readonly Dictionary<Button, double> PendingUninstall = new();

    public static List<RadiantDashScreen> Installed(RadiantDash dash, Actions.Def def) =>
        dash.Screens.Where(s => s.Slot.Tag == InstalledTag + def.Id).ToList();

    public static bool IsInstalled(Button button, Actions.Def def)
    {
        var dash = button.Slot.GetComponentInParents<RadiantDash>();
        return dash != null && Installed(dash, def).Count > 0;
    }

    public static void OnPressed(Button button, Actions.Def def)
    {
        var dash = button.Slot.GetComponentInParents<RadiantDash>();
        if (dash == null)
        {
            Actions.SetStatus(button, def, "Place this button in the dash first");
            return;
        }
        var installed = Installed(dash, def);
        if (installed.Count > 0) Uninstall(button, def, installed);
        else Install(button, def, dash);
    }

    private static void Uninstall(Button button, Actions.Def def, List<RadiantDashScreen> installed)
    {
        var now = button.Time.WorldTime;
        if (!PendingUninstall.TryGetValue(button, out var asked) || now - asked > ConfirmSeconds)
        {
            // Buttons since destroyed aren't kept.
            foreach (var gone in PendingUninstall.Keys.Where(b => b.IsRemoved).ToList()) PendingUninstall.Remove(gone);
            PendingUninstall[button] = now;
            Actions.SetStatus(button, def, "Press again to uninstall");
            return;
        }
        PendingUninstall.Remove(button);
        foreach (var screen in installed) screen.Slot.Destroy();
        ResoniteMod.Msg($"Uninstalled {def.Label} ({installed.Count} dock tab(s) removed).");
        Actions.SetStatus(button, def, "Uninstalled");
    }

    private static void Install(Button button, Actions.Def def, RadiantDash dash)
    {
        if (def.BuildScreen == null) return;
        var screen = def.BuildScreen(dash);
        screen.Slot.Tag = InstalledTag + def.Id;
        screen.NotifyModified();
        ResoniteMod.Msg($"Installed {def.Label} as dock tab \"{screen.Slot.Name}\".");
        Actions.SetStatus(button, def, "Installed!");
    }
}
