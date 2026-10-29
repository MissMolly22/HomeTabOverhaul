using System;
using System.Collections.Generic;
using System.Linq;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.UIX;
using ResoniteModLoader;

namespace HomeTabOverhaul;

// Rearranging the dash's dock: the "Dock" tab on the Home lists every dock tab in order, and in UI Edit mode moves
// them (▲ ▼), puts them into folders and takes them out, renames them, and makes or deletes new tabs and folders.
//
// Everything is kept in the dash itself, so it saves with it and still works without the mod:
//   - The dock shows one button per screen under the dash's screens slot, ordered by the screens' OrderOffset; moving
//     a tab renumbers them.
//   - A folder is a dock screen of its own (tagged FolderTag, with the folder icon and colour). The tabs in it are
//     moved under its "Folder Contents" slot, which takes them off the dock, and its screen shows a big button for
//     each (ButtonReferenceSet on the dash's CurrentScreen), so they open as usual.
//   - A new tab is an empty facet screen (tagged CustomTag) to fill in UI Edit mode.
// The engine only knows the screens directly under the screens slot; DockPatches adds the ones in folders to
// RadiantDash.Screens, so things like "open the inventory" still find a foldered tab.
// The list itself is rebuilt by the mod whenever it's shown (not saved).
internal static class Dock
{
    public const string FolderTag = "HomeTabOverhaul.Folder";
    public const string CustomTag = "HomeTabOverhaul.DockTab";
    public const string EditorName = "Dock Editor";
    private const string ContentsName = "Folder Contents";
    private const string TilesName = "Folder Tiles";
    private const string ListName = "Dock List";
    private const double ConfirmSeconds = 3;

    public static readonly colorX FolderColor = colorX.FromHexCode("#e0a030");
    private static readonly colorX RowColor = new(0.1f, 0.12f, 0.18f, 0.9f);
    private static readonly colorX FolderRowColor = new(0.22f, 0.16f, 0.06f, 0.9f);
    private static readonly colorX DeleteColor = RadiantUI_Constants.MidLight.RED;
    private static readonly colorX AddColor = RadiantUI_Constants.MidLight.GREEN;

    private static double _lastAction = double.MinValue;
    private static readonly Dictionary<RadiantDashScreen, double> DeleteAsked = new();

    // ---- Reading the dock -------------------------------------------------------------------------------------------

    public static bool IsFolder(RadiantDashScreen? screen) => screen != null && screen.Slot.Tag == FolderTag;
    private static bool IsCustom(RadiantDashScreen screen) => screen.Slot.Tag == CustomTag;
    // Added by an install button (or by one in an earlier version: RedX+, the cloud mirrors); can be deleted here.
    private static bool IsInstalled(RadiantDashScreen screen) =>
        screen.Slot.Tag?.StartsWith(DockTabs.InstalledTag, StringComparison.Ordinal) == true;

    // The screens under a slot, in dock order (a slot's children are kept sorted by OrderOffset).
    private static List<RadiantDashScreen> ScreensUnder(Slot? parent)
    {
        var screens = new List<RadiantDashScreen>();
        if (parent == null) return screens;
        foreach (var child in parent.Children)
        {
            if (child.IsRemoved) continue;
            var screen = child.GetComponent<RadiantDashScreen>();
            if (screen != null && !screen.IsRemoved) screens.Add(screen);
        }
        return screens;
    }

    private static List<RadiantDashScreen> OnDock(RadiantDash dash) => ScreensUnder(dash.ScreensContainer);
    private static Slot Contents(RadiantDashScreen folder) => folder.Slot.FindChild(ContentsName) ?? folder.Slot.AddSlot(ContentsName);
    private static List<RadiantDashScreen> Inside(RadiantDashScreen folder) => ScreensUnder(folder.Slot.FindChild(ContentsName));

    // The tabs in folders, read without going through RadiantDash.Screens (DockPatches adds these to it).
    public static IEnumerable<RadiantDashScreen> Foldered(RadiantDash dash)
    {
        var container = dash.ScreensContainer;
        if (container == null) yield break;
        foreach (var child in container.Children.ToList())
        {
            if (child.IsRemoved || child.Tag != FolderTag) continue;
            foreach (var screen in ScreensUnder(child.FindChild(ContentsName))) yield return screen;
        }
    }

    private static RadiantDash? DashOf(Slot slot) => slot.GetComponentInParents<RadiantDash>();

    // ---- When the Home loads -----------------------------------------------------------------------------------------

    // Tabs in folders aren't hidden by the dash (it only manages the ones on the dock), so any left showing when the
    // dash was saved are hidden here. Folder buttons are rebuilt: the game makes the dash itself anew each session, so
    // saved buttons point at last session's (they're also rebuilt whenever a folder is opened). Tabs that other mods
    // put back on the dock at startup go back into their folders.
    public static void Setup(RadiantDash dash)
    {
        LockInstalled(dash);
        AfterLoad(dash);
        // Again a little later: tabs that fix themselves up on load (order, colour) may not have yet.
        dash.RunInSeconds(3f, () =>
        {
            if (!dash.IsRemoved) Guard.Home.Run("restoring the dock", () => AfterLoad(dash));
        });
        Regather(dash);
        foreach (var folder in OnDock(dash).Where(IsFolder))
        {
            BuildTiles(dash, folder);
            // Folders made before they remembered their tabs learn them now.
            var remembered = Remembered(folder);
            foreach (var name in Inside(folder).Select(s => s.Slot.Name).Where(n => !remembered.Contains(n)).ToList())
                Remember(folder, name, keep: true);
            foreach (var screen in Inside(folder))
                if (screen.IsShown && dash.CurrentScreen.Target != screen) screen.Hide();
        }
    }

    // Colours the game cleared while loading, and the dock's order.
    private static void AfterLoad(RadiantDash dash)
    {
        foreach (var screen in dash.Screens.ToList()) RestoreColor(screen);
        RestoreOrder(dash);
    }

    // ---- The mod switched off and on ("enabled") ------------------------------------------------------------------

    // Off: the dock as it is without the mod. Tabs in folders go back on the dock (their folders still remember them),
    // and the tabs the mod made (folders, new tabs, the Dash Mirror) are set aside off the dock. On: they come back,
    // the folders take their tabs again and the saved order returns. Nothing is deleted either way.
    private const string SetAsideName = "HomeTabOverhaul Set Aside Dock Tabs";

    private static bool MadeByMod(RadiantDashScreen screen) =>
        IsFolder(screen) || IsCustom(screen) || screen.Slot.Tag == DockTabs.InstalledTag + DashMirror.Id;

    public static void SwitchOff(RadiantDash dash)
    {
        var container = dash.ScreensContainer;
        if (container == null) return;
        var made = OnDock(dash).Where(MadeByMod).ToList();
        if (made.Count == 0) return;
        RememberOrder(dash);
        foreach (var folder in made.Where(IsFolder))
            foreach (var screen in Inside(folder))
            {
                screen.Slot.SetParent(container, keepGlobalTransform: false);
                screen.Slot.SetIdentityTransform();
                if (screen.IsShown && dash.CurrentScreen.Target != screen) screen.Hide();
            }
        var aside = container.Parent.FindChild(SetAsideName) ?? container.Parent.AddSlot(SetAsideName);
        foreach (var screen in made)
        {
            if (dash.CurrentScreen.Target == screen) dash.CurrentScreen.Target = null;
            screen.Slot.SetParent(aside, keepGlobalTransform: false);
            screen.Hide();
        }
        dash.MarkScreensDirty();
        dash.NotifyModified();
        ResoniteMod.Msg($"HomeTabOverhaul is off: set aside {made.Count} dock tab(s) and folder(s) it made.");
    }

    public static void SwitchOn(RadiantDash dash)
    {
        var container = dash.ScreensContainer;
        var aside = container?.Parent?.FindChild(SetAsideName);
        if (container == null || aside == null || aside.ChildrenCount == 0) return;
        foreach (var child in aside.Children.ToList())
        {
            child.SetParent(container, keepGlobalTransform: false);
            child.SetIdentityTransform();
        }
        Regather(dash);
        RestoreOrder(dash);
        foreach (var folder in OnDock(dash).Where(IsFolder)) BuildTiles(dash, folder);
        dash.MarkScreensDirty();
        ResoniteMod.Msg("HomeTabOverhaul is on: its dock tabs and folders are back.");
    }

    public static void OnFolderShown(RadiantDashScreen folder)
    {
        var dash = DashOf(folder.Slot);
        if (dash != null) BuildTiles(dash, folder);
    }

    // ---- Remembering what's in a folder --------------------------------------------------------------------------

    // A folder remembers the names of the tabs put in it (on its contents slot's tag), so a tab that a mod rebuilds or
    // re-registers each session (Mod Settings, …) and that lands back on the dock is put back into its folder.
    private const char NameSeparator = '\u001f';

    private static List<string> Remembered(RadiantDashScreen folder) =>
        (folder.Slot.FindChild(ContentsName)?.Tag ?? "").Split(NameSeparator, StringSplitOptions.RemoveEmptyEntries).ToList();

    private static void Remember(RadiantDashScreen folder, string name, bool keep)
    {
        var names = Remembered(folder);
        names.Remove(name);
        if (string.IsNullOrEmpty(name)) keep = false; // a tab that lost its name can't be found by it
        if (keep) names.Add(name);
        Contents(folder).Tag = string.Join(NameSeparator, names);
    }

    // When a screen is registered with the dash (DockPatches) and when the dash loads.
    public static void Regather(RadiantDash dash)
    {
        var dock = OnDock(dash);
        foreach (var folder in dock.Where(IsFolder))
        {
            var inside = Inside(folder).Select(s => s.Slot.Name).ToHashSet();
            foreach (var name in Remembered(folder))
            {
                if (inside.Contains(name)) continue;
                var screen = dock.FirstOrDefault(s => s.Slot.Name == name && !IsFolder(s) && s.Slot.Parent == dash.ScreensContainer);
                if (screen == null) continue;
                MoveInto(dash, screen, folder);
                inside.Add(name);
            }
        }
    }

    // ---- A tab's look that lasts -----------------------------------------------------------------------------------

    // Kept on a child slot of the screen:
    //   - Installed tabs (RedX+ …) rewrite their own name and icon from where they sit in the dash (blank, or
    //     "CustomScreen []" in a folder), so their dock name and icon are stored and their Label and Icon driven from it.
    //   - Colours. The game clears every tab's colour each time the dash loads (RadiantDashScreen.OnLoading looks at its
    //     icon texture before references are resolved, finds none and resets ActiveColor), so the colour of made tabs,
    //     folders and installed tabs is stored and put back after loading (Setup).
    private const string LookName = "HomeTabOverhaul Dock Look";
    private const string LockedTag = "locked";

    private static Slot LookSlot(RadiantDashScreen screen) => screen.Slot.FindChild(LookName) ?? screen.Slot.AddSlot(LookName);

    public static void LockLook(RadiantDashScreen screen, string label, Uri? icon, colorX? color)
    {
        var slot = LookSlot(screen);
        var labelField = slot.GetComponentOrAttach<ValueField<string>>();
        labelField.Value.Value = label;
        var iconField = slot.GetComponentOrAttach<ValueField<Uri>>();
        iconField.Value.Value = icon;
        if (!screen.Label.IsDriven) Copy(slot, labelField.Value, screen.Label);
        if (!screen.Icon.IsDriven) Copy(slot, iconField.Value, screen.Icon);
        slot.Tag = LockedTag;
        LockColor(screen, color);
        screen.NotifyModified();
    }

    private static bool IsLocked(RadiantDashScreen screen) => screen.Slot.FindChild(LookName)?.Tag == LockedTag;

    // The copy lives on the look slot (DriveFrom would put it on the screen's own slot, where Unhook takes it for
    // the tab's own driver and removes it).
    private static void Copy<T>(Slot look, IField<T> source, IField<T> target)
    {
        var copy = look.AttachComponent<ValueCopy<T>>();
        copy.Source.Target = source;
        copy.Target.Target = target;
    }

    // An installed tab's colour is driven from the stored one, so neither the game's load-time reset nor the tab's
    // own code can change it.
    private static void LockColor(RadiantDashScreen screen, colorX? color)
    {
        var field = LookSlot(screen).GetComponentOrAttach<ValueField<colorX?>>();
        field.Value.Value = color;
        if (!screen.ActiveColor.IsDriven) Copy(LookSlot(screen), field.Value, screen.ActiveColor);
    }

    private static colorX? StoredColor(RadiantDashScreen screen)
    {
        var slot = screen.Slot.FindChild(LookName);
        var has = slot?.GetComponent<ValueField<bool>>();
        var stored = slot?.GetComponent<ValueField<colorX>>();
        return has != null && stored != null && has.Value.Value ? stored.Value.Value : null;
    }

    private static void StoreColor(RadiantDashScreen screen, colorX? color)
    {
        var slot = LookSlot(screen);
        slot.GetComponentOrAttach<ValueField<colorX>>().Value.Value = color ?? colorX.Black;
        slot.GetComponentOrAttach<ValueField<bool>>().Value.Value = color.HasValue;
        screen.ActiveColor.Value = color;
    }

    private static void RestoreColor(RadiantDashScreen screen)
    {
        var slot = screen.Slot.FindChild(LookName);
        var has = slot?.GetComponent<ValueField<bool>>();
        var stored = slot?.GetComponent<ValueField<colorX>>();
        if (has == null || stored == null) return;
        colorX? color = has.Value.Value ? stored.Value.Value : null;
        if (!screen.ActiveColor.IsDriven && screen.ActiveColor.Value != color) screen.ActiveColor.Value = color;
    }

    // Installed tabs (the dash mirror, and ones from earlier versions such as RedX+) keep the look they have; tabs
    // whose own insides would change it have those parts taken off first (Unhook).
    private static void LockInstalled(RadiantDash dash)
    {
        foreach (var screen in dash.Screens.ToList())
        {
            var tag = screen.Slot.Tag;
            if (tag == null || !tag.StartsWith(DockTabs.InstalledTag, StringComparison.Ordinal)) continue;
            if (!Unhook(screen)) Lock(screen);
            // Drivers just destroyed let go of their fields at the end of the update.
            else screen.RunInUpdates(1, () => Guard.Home.Run("keeping a dock tab's look", () =>
            {
                if (screen.IsRemoved) return;
                Lock(screen);
                RestoreOrder(dash);
                Regather(dash); // its name is back, so a folder can find it again
            }));
        }
    }

    // The look kept for it: what's stored (once locked) or what it shows now.
    private static void Lock(RadiantDashScreen screen)
    {
        var look = screen.Slot.FindChild(LookName);
        var label = look?.GetComponent<ValueField<string>>()?.Value.Value ?? screen.Label.Value ?? screen.Slot.Name;
        var icon = look?.GetComponent<ValueField<Uri>>()?.Value.Value ?? screen.Icon.Value;
        var color = look?.GetComponent<ValueField<colorX?>>()?.Value.Value ?? StoredColor(screen) ?? screen.ActiveColor.Value;
        LockLook(screen, label, icon, color);
    }

    // Installed tabs whose own insides drive their name, order, label, icon or colour (RedX+ does, from variables its
    // installer would have set; without it they come back blank after a restart) have those drivers removed, so the
    // stored look and the dock's order apply. Returns whether any were.
    private static bool Unhook(RadiantDashScreen screen)
    {
        var look = screen.Slot.FindChild(LookName);
        var removed = false;
        foreach (var field in new IField[] { screen.Slot.Name_Field, screen.Slot.OrderOffset_Field, screen.Label, screen.Icon, screen.ActiveColor })
        {
            if (!field.IsDriven) continue;
            var driver = (field.ActiveLink as IWorldElement)?.FindNearestParent<Component>();
            if (driver == null || driver.IsRemoved || (look != null && driver.Slot.IsChildOf(look, includeSelf: true)) ||
                !driver.Slot.IsChildOf(screen.Slot, includeSelf: true)) continue;
            ResoniteMod.Msg($"Took {driver.GetType().Name} off dock tab \"{screen.Slot.Name}\" ({field.Name}).");
            driver.Destroy();
            removed = true;
        }
        return removed;
    }

    private static bool Movable(RadiantDashScreen screen) => !screen.Slot.OrderOffset_Field.IsDriven;

    // ---- Remembering the dock's order ------------------------------------------------------------------------------

    // Some tabs put themselves first again when they load (RedX+ sets its order to -999), so the order chosen in the
    // Dock tab is remembered (the screens slot's tag) and put back after loading.
    private static void RememberOrder(RadiantDash dash)
    {
        var container = dash.ScreensContainer;
        if (container != null) container.Tag = string.Join(NameSeparator, OnDock(dash).Select(s => s.Slot.Name));
    }

    private static void RestoreOrder(RadiantDash dash)
    {
        var names = (dash.ScreensContainer?.Tag ?? "").Split(NameSeparator, StringSplitOptions.RemoveEmptyEntries).ToList();
        if (names.Count == 0) return;
        var dock = OnDock(dash);
        // Remembered tabs in their remembered order; any others stay just after the tab they follow now.
        var key = new Dictionary<RadiantDashScreen, float>();
        var last = -1f;
        foreach (var screen in dock)
        {
            var index = names.IndexOf(screen.Slot.Name);
            last = index >= 0 ? index : last + 0.001f;
            key[screen] = last;
        }
        var order = dock.OrderBy(s => key[s]).ToList();
        if (order.SequenceEqual(dock)) return;
        Renumber(order);
        dash.MarkScreensDirty();
        ResoniteMod.Msg("Put the dock back in its saved order.");
    }

    // ---- The editor (the Dock tab on the Home) -------------------------------------------------------------------

    // The editor's place on the Home: a slot in the tab area, shown with the Dock tab (HomeBuilder).
    public static Slot BuildEditorSlot(Slot gridsRoot)
    {
        var slot = gridsRoot.AddSlot(EditorName);
        var rect = slot.AttachComponent<RectTransform>();
        rect.OffsetMin.Value = float2.One * Layout.Padding;
        rect.OffsetMax.Value = -float2.One * Layout.Padding;
        return slot;
    }

    public static void RefreshEditorIn(Slot panel)
    {
        var editor = panel.FindChild("Tab Grids")?.FindChild(EditorName);
        if (editor != null) Refresh(editor);
    }

    public static void Refresh(Slot editor)
    {
        var dash = DashOf(editor);
        if (dash == null || dash.ScreensContainer == null) return;
        var old = editor.FindChild(ListName);
        var oldRect = old?.GetComponentInChildren<ScrollRect>();
        (float2, float2)? oldScroll = oldRect == null ? null : (oldRect.NormalizedPosition.Value, oldRect.AbsolutePosition);
        old?.Destroy();
        var root = editor.AddSlot(ListName);
        root.PersistentSelf = false; // rebuilt every time; nothing in it needs saving
        root.AttachComponent<RectTransform>();
        var editing = Userspace.UserInterfaceEditMode;

        var ui = new UIBuilder(root);
        RadiantUI_Constants.SetupEditorStyle(ui, extraPadding: true);
        ui.Style.TextColor = colorX.White;
        ui.VerticalLayout(Layout.Padding).ForceExpandHeight.Value = false;

        // Title, and the add buttons in UI Edit mode.
        ui.Style.MinHeight = 56f;
        ui.Style.PreferredHeight = 56f;
        ui.HorizontalLayout(Layout.Padding).ForceExpandWidth.Value = false;
        ui.Style.FlexibleWidth = 1f;
        var title = ui.Text((LocaleString)"Dock", alignment: Alignment.MiddleLeft);
        title.AutoSizeMax.Value = 40f;
        ui.Style.FlexibleWidth = -1f;
        if (editing)
        {
            Act(ui, "New Tab +", AddColor, 220f, editor, () => NewTab(dash));
            Act(ui, "New Folder +", FolderColor.MulRGB(0.8f), 240f, editor, () => NewFolder(dash));
        }
        ui.NestOut();

        ui.Style.MinHeight = 36f;
        ui.Style.PreferredHeight = 36f;
        ui.Style.FlexibleWidth = 1f;
        var hint = ui.Text((LocaleString)(editing
            ? "▲ ▼ move a tab · Into folder / Next folder / Take out · type in a name to rename it"
            : "Turn on UI Edit mode (Quick Settings ▸ Edit Dash UI) to change the dock."), alignment: Alignment.MiddleLeft);
        hint.Color.Value = colorX.White.SetA(0.7f);
        hint.AutoSizeMax.Value = 24f;

        // The rows, scrolling when there are many.
        ui.Style.MinHeight = -1f;
        ui.Style.PreferredHeight = -1f;
        ui.Style.FlexibleHeight = 1f;
        var scroll = ui.ScrollArea();
        // Stay where the list was scrolled to: straight away by proportion, then exactly once it has been laid out.
        if (oldScroll != null)
        {
            var (normalized, absolute) = oldScroll.Value;
            scroll.NormalizedPosition.Value = normalized;
            scroll.RunInUpdates(2, () =>
            {
                if (!scroll.IsRemoved) scroll.AbsolutePosition = absolute;
            });
        }
        ui.VerticalLayout(6f).ForceExpandHeight.Value = false;
        ui.FitContent(SizeFit.Disabled, SizeFit.PreferredSize);
        ui.Style.FlexibleHeight = -1f;
        ui.Style.MinHeight = 52f;
        ui.Style.PreferredHeight = 52f;

        var dock = OnDock(dash);
        var folders = dock.Where(IsFolder).ToList();
        foreach (var screen in dock)
        {
            Row(ui, editor, dash, screen, dock, null, folders, editing);
            if (editing) Picker(ui, editor, dash, screen);
            if (!IsFolder(screen)) continue;
            var inside = Inside(screen);
            foreach (var nested in inside)
            {
                Row(ui, editor, dash, nested, inside, screen, folders, editing);
                if (editing) Picker(ui, editor, dash, nested);
            }
        }
    }

    // ---- Choosing an icon or colour ----------------------------------------------------------------------------------

    // Pressing a made tab's icon or colour opens a row of choices under it; picking one applies it and closes it.
    private static RadiantDashScreen? _pickerFor;
    private static bool _pickingIcon;

    private static void TogglePicker(RadiantDashScreen screen, bool icon)
    {
        var same = _pickerFor == screen && _pickingIcon == icon;
        _pickerFor = same ? null : screen;
        _pickingIcon = icon;
    }

    private static void Picker(UIBuilder ui, Slot editor, RadiantDash dash, RadiantDashScreen screen)
    {
        if (_pickerFor != screen) return;
        ui.Panel(new colorX(0.05f, 0.06f, 0.1f, 0.95f));
        var row = ui.HorizontalLayout(6f, 6f);
        row.ForceExpandWidth.Value = false;
        row.ChildAlignment = Alignment.MiddleLeft;
        ui.Style.FlexibleWidth = -1f;
        if (_pickingIcon)
        {
            foreach (var icon in Icons)
            {
                var chosen = screen.Icon.Value == icon;
                var button = Act(ui, "", chosen ? HomeBuilder.NavyOpen : HomeBuilder.Navy, 46f, editor, () => SetIcon(dash, screen, icon));
                AddIcon(button.Slot, icon, 0.14f);
            }
        }
        else
        {
            foreach (var color in Colors)
            {
                var chosen = screen.ActiveColor.Value == color;
                var button = Act(ui, chosen ? "✓" : "", color?.SetA(1f) ?? RadiantDashButton.DEFAULT_COLOR.SetA(1f), 46f, editor,
                    () => SetColor(dash, screen, color));
            }
        }
        ui.NestOut();
        ui.NestOut();
    }

    private static void Row(UIBuilder ui, Slot editor, RadiantDash dash, RadiantDashScreen screen,
        List<RadiantDashScreen> siblings, RadiantDashScreen? folder, List<RadiantDashScreen> folders, bool editing)
    {
        ui.Panel(IsFolder(screen) ? FolderRowColor : RowColor);
        var row = ui.HorizontalLayout(Layout.Padding, 4f);
        row.ForceExpandWidth.Value = false;
        row.ChildAlignment = Alignment.MiddleLeft;
        ui.Style.FlexibleWidth = -1f;
        if (folder != null)
        {
            ui.Style.MinWidth = 56f;
            ui.Empty("Indent");
        }
        var (label, iconUri) = (screen.Label.Value ?? screen.Slot.Name, screen.Icon.Value);
        if (editing && (IsFolder(screen) || IsCustom(screen)))
        {
            // Tabs and folders made here: the icon and the colour swatch each open a row of choices (Picker).
            var iconButton = Act(ui, "", HomeBuilder.Navy, 56f, editor, () => TogglePicker(screen, icon: true));
            AddIcon(iconButton.Slot, screen.Icon.Value, 0.15f);
            Act(ui, "", screen.ActiveColor.Value?.SetA(1f) ?? RadiantDashButton.DEFAULT_COLOR.SetA(1f), 44f, editor,
                () => TogglePicker(screen, icon: false));
        }
        else
        {
            ui.Style.MinWidth = 44f;
            ui.Style.PreferredWidth = 44f;
            if (iconUri != null) ui.Image(iconUri, colorX.White);
            else ui.Empty("No icon");
            ui.Style.PreferredWidth = -1f;
        }

        ui.Style.MinWidth = 200f;
        ui.Style.FlexibleWidth = 1f;
        // The game's own tabs have their names set by the game (translated), so only the others can be renamed.
        if (editing && !screen.Label.IsDriven)
        {
            var field = ui.TextField(label, parseRTF: false);
            field.Text.Color.Value = colorX.White;
            field.Text.AutoSizeMax.Value = 28f;
            var content = field.Text.Content;
            field.Editor.Target.LocalEditingFinished += _ => Guard.Home.Run("renaming a dock tab", () => Rename(screen, content.Value));
        }
        else
        {
            var text = ui.Text((LocaleString)label, alignment: Alignment.MiddleLeft);
            text.AutoSizeMax.Value = 28f;
        }
        ui.Style.FlexibleWidth = -1f;

        if (editing)
        {
            var index = siblings.IndexOf(screen);
            var up = Act(ui, "▲", HomeBuilder.Navy, 56f, editor, () => Move(dash, siblings, index, -1));
            up.Enabled = index > 0 && Movable(screen) && Movable(siblings[index - 1]);
            var down = Act(ui, "▼", HomeBuilder.Navy, 56f, editor, () => Move(dash, siblings, index, 1));
            down.Enabled = index < siblings.Count - 1 && Movable(screen) && Movable(siblings[index + 1]);
            if (folder != null)
            {
                if (folders.Count > 1)
                {
                    var next = folders[(folders.IndexOf(folder) + 1) % folders.Count];
                    Act(ui, "Next folder", FolderColor.MulRGB(0.6f), 170f, editor, () => MoveInto(dash, screen, next));
                }
                Act(ui, "Take out", HomeBuilder.Navy, 150f, editor, () => TakeOut(dash, screen, folder));
            }
            else if (!IsFolder(screen) && folders.Count > 0)
                Act(ui, "Into folder", FolderColor.MulRGB(0.6f), 170f, editor, () => MoveInto(dash, screen, folders[0]));
            if (IsFolder(screen) || IsCustom(screen) || IsInstalled(screen))
            {
                Button? delete = null;
                delete = Act(ui, "Delete", DeleteColor, 130f, editor, () => Delete(dash, screen, editor, delete!), refresh: false);
            }
        }
        ui.NestOut();
        ui.NestOut();
    }

    // A button that runs its action once per click (a click can arrive twice: pointer and laser) and then rebuilds
    // the list (next update: the button pressed is part of it).
    private static Button Act(UIBuilder ui, string text, colorX color, float width, Slot editor, Action action, bool refresh = true)
    {
        ui.Style.MinWidth = width;
        var button = ui.Button((LocaleString)text, colorX.White);
        HomeBuilder.StyleListButton(button, color, 24f);
        button.LocalPressed += (_, _) =>
        {
            var now = editor.Time.WorldTime;
            if (now - _lastAction < 0.2) return;
            _lastAction = now;
            Guard.Home.Run("changing the dock", () =>
            {
                action();
                if (refresh) editor.RunInUpdates(1, () => Guard.Home.Run("showing the dock", () => Refresh(editor)));
            });
        };
        return button;
    }

    // ---- Changing the dock ----------------------------------------------------------------------------------------

    private static void Move(RadiantDash dash, List<RadiantDashScreen> siblings, int index, int by)
    {
        var target = index + by;
        if (index < 0 || target < 0 || target >= siblings.Count) return;
        var order = siblings.ToList();
        (order[index], order[target]) = (order[target], order[index]);
        Renumber(order);
        Changed(dash, order[target]);
        ResoniteMod.Msg($"Moved dock tab \"{order[target].Label.Value}\" {(by < 0 ? "up" : "down")}.");
    }

    private static void Renumber(List<RadiantDashScreen> order)
    {
        // A tab whose own code drives its position (an older RedX+ copy) can't be moved; the others still are.
        for (var i = 0; i < order.Count; i++)
            if (!order[i].Slot.OrderOffset_Field.IsDriven) order[i].Slot.OrderOffset = i * 100;
        // The dock's own order is remembered (RestoreOrder).
        var dash = order.Count > 0 ? DashOf(order[0].Slot) : null;
        if (dash != null && order[0].Slot.Parent == dash.ScreensContainer) RememberOrder(dash);
    }

    private static void MoveInto(RadiantDash dash, RadiantDashScreen screen, RadiantDashScreen folder)
    {
        var from = screen.Slot.Parent?.Parent?.GetComponent<RadiantDashScreen>();
        var contents = Contents(folder);
        var last = ScreensUnder(contents).LastOrDefault();
        screen.Slot.SetParent(contents, keepGlobalTransform: false);
        screen.Slot.SetIdentityTransform();
        if (Movable(screen)) screen.Slot.OrderOffset = (last?.Slot.OrderOffset ?? 0) + 100;
        if (from != null && IsFolder(from)) { Remember(from, screen.Slot.Name, keep: false); BuildTiles(dash, from); }
        Remember(folder, screen.Slot.Name, keep: true);
        BuildTiles(dash, folder);
        Changed(dash, screen);
        ResoniteMod.Msg($"Put dock tab \"{screen.Label.Value}\" into folder \"{folder.Label.Value}\".");
    }

    // Back onto the dock, just after its folder.
    private static void TakeOut(RadiantDash dash, RadiantDashScreen screen, RadiantDashScreen folder)
    {
        screen.Slot.SetParent(dash.ScreensContainer, keepGlobalTransform: false);
        screen.Slot.SetIdentityTransform();
        var order = OnDock(dash).Where(s => s != screen).ToList();
        order.Insert(order.IndexOf(folder) + 1, screen);
        Renumber(order);
        BuildTiles(dash, folder);
        Remember(folder, screen.Slot.Name, keep: false);
        Changed(dash, screen);
        ResoniteMod.Msg($"Took dock tab \"{screen.Label.Value}\" out of folder \"{folder.Label.Value}\".");
    }

    private static void Rename(RadiantDashScreen screen, string? name)
    {
        name = name?.Trim();
        if (string.IsNullOrEmpty(name) || screen.IsRemoved || screen.Label.IsDriven || screen.Label.Value == name) return;
        screen.Label.Value = name;
        if (IsFolder(screen) || IsCustom(screen)) screen.Slot.Name = name!;
        screen.NotifyModified();
        ResoniteMod.Msg($"Renamed dock tab to \"{name}\".");
    }

    // The icons and colours a made tab or folder cycles through (null: the dash's own colour).
    private static readonly Uri[] Icons =
    [
        OfficialAssets.Common.Icons.Square, OfficialAssets.Common.Icons.Folder, OfficialAssets.Common.Icons.Star,
        OfficialAssets.Common.Icons.Heart, OfficialAssets.Common.Icons.Home, OfficialAssets.Common.Icons.Book,
        OfficialAssets.Common.Icons.Camera, OfficialAssets.Common.Icons.Gear, OfficialAssets.Common.Icons.Note,
        OfficialAssets.Common.Icons.Message, OfficialAssets.Common.Icons.Cloud, OfficialAssets.Common.Icons.Sun,
        OfficialAssets.Common.Icons.Moon, OfficialAssets.Common.Icons.Leaf, OfficialAssets.Common.Icons.Tree,
        OfficialAssets.Common.Icons.Bolt, OfficialAssets.Common.Icons.Cup, OfficialAssets.Common.Icons.Building,
        OfficialAssets.Common.Icons.Location, OfficialAssets.Common.Icons.Search, OfficialAssets.Common.Icons.Lock,
    ];

    private static readonly colorX?[] Colors =
    [
        null, FolderColor, colorX.FromHexCode("#3f6fd1"), colorX.FromHexCode("#8a4fd8"), colorX.FromHexCode("#2a9da6"),
        colorX.FromHexCode("#3c9a4a"), colorX.FromHexCode("#d0453c"), colorX.FromHexCode("#d65a9c"),
    ];

    private static void SetIcon(RadiantDash dash, RadiantDashScreen screen, Uri icon)
    {
        screen.Icon.Value = icon;
        // The dock button made its icon when it was built; point it at the new one.
        if (dash._screenButtons.TryGetValue(screen, out var button) && button._icon.Target is { } image)
            image.Sprite.Target = image.Slot.AttachSprite(icon, false, false, true, 256);
        _pickerFor = null;
        StyleChanged(dash, screen);
    }

    private static void SetColor(RadiantDash dash, RadiantDashScreen screen, colorX? color)
    {
        StoreColor(screen, color);
        _pickerFor = null;
        StyleChanged(dash, screen);
    }

    // A tab inside a folder also shows on the folder's page.
    private static void StyleChanged(RadiantDash dash, RadiantDashScreen screen)
    {
        var folder = screen.Slot.Parent?.Parent?.GetComponent<RadiantDashScreen>();
        if (folder != null && IsFolder(folder)) BuildTiles(dash, folder);
        screen.NotifyModified();
    }

    // An icon filling a button, inset by `inset` of its size on each side.
    private static void AddIcon(Slot button, Uri? uri, float inset)
    {
        if (uri == null) return;
        var icon = button.AddSlot("Icon");
        var rect = icon.AttachComponent<RectTransform>();
        rect.AnchorMin.Value = float2.One * inset;
        rect.AnchorMax.Value = float2.One * (1f - inset);
        var image = icon.AttachComponent<Image>();
        image.Sprite.Target = icon.AttachSprite(uri);
        image.PreserveAspect.Value = true;
    }

    private static void NewTab(RadiantDash dash)
    {
        var name = UniqueName(dash, "New Tab");
        var screen = dash.AttachScreen<GridContainerScreen>(name, null, OfficialAssets.Common.Icons.Square);
        screen.Slot.Tag = CustomTag;
        Append(dash, screen);
        ResoniteMod.Msg($"Added dock tab \"{name}\".");
    }

    private static void NewFolder(RadiantDash dash)
    {
        var name = UniqueName(dash, "New Folder");
        var folder = dash.AttachScreen(name, FolderColor, OfficialAssets.Common.Icons.Folder);
        folder.Slot.Tag = FolderTag;
        StoreColor(folder, FolderColor); // kept over restarts
        Contents(folder);
        BuildTiles(dash, folder);
        Append(dash, folder);
        ResoniteMod.Msg($"Added dock folder \"{name}\".");
    }

    // At the end of the dock, but before Exit.
    private static void Append(RadiantDash dash, RadiantDashScreen screen)
    {
        var order = OnDock(dash).Where(s => s != screen).ToList();
        var exit = order.FindIndex(s => s is ExitScreen);
        order.Insert(exit >= 0 ? exit : order.Count, screen);
        Renumber(order);
        Changed(dash, screen);
    }

    private static string UniqueName(RadiantDash dash, string baseName)
    {
        var taken = dash.Screens.Select(s => s.Label.Value).ToHashSet();
        var name = baseName;
        for (var n = 2; taken.Contains(name); n++) name = baseName + " " + n;
        return name;
    }

    // Press twice. A folder's tabs go back onto the dock first; a new tab takes its facets with it.
    private static void Delete(RadiantDash dash, RadiantDashScreen screen, Slot editor, Button pressed)
    {
        var now = editor.Time.WorldTime;
        if (!DeleteAsked.TryGetValue(screen, out var asked) || now - asked > ConfirmSeconds)
        {
            foreach (var gone in DeleteAsked.Keys.Where(s => s.IsRemoved).ToList()) DeleteAsked.Remove(gone);
            DeleteAsked[screen] = now;
            var label = pressed.Slot.GetComponentInChildren<Text>();
            if (label == null) return;
            label.Content.Value = "Sure?";
            editor.RunInSeconds((float)ConfirmSeconds, () =>
            {
                if (!label.IsRemoved && label.Content.Value == "Sure?") label.Content.Value = "Delete";
            });
            return;
        }
        DeleteAsked.Remove(screen);
        var name = screen.Label.Value;
        if (IsFolder(screen))
            foreach (var nested in Inside(screen).AsEnumerable().Reverse()) TakeOut(dash, nested, screen);
        if (dash.CurrentScreen.Target == screen) dash.CurrentScreen.Target = OnDock(dash).FirstOrDefault(s => s != screen);
        screen.Slot.Destroy();
        dash.MarkScreensDirty();
        ResoniteMod.Msg($"Deleted dock {(IsFolder(screen) ? "folder" : "tab")} \"{name}\".");
        editor.RunInUpdates(1, () => Guard.Home.Run("showing the dock", () => Refresh(editor)));
    }

    // The dock buttons only follow the screens' order when the dash re-reads them; the edit is saved with the dash.
    private static void Changed(RadiantDash dash, RadiantDashScreen screen)
    {
        dash.MarkScreensDirty();
        screen.NotifyModified();
    }

    // ---- A folder's own screen -------------------------------------------------------------------------------------

    // Its title and a grid of big buttons, one per tab inside, each opening that tab. Plain engine UI, saved with the
    // dash; rebuilt whenever the folder's contents change.
    private static void BuildTiles(RadiantDash dash, RadiantDashScreen folder)
    {
        var canvas = folder.ScreenCanvas?.Slot;
        if (canvas == null) return;
        if (canvas.ChildrenCount == 0) folder.BuildBackground(new UIBuilder(canvas), nest: false);
        canvas.FindChild(TilesName)?.Destroy();
        var root = canvas.AddSlot(TilesName);
        var rect = root.AttachComponent<RectTransform>();
        rect.OffsetMin.Value = float2.One * 48f;
        rect.OffsetMax.Value = -float2.One * 48f;

        var ui = new UIBuilder(root);
        RadiantUI_Constants.SetupEditorStyle(ui, extraPadding: true);
        ui.Style.TextColor = colorX.White;
        ui.VerticalLayout(24f).ForceExpandHeight.Value = false;
        ui.Style.MinHeight = 80f;
        ui.Style.PreferredHeight = 80f;
        var title = ui.Text((LocaleString)"", alignment: Alignment.MiddleLeft);
        title.Content.DriveFrom(folder.Label);
        title.AutoSizeMax.Value = 64f;

        var inside = Inside(folder);
        if (inside.Count == 0)
        {
            ui.Style.MinHeight = 48f;
            var empty = ui.Text((LocaleString)"This folder is empty. Add tabs to it from Home ▸ Dock in UI Edit mode.", alignment: Alignment.MiddleLeft);
            empty.Color.Value = colorX.White.SetA(0.7f);
            empty.AutoSizeMax.Value = 32f;
            return;
        }
        ui.Style.MinHeight = -1f;
        ui.Style.PreferredHeight = -1f;
        ui.Style.FlexibleHeight = 1f;
        ui.GridLayout(new float2(300f, 300f), new float2(24f, 24f), Alignment.TopLeft);
        foreach (var screen in inside)
        {
            var button = ui.Button((LocaleString)"", colorX.White);
            HomeBuilder.StyleListButton(button, screen.CurrentColor.SetA(1f).MulRGB(0.6f));
            var open = button.Slot.AttachComponent<ButtonReferenceSet<RadiantDashScreen>>();
            open.TargetReference.Target = dash.CurrentScreen;
            open.SetReference.Target = screen;
            var label = button.Slot.GetComponentInChildren<Text>();
            if (label != null)
            {
                label.Content.DriveFrom(screen.Label);
                label.Align = Alignment.BottomCenter;
                label.AutoSizeMax.Value = 34f;
                var labelRect = label.Slot.GetComponent<RectTransform>();
                labelRect.OffsetMin.Value = new float2(12f, 12f);
                labelRect.OffsetMax.Value = new float2(-12f, -12f);
            }
            var iconUri = screen.Icon.Value;
            if (iconUri != null)
            {
                var icon = button.Slot.AddSlot("Icon");
                var iconRect = icon.AttachComponent<RectTransform>();
                iconRect.AnchorMin.Value = new float2(0.25f, 0.3f);
                iconRect.AnchorMax.Value = new float2(0.75f, 0.9f);
                var image = icon.AttachComponent<Image>();
                image.Sprite.Target = icon.AttachSprite(iconUri);
                image.PreserveAspect.Value = true;
            }
        }
    }
}
