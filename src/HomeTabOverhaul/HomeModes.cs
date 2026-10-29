using System;
using System.Linq;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.UIX;
using ResoniteModLoader;

namespace HomeTabOverhaul;

// What the Home shows, from the mod's settings:
//   - Tabbed: the mod's Home (big facets on top, tabs below). Its divider lines follow the "show_dividers" setting.
//   - Flat ("home_tabs" off): one full-size grid, "Flat Home", with the game's facets in their usual places and the
//     mod's in the free space. Built the first time it's needed, then kept: switching back and forth shows one and
//     hides the other, so each keeps whatever was rearranged in it.
//   - Old ("enabled" off, with a "Home (old)" tab): the new Home is parked off the dock and "Home (old)" takes its
//     place and name. Switching the mod back on swaps them back. With no "Home (old)", "enabled" off means Flat.
// Dock tabs and folders are left alone in every case.
internal static class HomeModes
{
    public const string FlatName = "Flat Home";
    private const string ParkedName = "HomeTabOverhaul Parked Home";

    private enum Mode { Tabbed, Flat, Old }

    private static bool Setting(ModConfigurationKey<bool> key) => HomeTabOverhaul.Config?.GetValue(key) ?? true;

    private static Mode Wanted(bool hasOld) =>
        !Setting(HomeTabOverhaul.Enabled) ? (hasOld ? Mode.Old : Mode.Flat)
        : Setting(HomeTabOverhaul.ShowTabs) ? Mode.Tabbed : Mode.Flat;

    // Whether facets in the flat Home are the ones in use (for the saved Dimness and Dash Colour).
    public static bool FlatShown => Wanted(hasOld: false) == Mode.Flat;

    public static bool IsFlatFacet(Slot slot) => slot.FindParent(s => s.Name == HomeBuilder.FacetsPrefix + FlatName) != null;

    // ---- Finding the screens ---------------------------------------------------------------------------------------

    private static Slot? Parked(RadiantDash dash) => dash.ScreensContainer?.Parent?.FindChild(ParkedName);

    private static GridContainerScreen? FindHome(RadiantDash dash) =>
        dash.Screens.Concat(Parked(dash)?.Children.Select(c => c.GetComponent<RadiantDashScreen>()).Where(s => s != null) ?? [])
            .OfType<GridContainerScreen>().FirstOrDefault(g => g.HasPreset(typeof(HomeScreenInitializer)));

    private static RadiantDashScreen? FindOld(RadiantDash dash) =>
        dash.Screens.FirstOrDefault(s => s.Slot.Name == HomeBuilder.OldHomeLabel);

    // ---- Applying ---------------------------------------------------------------------------------------------------

    // When the settings change: on the dash in userspace.
    public static void OnSettingsChanged()
    {
        var world = Userspace.UserspaceWorld;
        world?.RunSynchronously(() => Guard.Home.Run("applying the Home settings", () =>
        {
            var dash = world.RootSlot.GetComponentInChildren<RadiantDash>();
            if (dash == null) return;
            Apply(dash);
            // Switched (back) on: what the mod sets up when the dash loads, and the saved Dimness and Dash Colour.
            if (HomeTabOverhaul.Active)
            {
                Patches.ScreenPatches.SetUp(dash);
                QuickSettings.Apply(dash);
            }
        }));
    }

    public static void Apply(RadiantDash dash)
    {
        // The whole mod on or off: its dock tabs set aside or back, and its tint and dimming off or back.
        if (!Setting(HomeTabOverhaul.Enabled))
        {
            Dock.SwitchOff(dash);
            QuickSettings.Off(dash);
        }
        else Dock.SwitchOn(dash);
        var home = FindHome(dash);
        if (home == null) return;
        var old = FindOld(dash);
        var mode = Wanted(old != null);
        if (mode == Mode.Old)
        {
            Park(dash, home, old!);
            return;
        }
        Unpark(dash, home, old);
        SetLayout(home, mode == Mode.Flat);
        // Without tabs the Home's load hook may not run, so the install labels, Dimness and Dash Colour are
        // brought up to date from here too.
        if (mode == Mode.Flat) HomeBuilder.RefreshButtonsSoon(home);
    }

    // The dash opens on the Home when the game starts (otherwise the game shows whichever tab it finds first, which can
    // be a tab that puts itself first on the dock, like RedX+). With the mod off, that's "Home (old)" in its place.
    public static void OpenHome(RadiantDash dash)
    {
        var home = FindHome(dash);
        RadiantDashScreen? target = home != null && home.Slot.Parent?.Name != ParkedName ? home : FindOld(dash);
        if (target != null && dash.CurrentScreen.Target != target) dash.CurrentScreen.Target = target;
    }

    // ---- Tabbed or flat --------------------------------------------------------------------------------------------

    private static void SetLayout(GridContainerScreen screen, bool flat)
    {
        var canvas = screen.ScreenCanvas?.Slot;
        var panel = canvas?.FindChild(HomeBuilder.PanelName);
        var grid = screen.GridContainer;
        if (canvas == null || panel == null || grid == null) return; // not built yet: the build applies it when done
        var active = panel.GetComponent<ValueField<int>>();
        var side = panel.FindChild("Side")?.GetComponentInChildren<GridContainer>();
        var flatGrid = canvas.FindChild(FlatName)?.GetComponent<GridContainer>();

        if (flat)
        {
            flatGrid ??= BuildFlat(screen, canvas);
            // The Home's background is drawn by its own grid, hidden now, so this grid has its own (made by
            // earlier versions without one).
            var background = flatGrid.BackgroundRoot.Slot;
            if (background.GetComponent<TiledRawImage>() == null && background.GetComponent<Image>() == null)
                HomeBuilder.AddBackground(background);
            SetShown(flatGrid, true);
            SetShown(grid, false);
            SetShown(side, false);
            // The tab drivers live on the panel: with no tab open they hide every tab's facets, and only then is
            // the panel switched off (switched off first, they'd stop and leave the open tab's facets showing).
            if (active != null) active.Value.Value = -1;
            panel.RunInUpdates(2, () =>
            {
                if (!panel.IsRemoved && active?.Value.Value == -1) panel.ActiveSelf = false;
            });
            return;
        }

        SetShown(flatGrid, false);
        SetShown(grid, true);
        SetShown(side, true);
        var wasOff = !panel.ActiveSelf;
        panel.ActiveSelf = true;
        if (active != null && active.Value.Value < 0) active.Value.Value = 0;
        var dividers = Setting(HomeTabOverhaul.ShowDividers);
        var divider = panel.FindChild("Divider");
        if (divider != null) divider.ActiveSelf = dividers;
        var sideDivider = panel.FindChild("Side")?.FindChild("Divider");
        if (sideDivider != null) sideDivider.ActiveSelf = dividers;
        if (wasOff) HomeBuilder.SetUpTabs(panel);
    }

    // A grid and the facets it holds (they live outside it, under the screen).
    private static void SetShown(GridContainer? grid, bool shown)
    {
        if (grid == null) return;
        grid.Slot.ActiveSelf = shown;
        var facets = grid.FacetsRoot.Target;
        if (facets != null && !facets.ActiveSelf_Field.IsDriven) facets.ActiveSelf = shown;
    }

    private static GridContainer BuildFlat(GridContainerScreen screen, Slot canvas)
    {
        ResoniteMod.Msg("Building the Home without tabs.");
        var grid = HomeBuilder.BuildTabGrid(screen, canvas, FlatName, Layout.TotalColumns, Layout.TotalRows);
        grid.Slot.Name = FlatName;
        grid.StartTask(async () =>
        {
            // Placed once it's laid out (only while the Home is showing).
            while (!grid.IsReady)
            {
                if (grid.IsRemoved) return;
                await default(NextUpdate);
            }
            await default(NextUpdate);
            Guard.Home.Run("placing the Home facets without tabs", () =>
            {
                foreach (var placement in Layout.FlatFacets) HomeBuilder.AddFacet(grid, placement);
                foreach (var placement in Layout.FlatLive) HomeBuilder.AddFacet(grid, placement);
                grid.NotifyModified();
            });
        });
        return grid;
    }

    // ---- Swapping in "Home (old)" ----------------------------------------------------------------------------------

    private static void Park(RadiantDash dash, GridContainerScreen home, RadiantDashScreen old)
    {
        var container = dash.ScreensContainer;
        if (container == null || home.Slot.Parent?.Name == ParkedName) return; // already parked
        var holder = Parked(dash) ?? container.Parent.AddSlot(ParkedName);
        // Remember where "Home (old)" was, to put it back.
        holder.Tag = old.Slot.OrderOffset.ToString();
        if (old.Slot.Parent != container)
        {
            old.Slot.SetParent(container, keepGlobalTransform: false);
            old.Slot.SetIdentityTransform();
        }
        old.Slot.OrderOffset = home.Slot.OrderOffset;
        old.Label.Value = "Home";
        home.Slot.SetParent(holder, keepGlobalTransform: false);
        if (dash.CurrentScreen.Target == home) dash.CurrentScreen.Target = old;
        home.Hide();
        dash.MarkScreensDirty();
        old.NotifyModified();
        ResoniteMod.Msg("HomeTabOverhaul is off: \"Home (old)\" is the Home again.");
    }

    private static void Unpark(RadiantDash dash, GridContainerScreen home, RadiantDashScreen? old)
    {
        var holder = Parked(dash);
        var container = dash.ScreensContainer;
        if (holder == null || container == null || home.Slot.Parent != holder) return;
        home.Slot.SetParent(container, keepGlobalTransform: false);
        home.Slot.SetIdentityTransform();
        if (old != null)
        {
            home.Slot.OrderOffset = old.Slot.OrderOffset;
            old.Slot.OrderOffset = long.TryParse(holder.Tag, out var offset) && offset != home.Slot.OrderOffset
                ? offset : home.Slot.OrderOffset + 1;
            old.Label.Value = HomeBuilder.OldHomeLabel;
            if (dash.CurrentScreen.Target == old) dash.CurrentScreen.Target = home;
            old.NotifyModified();
        }
        dash.MarkScreensDirty();
        home.NotifyModified();
        ResoniteMod.Msg("HomeTabOverhaul is on: the new Home is back.");
    }
}
