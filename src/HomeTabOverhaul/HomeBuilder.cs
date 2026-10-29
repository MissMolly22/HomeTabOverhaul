using System;
using System.Linq;
using System.Threading.Tasks;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.UIX;
using ResoniteModLoader;

namespace HomeTabOverhaul;

// Turns the engine's Home screen into the new layout, once. On the first run the current Home is duplicated into a
// dock tab "Home (old)", then the real Home (the one the engine knows as Home) is cleared and rebuilt:
//   - the engine's own Home grid shrinks to the top part of the screen and holds the fixed facets;
//   - "HomeTabOverhaul Tabs" fills the bottom: a column of tabs, and one grid container per tab stacked on each other.
// Tab switching is done by engine components only (ButtonValueSet writes the tab number into a ValueField,
// ValueEqualityDrivers show the matching grid), so it is saved with the dash and works without the mod.
internal static class HomeBuilder
{
    public const string PanelName = "HomeTabOverhaul Tabs";
    // The panel's name until every facet is placed. Finding it on a later load means the first run was cut short
    // (the game closed before the Home screen was ever shown), so the new Home is built again.
    private const string BuildingName = PanelName + " (building)";
    public const string FacetsPrefix = "Facets - ";
    public const string OldHomeLabel = "Home (old)";

    // The grey every tab had in the first version (Setup recolours tabs still in it).
    public static readonly colorX OldTabColor = RadiantUI_Constants.Neutrals.DARKLIGHT;

    // Every tab is navy; the open one is a brighter blue. (sRGB hex, like the dash's own palette.)
    public static readonly colorX Navy = colorX.FromHexCode("#22345e");
    public static readonly colorX NavyOpen = colorX.FromHexCode("#3f6fd1");

    public static void ColorTab(BooleanValueDriver<colorX> shade)
    {
        shade.FalseValue.Value = Navy;
        shade.TrueValue.Value = NavyOpen;
    }
    private static readonly colorX DividerColor = RadiantUI_Constants.Neutrals.MIDLIGHT.SetA(0.85f);

    public static void OnHomeReady(GridContainerPreset preset)
    {
        if (!preset.World.IsUserspace()) return;
        var grid = preset.Slot.GetComponent<GridContainer>();
        var screen = preset.Slot.GetComponentInParents<GridContainerScreen>();
        if (grid == null || screen == null || screen.GridContainer != grid || screen.ScreenCanvas == null) return;
        var existing = screen.ScreenCanvas.Slot.FindChild(PanelName) ?? screen.ScreenCanvas.Slot.FindChild(BuildingName);
        if (existing != null && existing.Name == PanelName && existing.Tag == Layout.Version)
        {
            // Already built on an earlier run. The dock may have changed since (tabs installed or removed by hand),
            // so the install buttons' labels are brought up to date once the dash has settled.
            RefreshButtonsSoon(screen);
            SetUpTabs(existing);
            ApplyModes(screen);
            return;
        }

        if (existing != null)
        {
            ResoniteMod.Msg(existing.Name == BuildingName
                ? "The new Home screen was not finished last time; building it again."
                : "The new Home screen was built with an older layout; building it again.");
            existing.Destroy();
        }
        // Leftover tab facets from an earlier build (also after one that failed half-way). Copied first: destroying
        // a child changes the list being walked.
        foreach (var child in screen.ScreenRoot.Children.ToList())
            if (child.Name.StartsWith(FacetsPrefix, StringComparison.Ordinal)) child.Destroy();
        // The Home without tabs goes with them (its facets were among those above).
        screen.ScreenCanvas.Slot.FindChild(HomeModes.FlatName)?.Destroy();
        // (Looked for among all dock tabs: it may be in a dock folder.)
        var dash = screen.Slot.GetComponentInParents<RadiantDash>();
        if (dash != null && !dash.Screens.Any(s => s.Slot.Name == OldHomeLabel))
        {
            ResoniteMod.Msg("First run: keeping the current Home as \"" + OldHomeLabel + "\" and building the new Home screen.");
            CopyOldHome(screen);
        }
        BuildNewHome(screen, preset, grid);
    }

    private static void CopyOldHome(GridContainerScreen home)
    {
        var copySlot = home.Slot.Duplicate();
        copySlot.Name = OldHomeLabel;
        copySlot.OrderOffset = home.Slot.OrderOffset + 1;
        // Without its preset the copy is a plain screen: the engine won't treat it as Home, re-initialise it, or
        // insert new default facets into it after game updates.
        foreach (var copiedPreset in copySlot.GetComponentsInChildren<GridContainerPreset>()) copiedPreset.Destroy();
        var copy = copySlot.GetComponent<GridContainerScreen>();
        copySlot.RemoveAllComponents((LocaleStringDriver d) => d.Target.Target == copy.Label);
        copy.Label.Value = OldHomeLabel;
        copy.GridContainer?.NotifyModified();
    }

    private static void BuildNewHome(GridContainerScreen screen, GridContainerPreset preset, GridContainer homeGrid)
    {
        // Marks the Home as customised so a game update never wipes it back to the default facets; the engine sets
        // this itself whenever someone edits the grid.
        preset._isModified.Value = true;

        homeGrid.DestroyAllFacets();
        homeGrid.ClearLastCalculatedData();
        homeGrid.RectTransform.AnchorMin.Value = new float2(0f, Layout.SplitY);
        homeGrid.RectTransform.AnchorMax.Value = float2.One;
        homeGrid.CellSize.Value = null;
        homeGrid.CellCount.Value = new int2(Layout.TotalColumns, Layout.FixedRows);

        var panel = BuildTabsPanel(screen, out var active, out var tabGrids, out var sideGrid);

        // Grids only know their size after a layout pass, and a hidden one never gets one. So every tab stays visible
        // (stacked) until its facets are placed, and only then do the drivers start showing one tab at a time.
        screen.StartTask(async () =>
        {
            if (!await WaitForLayout(screen, homeGrid, [.. tabGrids, sideGrid])) return;
            Guard.Home.Run("placing the Home facets", () =>
            {
                foreach (var placement in Layout.Fixed) AddFacet(homeGrid, placement);
                foreach (var install in Layout.FixedInstalls)
                    Actions.InstallInto(homeGrid, install.Record, "Battery facet",
                        message => ResoniteMod.Msg($"Installing a battery facet: {message}"), install.Rect);
                foreach (var install in Layout.Side)
                    Actions.InstallInto(sideGrid, install.Record, "Side facet",
                        message => ResoniteMod.Msg($"Installing into the side area: {message}"), install.Rect);
                for (var i = 0; i < tabGrids.Length; i++)
                {
                    var tab = Layout.Tabs[i]; // the report below runs later, after the loop has moved on
                    foreach (var placement in tab.Facets) AddFacet(tabGrids[i], placement);
                    // Loaded from the cloud, so they land a few seconds later (placed while the tab is still laid out).
                    foreach (var install in tab.Installs ?? [])
                        Actions.InstallInto(tabGrids[i], install.Record, tab.Name + " facet",
                            message => ResoniteMod.Msg($"Installing into {tab.Name}: {message}"), install.Rect);
                    ShowWhenActive(tabGrids[i].Slot, active, i);
                    ShowWhenActive(tabGrids[i].FacetsRoot.Target, active, i);
                }
                panel.Name = PanelName;
                panel.Tag = Layout.Version;
                SetUpTabs(panel);
                homeGrid.NotifyModified();
                ResoniteMod.Msg("New Home screen built.");
                ApplyModes(screen);
                // One-off, with the rebuild: the first mirror installs left duplicate dock tabs.
                var dash = screen.Slot.GetComponentInParents<RadiantDash>();
                if (dash != null) DockTabs.RemoveDuplicates(dash);
                RefreshButtonsSoon(screen);
            });
        });
    }

    // New Tab +, the pager and the edit controls, added to the saved panel if it doesn't have them yet (TabsController).
    // Tabs or not, and the divider lines, from the settings (HomeModes).
    private static void ApplyModes(RadiantDashScreen screen)
    {
        var dash = screen.Slot.GetComponentInParents<RadiantDash>();
        if (dash != null) HomeModes.Apply(dash);
    }

    // Also the dock: tabs in folders hidden, folder screens complete, the Dock tab's list filled in.
    public static void SetUpTabs(Slot panel) =>
        Guard.Home.Run("setting up the tab controls", () =>
        {
            var dash = panel.GetComponentInParents<RadiantDash>();
            if (dash != null) Dock.Setup(dash);
            TabsController.For(panel)?.Setup();
        });

    public static void RefreshButtonsSoon(RadiantDashScreen screen) =>
        screen.RunInSeconds(3f, () => Guard.Actions.Run("updating the install buttons", () =>
        {
            var dash = screen.Slot.GetComponentInParents<RadiantDash>();
            if (dash == null || dash.IsRemoved) return;
            Actions.RefreshLabels(dash);
            // Saved Dimness and Dash Colour.
            QuickSettings.Apply(dash);
        }));

    private static async Task<bool> WaitForLayout(RadiantDashScreen screen, GridContainer homeGrid, GridContainer[] tabGrids)
    {
        var fixedHeight = screen.ScreenCanvas.Size.Value.y * (1f - Layout.SplitY);
        bool Ready()
        {
            // The fixed grid still reports its old full height until the pass after we shrank it.
            if (!homeGrid.IsReady || homeGrid.RectTransform.LocalComputeRect.size.y > fixedHeight * 1.1f) return false;
            foreach (var grid in tabGrids)
                if (!grid.IsReady) return false;
            return true;
        }
        // Layout only runs while the Home screen is visible, so this can wait until the dash is next opened on Home.
        while (!Ready())
        {
            // A dash reload in the meantime starts the build over, which removes these grids.
            if (homeGrid.IsRemoved || Guard.Home.Disabled || Array.Exists(tabGrids, g => g.IsRemoved)) return false;
            await default(NextUpdate);
        }
        await default(NextUpdate);
        return Ready();
    }

    public static void AddFacet(GridContainer grid, Layout.Placement placement)
    {
        Facet facet;
        if (placement.Build != null)
            facet = placement.Build(grid.World);
        else
            facet = ((FacetPreset)grid.World.AddSlot(placement.Preset!.Name).AttachComponent(placement.Preset)).Facet;
        grid.PlaceFacet(facet, placement.Rect);
    }

    private static Slot BuildTabsPanel(GridContainerScreen screen, out ValueField<int> active, out GridContainer[] grids,
        out GridContainer side)
    {
        var panel = screen.ScreenCanvas.Slot.AddSlot(BuildingName);
        var panelRect = panel.AttachComponent<RectTransform>();
        panelRect.AnchorMax.Value = new float2(1f, Layout.SplitY);
        // The panel gets its own draw group. A canvas draws its nested facet canvases in the order they were created,
        // not in screen order; with the tab background sitting between the top facets and the tab facets, a moved
        // (re-created) top facet shuffled that order and the first tab facet was drawn under the tab background
        // (looked dimmed). In its own group, the tab facets are only ordered among themselves, after the background.
        panel.AttachComponent<GraphicChunkRoot>();
        active = panel.AttachComponent<ValueField<int>>();

        var background = panel.AddSlot("Background");
        background.AttachComponent<RectTransform>();
        AddBackground(background);

        // A clear line between the fixed facets above and the tabs below (before the tab grids, so nothing in the
        // panel is drawn after the facets).
        var divider = panel.AddSlot("Divider");
        var dividerRect = divider.AttachComponent<RectTransform>();
        dividerRect.AnchorMin.Value = new float2(0f, 1f);
        dividerRect.AnchorMax.Value = float2.One;
        dividerRect.OffsetMin.Value = new float2(Layout.Padding * 2f, -2f);
        dividerRect.OffsetMax.Value = new float2(-Layout.Padding * 2f, 2f);
        divider.AttachComponent<Image>().Tint.Value = DividerColor;

        var list = panel.AddSlot("Tab List");
        var listRect = list.AttachComponent<RectTransform>();
        listRect.AnchorMax.Value = new float2(Layout.TabListRight, 1f);
        // The dash's own button look (rounded sprite, bold font) as used by Settings and Inventory.
        var ui = ListBuilder(list, Layout.TabButtonHeight);
        var layout = ui.VerticalLayout(Layout.Padding * 1.5f, Layout.Padding * 2f);
        layout.ForceExpandHeight.Value = false;
        for (var i = 0; i < Layout.Tabs.Length; i++) BuildTabButton(ui, Layout.Tabs[i].Name, i, active).Slot.OrderOffset = i;
        // The Dock tab comes last; its page is the dock editor rather than a facet grid.
        BuildTabButton(ui, "Dock", Layout.DockTabId, active).Slot.OrderOffset = Layout.DockTabId;

        var gridsRoot = panel.AddSlot("Tab Grids");
        var gridsRect = gridsRoot.AttachComponent<RectTransform>();
        gridsRect.AnchorMin.Value = new float2(Layout.TabListRight, 0f);
        gridsRect.AnchorMax.Value = new float2(Layout.SideLeft, 1f);
        grids = new GridContainer[Layout.Tabs.Length];
        for (var i = 0; i < Layout.Tabs.Length; i++) grids[i] = BuildTabGrid(screen, gridsRoot, Layout.Tabs[i].Name);
        ShowWhenActive(Dock.BuildEditorSlot(gridsRoot), active, Layout.DockTabId);

        // The side area on the right: one grid that stays put whichever tab is open. No draw group of its own: a grid
        // works out where its facets go from its position within its draw group, so inside a group that doesn't
        // start at the panel's corner the facets landed in the wrong place. (The panel's group starts at the screen's
        // bottom-left corner, which is why the tabs are fine.)
        var sideRoot = panel.AddSlot("Side");
        var sideRect = sideRoot.AttachComponent<RectTransform>();
        sideRect.AnchorMin.Value = new float2(Layout.SideLeft, 0f);
        var sideLine = sideRoot.AddSlot("Divider");
        var sideLineRect = sideLine.AttachComponent<RectTransform>();
        sideLineRect.AnchorMax.Value = new float2(0f, 1f);
        sideLineRect.OffsetMin.Value = new float2(-2f, Layout.Padding * 2f);
        sideLineRect.OffsetMax.Value = new float2(2f, -Layout.Padding * 2f);
        sideLine.AttachComponent<Image>().Tint.Value = DividerColor;
        side = BuildTabGrid(screen, sideRoot, "Side", Layout.SideColumns);
        return panel;
    }

    public static Button BuildTabButton(UIBuilder ui, string name, int index, ValueField<int> active)
    {
        var button = ui.Button((LocaleString)name, colorX.White);
        button.Slot.Name = "Tab Button - " + name;
        var set = button.Slot.AttachComponent<ButtonValueSet<int>>();
        set.TargetValue.Target = active.Value;
        set.SetValue.Value = index;
        StyleListButton(button, Navy);

        // BaseColor switches between the normal and selected shade with the open tab.
        var shade = active.Slot.AttachComponent<BooleanValueDriver<colorX>>();
        ColorTab(shade);
        shade.TargetField.Target = button.BaseColor;
        var isOpen = active.Slot.AttachComponent<ValueEqualityDriver<int>>();
        isOpen.TargetValue.Target = active.Value;
        isOpen.Reference.Value = index;
        isOpen.Target.Target = shade.State;
        return button;
    }

    // White, size-capped label, and the colour taken from BaseColor: the colour drivers only brighten it on hover and
    // press (a Button on a tinted Image would otherwise multiply the tint by itself and come out much darker).
    public static void StyleListButton(Button button, colorX color, float maxText = 30f)
    {
        var label = button.Slot.GetComponentInChildren<Text>();
        if (label != null)
        {
            label.Color.Value = colorX.White;
            label.AutoSizeMax.Value = maxText;
        }
        foreach (var colorDriver in button.ColorDrivers)
        {
            if (colorDriver.TintColorMode.Value == InteractionElement.ColorMode.Direct) continue;
            colorDriver.TintColorMode.Value = InteractionElement.ColorMode.Explicit;
            colorDriver.NormalColor.Value = colorX.White;
            colorDriver.HighlightColor.Value = new colorX(1.3f, 1.3f, 1.3f);
            colorDriver.PressColor.Value = new colorX(1.6f, 1.6f, 1.6f);
        }
        button.BaseColor.Value = color;
    }

    // A UIBuilder that adds rows to the tab list in the dash's own button style.
    public static UIBuilder ListBuilder(Slot list, float height)
    {
        var ui = new UIBuilder(list);
        RadiantUI_Constants.SetupEditorStyle(ui, extraPadding: true);
        ui.Style.TextColor = colorX.White;
        ui.Style.MinHeight = height;
        ui.Style.PreferredHeight = height;
        return ui;
    }

    public static GridContainer BuildTabGrid(GridContainerScreen screen, Slot root, string name, int columns = Layout.TabColumns, int rows = Layout.TabRows)
    {
        var slot = root.AddSlot("Tab - " + name);
        slot.AttachComponent<RectTransform>();
        var grid = slot.AttachComponent<GridContainer>();
        grid.CellSize.Value = null;
        grid.CellCount.Value = new int2(columns, rows);
        grid.Padding.Value = float2.One * Layout.Padding;
        // Facets live outside the canvas (like the engine's own "Facets" slot) and are drawn where the grid puts them,
        // so a hidden tab has to hide its facets too.
        var facets = screen.ScreenRoot.AddSlot(FacetsPrefix + name);
        grid.FacetsRoot.Target = facets;
        grid.EditMode.DriveFrom(slot.AttachComponent<UserInterfaceEditModeSync>().EditModeActive);
        GridContainerAnimatedEditVisual.Setup(grid, grid.BackgroundRoot.Slot.AddSlot("EditGrid"));
        return grid;
    }

    // The same background the engine gives every dash screen (RadiantDashScreen.BuildBackground), so the tab half
    // matches the fixed half above it.
    public static void AddBackground(Slot slot)
    {
        if (Engine.Current.InUniverse)
        {
            slot.AttachComponent<Image>().Tint.Value = UserspaceRadiantDash.DEFAULT_BACKGROUND;
            return;
        }
        var image = slot.AttachComponent<TiledRawImage>();
        image.TileSize.Value = RadiantDashScreen.BackgroundTiling;
        image.Texture.Target = slot.AttachTexture(RadiantDashScreen.BackgroundTexture);
        image.Tint.Value = UserspaceRadiantDash.DEFAULT_BACKGROUND;
    }

    // The driver sits on the always-active panel: a component on a slot it has just switched off would stop updating.
    public static void ShowWhenActive(Slot slot, ValueField<int> active, int index)
    {
        var driver = active.Slot.AttachComponent<ValueEqualityDriver<int>>();
        driver.TargetValue.Target = active.Value;
        driver.Reference.Value = index;
        driver.Target.Target = slot.ActiveSelf_Field;
    }
}
