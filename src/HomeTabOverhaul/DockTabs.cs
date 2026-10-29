using System;
using System.Collections.Generic;
using System.Linq;
using FrooxEngine;
using FrooxEngine.UIX;
using ResoniteModLoader;

namespace HomeTabOverhaul;

// Items that are whole dash screens (the dashboard mirrors): loading one puts itself on the dock as a new tab. The
// button installs it like that and then becomes "Uninstall …", which removes that dock tab again (press twice).
// An installed tab is recognised by the tag "HomeTabOverhaul.Installed:<id>" on its screen slot, which saves with
// the dash. A copy that was already on the dock without the tag (installed by hand, or before this existed) is
// adopted the first time the button is used: the freshly loaded duplicate is removed and the existing one tagged.
internal static class DockTabs
{
    public const string InstalledTag = "HomeTabOverhaul.Installed:";
    private const double ConfirmSeconds = 3;

    private static readonly Dictionary<Button, double> PendingUninstall = new();

    public static List<RadiantDashScreen> Installed(RadiantDash dash, Actions.Def def) =>
        dash.Screens.Where(s => s.Slot.Tag == InstalledTag + def.Id ||
                                (def.ScreenName != null && s.Slot.Name == def.ScreenName && string.IsNullOrEmpty(s.Slot.Tag))).ToList();

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
            // Buttons and screens since destroyed aren't kept.
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
        var before = new HashSet<RadiantDashScreen>(dash.Screens);
        Actions.SetStatus(button, def, "Installing…", keep: true);
        var holder = button.World.AddSlot(def.Label);
        holder.StartTask(async () =>
        {
            var loaded = await holder.LoadObjectAsync(def.Record!);
            RadiantDashScreen? added = null;
            if (loaded && def.FromTemplate)
            {
                // An installer facet (Mirracle): the dock tab is a hidden template inside it, which the installer
                // copies into the dash once it's dropped there. Do that copy directly and throw the installer away.
                Guard.Actions.Run("installing " + def.Label, () =>
                {
                    var template = holder.GetComponentInChildren<RadiantDashScreen>();
                    if (template == null) return;
                    var copy = template.Slot.Duplicate(dash._screensContainer.Target);
                    copy.ActiveSelf = true;
                    added = copy.GetComponent<RadiantDashScreen>();
                    if (added != null) dash.RegisterScreen(added);
                    holder.Destroy();
                });
            }
            // Otherwise the item adds itself to the dock; give it up to ~10 seconds.
            for (var i = 0; loaded && i < 600 && added == null && !dash.IsRemoved; i++)
            {
                added = dash.Screens.FirstOrDefault(s => !before.Contains(s));
                if (added == null) await default(NextUpdate);
            }
            Guard.Actions.Run("installing " + def.Label, () =>
            {
                if (!loaded)
                {
                    if (!holder.IsRemoved) holder.Destroy();
                    ResoniteMod.Warn($"Couldn't load {def.Label} from {def.Record}.");
                    Actions.SetStatus(button, def, "Couldn't load it");
                    return;
                }
                if (added == null)
                {
                    // It didn't register itself: do it the way the engine adds screens.
                    added = holder.IsRemoved ? null : holder.GetComponentInChildren<RadiantDashScreen>();
                    if (added != null) dash.RegisterScreen(added);
                }
                if (added == null)
                {
                    Actions.SetStatus(button, def, "Couldn't add it to the dock");
                    return;
                }
                var twin = dash.Screens.FirstOrDefault(s => s != added && s.Slot.Name == added.Slot.Name && string.IsNullOrEmpty(s.Slot.Tag));
                if (twin != null)
                {
                    added.Slot.Destroy();
                    twin.Slot.Tag = InstalledTag + def.Id;
                    KeepLook(twin, def);
                    ResoniteMod.Msg($"{def.Label} was already on the dock; that tab is now the installed one.");
                    Actions.SetStatus(button, def, "Already installed");
                    return;
                }
                added.Slot.Tag = InstalledTag + def.Id;
                KeepLook(added, def);
                added.NotifyModified();
                ResoniteMod.Msg($"Installed {def.Label} as dock tab \"{added.Slot.Name}\".");
                Actions.SetStatus(button, def, "Installed!");
            });
        });
    }

    // Its name, icon and colour as installed (the ones the mod knows, or what it shows now), kept over restarts and
    // moves between the dock and folders.
    private static void KeepLook(RadiantDashScreen screen, Actions.Def def) =>
        Dock.LockLook(screen, def.Look?.Label ?? screen.Label.Value ?? screen.Slot.Name, def.Look?.Icon ?? screen.Icon.Value,
            def.Look?.Color ?? screen.ActiveColor.Value);

    // One-off tidy-up: the first mirror installs (before this existed) left duplicate dock tabs. Tabs with the same
    // slot name AND the same icon as an earlier tab are removed; the earliest copy stays.
    public static void RemoveDuplicates(RadiantDash dash)
    {
        var seen = new HashSet<(string, string)>();
        foreach (var screen in dash.Screens.ToList())
        {
            if (screen.Slot.Tag is Dock.FolderTag or Dock.CustomTag) continue; // made in the Dock tab; names are unique anyway
            var key = (screen.Slot.Name, screen.Icon.Value?.ToString() ?? "");
            if (seen.Add(key)) continue;
            ResoniteMod.Msg($"Removed duplicate dock tab \"{screen.Slot.Name}\" ({screen.Label.Value}).");
            screen.Slot.Destroy();
        }
    }
}
