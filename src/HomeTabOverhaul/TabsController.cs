using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.UIX;
using ResoniteModLoader;

namespace HomeTabOverhaul;

// Everything about the Home's tabs that needs the mod at runtime: "New Tab +", renaming and deleting tabs (in UI Edit
// mode), and paging the tab list when there are more tabs than fit.
//
// A tab is read straight from the saved dash, so nothing extra has to be stored: its button carries a
// ButtonValueSet<int> whose number is the tab's id, and on the panel a ValueEqualityDriver<int> with that id as its
// Reference shows the tab's grid (and another its facets). Ids are never reused, so deleting a tab doesn't renumber
// the others. The controls this adds (New Tab +, the pager, the edit controls) are ordinary engine UI saved with the
// dash; their buttons are tagged "HomeTabOverhaul.Tabs:<action>" and handled here (ButtonPatches).
internal sealed class TabsController
{
    public const string TagPrefix = "HomeTabOverhaul.Tabs:";
    private const string NewTabName = "New Tab Button";
    private const string PagerName = "Tab Pager";
    private const string SpacerName = "Tab Page Spacer";
    private const string EditName = "Tab Edit Controls";
    private const long ControlsOrder = 1_000_000;
    private const double ConfirmSeconds = 3;

    private static readonly ConditionalWeakTable<Slot, TabsController> ByPanel = new();
    private static readonly colorX NewTabColor = RadiantUI_Constants.MidLight.GREEN;
    private static readonly colorX DeleteColor = RadiantUI_Constants.MidLight.RED;
    private static readonly colorX PagerColor = HomeBuilder.Navy;

    private readonly Slot _panel;
    private readonly GridContainerScreen _screen;
    private readonly ValueField<int> _active;
    private readonly Slot _list;
    private readonly Slot _gridsRoot;
    private int _page;
    private double _deleteAsked = double.MinValue;
    private bool _subscribed;

    private sealed record Tab(int Id, Button Button, Text? Label, GridContainer? Grid, Slot? Facets);

    private TabsController(Slot panel, GridContainerScreen screen, ValueField<int> active, Slot list, Slot gridsRoot)
    {
        _panel = panel;
        _screen = screen;
        _active = active;
        _list = list;
        _gridsRoot = gridsRoot;
    }

    public static TabsController? For(Slot panel)
    {
        if (ByPanel.TryGetValue(panel, out var existing)) return existing;
        var screen = panel.GetComponentInParents<GridContainerScreen>();
        var active = panel.GetComponent<ValueField<int>>();
        var list = panel.FindChild("Tab List")?.GetComponentInChildren<VerticalLayout>()?.Slot;
        var gridsRoot = panel.FindChild("Tab Grids");
        if (screen == null || active == null || list == null || gridsRoot == null) return null;
        var controller = new TabsController(panel, screen, active, list, gridsRoot);
        ByPanel.Add(panel, controller);
        return controller;
    }

    // Run whenever the Home loads: adds any controls this version has and older dashes don't, then lays out the list.
    public void Setup()
    {
        var tabs = Tabs();
        for (var i = 0; i < tabs.Count; i++)
        {
            SetHeight(tabs[i].Button.Slot, Layout.TabButtonHeight);
            // All tabs navy (earlier versions used grey, then one colour per tab).
            foreach (var shade in ShadesOf(tabs[i]))
                HomeBuilder.ColorTab(shade);
        }

        // The list starts at the top, with a small margin (it used to be centred).
        var layout = _list.GetComponent<VerticalLayout>();
        if (layout != null)
        {
            layout.VerticalAlign.Value = LayoutVerticalAlignment.Top;
            layout.PaddingTop.Value = Layout.Padding;
            layout.PaddingBottom.Value = Layout.Padding;
        }

        if (_list.FindChild(NewTabName) == null) BuildNewTabButton();
        if (_list.FindChild(SpacerName) == null) BuildSpacer();
        if (_list.FindChild(PagerName) == null) BuildPager();
        if (_list.FindChild(EditName) == null) BuildEditControls();
        // Earlier versions drove these two from a UI Edit mode sync. The driving component sat on the very slot it
        // switched off, so once hidden it couldn't bring them back until the screen was reopened. They're now shown
        // and hidden here (Refresh), on every edit mode change and whenever the Home is shown.
        foreach (var name in new[] { NewTabName, EditName })
        {
            var slot = _list.FindChild(name);
            if (slot == null || !slot.ActiveSelf_Field.IsDriven) continue;
            var driver = (slot.ActiveSelf_Field.ActiveLink as IWorldElement)?.FindNearestParent<Component>();
            if (driver != null) driver.Destroy();
            else slot.ActiveSelf_Field.ActiveLink?.ReleaseLink();
        }
        var pager = _list.FindChild(PagerName)!;
        SetHeight(pager, Layout.PagerHeight);
        var pageLabel = pager.FindChild("Page")?.GetComponentInChildren<Text>();
        if (pageLabel != null) pageLabel.AutoSizeMax.Value = 22f;
        // The pager and Rename buttons match the tabs.
        foreach (var button in _list.GetComponentsInChildren<Button>(b => b.Slot.Tag is TagPrefix + "prev" or TagPrefix + "next" or TagPrefix + "rename"))
            button.BaseColor.Value = PagerColor;
        if (!_subscribed)
        {
            _subscribed = true;
            // Switching tabs (including a new or deleted tab) shows the page with the open tab.
            _active.Value.OnValueChange += _ => Guard.Home.Run("switching tabs", () => Refresh(followOpenTab: true));
            // Entering or leaving UI Edit mode, wherever it's toggled (Settings, the radial menu…).
            Userspace.UserInterfaceEditModeChanged += OnEditModeChanged;
            // The column's height is only known after a layout pass.
            _panel.RunInSeconds(1f, () => Guard.Home.Run("laying out the tabs", () => Refresh()));
        }
        // The game starts on the first page.
        _page = 0;
        Refresh();
    }

    private void OnEditModeChanged(bool _)
    {
        if (_panel.IsRemoved)
        {
            Userspace.UserInterfaceEditModeChanged -= OnEditModeChanged;
            return;
        }
        _panel.RunSynchronously(() => Guard.Home.Run("UI Edit mode", () => Refresh()));
    }

    // Called when the Home screen is shown (ScreenPatches), in case edit mode changed while it was hidden.
    public static void OnScreenShown(RadiantDashScreen screen)
    {
        var panel = screen.ScreenCanvas?.Slot.FindChild(HomeBuilder.PanelName);
        if (panel != null && ByPanel.TryGetValue(panel, out var controller)) controller.Refresh();
    }

    // ---- Reading the tabs from the dash -------------------------------------------------------------------------

    private List<Tab> Tabs()
    {
        var drivers = _panel.GetComponents<ValueEqualityDriver<int>>();
        var tabs = new List<Tab>();
        foreach (var child in _list.Children.OrderBy(c => c.OrderOffset))
        {
            var set = child.GetComponent<ButtonValueSet<int>>();
            var button = child.GetComponent<Button>();
            if (set == null || button == null || set.TargetValue.Target != _active.Value) continue;
            var id = set.SetValue.Value;
            GridContainer? grid = null;
            Slot? facets = null;
            foreach (var driver in drivers)
            {
                if (driver.Reference.Value != id) continue;
                var shown = (driver.Target.Target as IWorldElement)?.FindNearestParent<Slot>();
                var found = shown?.GetComponent<GridContainer>();
                if (found != null) grid = found;
            }
            if (grid != null) facets = grid.FacetsRoot.Target;
            tabs.Add(new Tab(id, button, child.GetComponentInChildren<Text>(), grid, facets));
        }
        return tabs;
    }

    private Tab? Current() => Tabs().FirstOrDefault(t => t.Id == _active.Value.Value);

    // ---- Paging -------------------------------------------------------------------------------------------------

    // How many tab buttons fit in the column right now, measured from its laid-out height. In UI Edit mode New Tab +
    // and the rename box take room too; the pager (when needed) takes one short row.
    private (bool Paged, int PageSize) Capacity(int tabCount)
    {
        var layout = _list.GetComponent<VerticalLayout>();
        var spacing = layout?.Spacing.Value ?? 0f;
        var height = _list.GetComponent<RectTransform>()?.LocalComputeRect.size.y ?? 0f;
        if (height <= 1f) height = Layout.TabListFallbackHeight; // not laid out yet
        var available = height - (layout?.PaddingTop.Value ?? 0f) - (layout?.PaddingBottom.Value ?? 0f) + spacing;
        if (Userspace.UserInterfaceEditMode)
            available -= Layout.TabButtonHeight + spacing + Layout.PagerHeight * 2f + Layout.Padding + spacing;
        var row = Layout.TabButtonHeight + spacing;
        var fit = MathX.Max(1, MathX.FloorToInt(available / row));
        if (tabCount <= fit) return (false, fit);
        return (true, MathX.Max(1, MathX.FloorToInt((available - Layout.PagerHeight - spacing) / row)));
    }

    private void Refresh(bool followOpenTab = false)
    {
        var tabs = Tabs();
        var (paged, pageSize) = Capacity(tabs.Count);
        var pages = paged ? (tabs.Count + pageSize - 1) / pageSize : 1;
        var openIndex = tabs.FindIndex(t => t.Id == _active.Value.Value);
        if (openIndex >= 0 && paged && followOpenTab) _page = openIndex / pageSize;
        // Math.Clamp, not MathX.Clamp: the engine's int Clamp treats its maximum as exclusive (it returned page 1 for
        // every turn to page 2).
        _page = Math.Clamp(_page, 0, pages - 1);
        var shown = 0;
        for (var i = 0; i < tabs.Count; i++)
        {
            var visible = !paged || i / pageSize == _page;
            tabs[i].Button.Slot.ActiveSelf = visible;
            if (visible) shown++;
        }
        // A short last page keeps the empty rows, so New Tab + and the pager stay where they were (otherwise they
        // jump up as the page changes and the next click on ▶ misses).
        var spacer = _list.FindChild(SpacerName);
        if (spacer != null)
        {
            var missing = paged ? pageSize - shown : 0;
            spacer.ActiveSelf = missing > 0;
            var spacing = _list.GetComponent<VerticalLayout>()?.Spacing.Value ?? 0f;
            SetHeight(spacer, MathX.Max(0f, missing * (Layout.TabButtonHeight + spacing) - spacing));
        }

        // Every tab grid has its own UI Edit mode sync, on the grid's slot. While a tab is hidden that slot is off and
        // misses edit mode changes, so a tab opened later could still refuse to be edited until the screen was
        // reopened. A couple of frames from now (after the tab drivers have switched slots on) every sync on the
        // panel re-reads the current mode.
        _panel.RunInUpdates(2, () => Guard.Home.Run("syncing UI Edit mode", () =>
        {
            // (The whole screen: the Edit Dash UI facet carries one too.)
            foreach (var sync in _screen.Slot.GetComponentsInChildren<UserInterfaceEditModeSync>()) sync.MarkChangeDirty();
        }));

        // New Tab + and the rename box only in UI Edit mode.
        var editing = Userspace.UserInterfaceEditMode;
        var newTab = _list.FindChild(NewTabName);
        if (newTab != null && !newTab.ActiveSelf_Field.IsDriven) newTab.ActiveSelf = editing;
        var editBox = _list.FindChild(EditName);
        if (editBox != null && !editBox.ActiveSelf_Field.IsDriven) editBox.ActiveSelf = editing;

        var pager = _list.FindChild(PagerName);
        if (pager != null)
        {
            pager.ActiveSelf = paged;
            var label = pager.FindChild("Page")?.GetComponentInChildren<Text>();
            if (label != null) label.Content.Value = $"{_page + 1} / {pages}";
        }

        var field = _list.FindChild(EditName)?.GetComponentInChildren<TextField>();
        var current = tabs.FirstOrDefault(t => t.Id == _active.Value.Value);
        if (field != null && current?.Label != null) field.Text.Content.Value = current.Label.Content.Value;

        // The Dock tab's list follows the dock and UI Edit mode; it's rebuilt only while it's the open tab (opening it
        // is a tab switch, which comes through here).
        if (_active.Value.Value == Layout.DockTabId) Dock.RefreshEditorIn(_panel);
    }


    private double _lastTurn = double.MinValue;

    private IEnumerable<BooleanValueDriver<colorX>> ShadesOf(Tab tab) =>
        _panel.GetComponents<BooleanValueDriver<colorX>>(d => d.TargetField.Target == tab.Button.BaseColor);


    private void TurnPage(int by)
    {
        // One click can reach the button twice (pointer and laser both pressing); one page per click.
        var now = _panel.Time.WorldTime;
        if (now - _lastTurn < 0.2)
        {
            ResoniteMod.Msg("Tab pager: second press within 0.2 s ignored.");
            return;
        }
        _lastTurn = now;
        var before = _page;
        _page += by;

        Refresh();
        ResoniteMod.Msg($"Tab pager: page {before + 1} → {_page + 1}.");
    }

    // ---- Actions ------------------------------------------------------------------------------------------------

    public static bool IsOurs(Button button) =>
        button.Slot.Tag != null && button.Slot.Tag.StartsWith(TagPrefix, StringComparison.Ordinal);

    public static void OnPressed(Button button)
    {
        var panel = button.Slot;
        while (panel != null && panel.Name != HomeBuilder.PanelName) panel = panel.Parent;
        if (panel == null) return;
        var controller = For(panel);
        ResoniteMod.Msg($"Tab control pressed: {button.Slot.Tag.Substring(TagPrefix.Length)} (controller {(controller == null ? "missing" : "found")}).");
        if (controller == null) return;
        switch (button.Slot.Tag.Substring(TagPrefix.Length))
        {
            case "new": controller.NewTab(); break;
            case "prev": controller.TurnPage(-1); break;
            case "next": controller.TurnPage(1); break;
            case "rename": controller.Rename(); break;
            case "delete": controller.Delete(button); break;
        }
    }

    private void NewTab()
    {
        var tabs = Tabs();
        var id = tabs.Count == 0 ? 0 : tabs.Max(t => t.Id) + 1;
        var name = "New Tab";
        for (var n = 2; tabs.Any(t => t.Label?.Content.Value == name); n++) name = "New Tab " + n;

        var ui = HomeBuilder.ListBuilder(_list, Layout.TabButtonHeight);
        var button = HomeBuilder.BuildTabButton(ui, name, id, _active);
        button.Slot.OrderOffset = tabs.Count == 0 ? 0 : tabs.Max(t => t.Button.Slot.OrderOffset) + 1;
        var grid = HomeBuilder.BuildTabGrid(_screen, _gridsRoot, name);
        HomeBuilder.ShowWhenActive(grid.Slot, _active, id);
        HomeBuilder.ShowWhenActive(grid.FacetsRoot.Target, _active, id);
        _active.Value.Value = id;
        grid.NotifyModified();
        ResoniteMod.Msg($"Added tab \"{name}\" (id {id}).");
        Refresh();
    }

    private void Rename()
    {
        var tab = Current();
        var field = _list.FindChild(EditName)?.GetComponentInChildren<TextField>();
        var name = field?.Text.Content.Value?.Trim();
        if (tab?.Label == null || string.IsNullOrEmpty(name)) return;
        tab.Label.Content.Value = name;
        tab.Button.Slot.Name = "Tab Button - " + name;
        if (tab.Grid != null) tab.Grid.Slot.Name = "Tab - " + name;
        if (tab.Facets != null) tab.Facets.Name = HomeBuilder.FacetsPrefix + name;
        _active.NotifyModified();
        ResoniteMod.Msg($"Renamed tab {tab.Id} to \"{name}\".");
    }

    // Removes the open tab and the facets in it. Press twice: the first press only asks.
    private void Delete(Button pressed)
    {
        var tabs = Tabs();
        var tab = tabs.FirstOrDefault(t => t.Id == _active.Value.Value);
        if (tab == null) return;
        var label = pressed.Slot.GetComponentInChildren<Text>();
        if (tabs.Count <= 1)
        {
            Flash(label, "Can't delete the last tab", "Delete tab");
            return;
        }
        if (tab.Grid == null)
        {
            Flash(label, "The Dock tab can't be deleted", "Delete tab");
            return;
        }
        var now = pressed.Time.WorldTime;
        if (now - _deleteAsked > ConfirmSeconds)
        {
            _deleteAsked = now;
            Flash(label, "Press again: deletes the tab and its facets", "Delete tab");
            return;
        }
        _deleteAsked = double.MinValue;

        // The panel drivers that belong to this tab: the ones showing its grid and facets, and the one colouring its
        // button (found through the shade driver it feeds).
        var shades = _panel.GetComponents<BooleanValueDriver<colorX>>(d => d.TargetField.Target == tab.Button.BaseColor);
        foreach (var driver in _panel.GetComponents<ValueEqualityDriver<int>>(d => d.Reference.Value == tab.Id)) driver.Destroy();
        foreach (var shade in shades) shade.Destroy();
        var name = tab.Label?.Content.Value;
        tab.Facets?.Destroy();
        tab.Grid?.Slot.Destroy();
        tab.Button.Slot.Destroy();

        var next = tabs.Where(t => t != tab).ToList();
        var index = tabs.IndexOf(tab);
        _active.Value.Value = next[MathX.Min(index, next.Count - 1)].Id;
        _active.NotifyModified();
        Flash(label, "Deleted", "Delete tab");
        ResoniteMod.Msg($"Deleted tab \"{name}\" (id {tab.Id}).");
        Refresh();
    }

    private void Flash(Text? label, string message, string normal)
    {
        if (label == null) return;
        label.Content.Value = message;
        _panel.RunInSeconds((float)ConfirmSeconds, () =>
        {
            if (!label.IsRemoved && label.Content.Value == message) label.Content.Value = normal;
        });
    }

    // ---- Building the controls ----------------------------------------------------------------------------------

    private void BuildNewTabButton()
    {
        var ui = HomeBuilder.ListBuilder(_list, Layout.TabButtonHeight);
        var button = ui.Button((LocaleString)"New Tab +", colorX.White);
        button.Slot.Name = NewTabName;
        button.Slot.Tag = TagPrefix + "new";
        button.Slot.OrderOffset = ControlsOrder;
        HomeBuilder.StyleListButton(button, NewTabColor);
    }

    // Empty space standing in for the missing tab buttons on a short last page (sized in Refresh).
    private void BuildSpacer()
    {
        var spacer = _list.AddSlot(SpacerName);
        spacer.AttachComponent<RectTransform>();
        spacer.AttachComponent<LayoutElement>();
        spacer.OrderOffset = ControlsOrder - 1;
        spacer.ActiveSelf = false;
    }

    // ◀ 1 / 2 ▶, shown only when the tabs don't all fit.
    private void BuildPager()
    {
        var ui = HomeBuilder.ListBuilder(_list, Layout.PagerHeight);
        var row = ui.HorizontalLayout(Layout.Padding);
        row.Slot.Name = PagerName;
        row.Slot.OrderOffset = ControlsOrder + 1;
        row.ForceExpandWidth.Value = false;
        ui.Style.FlexibleWidth = 1f;
        PagerButton(ui, "◀", "prev");
        ui.Style.FlexibleWidth = 2f;
        var page = ui.Text((LocaleString)"1 / 1");
        page.Slot.Name = "Page";
        page.Color.Value = colorX.White;
        ui.Style.FlexibleWidth = 1f;
        PagerButton(ui, "▶", "next");
        ui.NestOut();
        row.Slot.ActiveSelf = false;
    }

    private static void PagerButton(UIBuilder ui, string text, string action)
    {
        var button = ui.Button((LocaleString)text, colorX.White);
        button.Slot.Tag = TagPrefix + action;
        HomeBuilder.StyleListButton(button, PagerColor, 22f);
    }

    // Only shown in UI Edit mode: a box with the open tab's name, Rename (applies the box), and Delete tab.
    private void BuildEditControls()
    {
        var ui = HomeBuilder.ListBuilder(_list, Layout.PagerHeight);
        var box = ui.VerticalLayout(Layout.Padding);
        box.Slot.Name = EditName;
        box.Slot.OrderOffset = ControlsOrder + 2;
        box.ForceExpandHeight.Value = false;
        var boxElement = box.Slot.GetComponentOrAttach<LayoutElement>();
        boxElement.MinHeight.Value = Layout.PagerHeight * 2f + Layout.Padding;
        boxElement.PreferredHeight.Value = Layout.PagerHeight * 2f + Layout.Padding;
        var field = ui.TextField();
        var fieldText = field.Text;
        fieldText.Color.Value = colorX.White;
        fieldText.AutoSizeMax.Value = 26f;
        ui.HorizontalLayout(Layout.Padding);
        ui.Style.FlexibleWidth = 1f;
        var rename = ui.Button((LocaleString)"Rename", colorX.White);
        rename.Slot.Tag = TagPrefix + "rename";
        HomeBuilder.StyleListButton(rename, PagerColor, 24f);
        var delete = ui.Button((LocaleString)"Delete tab", colorX.White);
        delete.Slot.Tag = TagPrefix + "delete";
        HomeBuilder.StyleListButton(delete, DeleteColor, 24f);
        ui.NestOut();
        ui.NestOut();
        // Shown with UI Edit mode (Refresh switches it).
    }

    private static void SetHeight(Slot slot, float height)
    {
        var element = slot.GetComponent<LayoutElement>();
        if (element == null) return;
        element.MinHeight.Value = height;
        element.PreferredHeight.Value = height;
    }
}
