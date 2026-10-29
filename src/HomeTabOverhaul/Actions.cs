using System;
using System.Collections.Generic;
using System.Linq;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.UIX;
using FrooxEngine.Undo;
using Renderite.Shared;
using ResoniteModLoader;

namespace HomeTabOverhaul;

// The mod's own button facets. Each one is built from engine components only (Facet + Canvas + Button) so it saves
// with the dash like any other facet; the Button's slot carries the tag "HomeTabOverhaul.Action:<id>" and the mod runs
// the action when it's pressed (ButtonPatches). Because the action is looked up by id, a link can be updated in a mod
// update without touching anyone's dash.
//   - InstallFacet loads a published facet and places it straight into the grid the button sits in.
//   - InstallDockTab loads an item that is a whole dash screen (it adds itself to the dock); the button then toggles
//     to "Uninstall …" (DockTabs).
//   - SpawnObject spawns an item in front of you in the world you're in, like the inventory's Spawn button.
//   - SpawnLocalMirror builds a mirror only you can see (LocalMirror).
internal static class Actions
{
    public const string TagPrefix = "HomeTabOverhaul.Action:";

    public enum Kind { InstallFacet, InstallDockTab, SpawnObject, SpawnLocalMirror, SpawnLocalLight, Respawn }

    // FromTemplate: the item is an installer facet holding the screen as a hidden template (DockTabs). ScreenName: the
    // dock tab's slot name, so a copy already on the dock counts as installed.
    // Look: the dock tab's own name, icon and colour, kept for it (Dock.LockLook) when it would lose them.
    public sealed record TabLook(string Label, Uri Icon, colorX Color);

    public sealed record Def(string Id, Kind Kind, string Label, Uri Icon, colorX Color, Uri? Record, bool FromTemplate = false,
        string? ScreenName = null, TabLook? Look = null);

    private static readonly colorX Purple = new(0.45f, 0.25f, 0.75f);
    private static readonly colorX Teal = new(0.1f, 0.5f, 0.5f);
    private static readonly colorX Orange = new(0.75f, 0.4f, 0.1f);

    public static readonly Def RedX = new("redx", Kind.InstallDockTab, "Install RedX+",
        OfficialAssets.Graphics.Icons.General.BoxOut, Purple, new Uri("resrec:///U-AdamSkI2003/R-dcc02b27-06e5-48a0-9c3c-2b007d313b7f"), FromTemplate: true,
        ScreenName: "CustomScreen [RedX+]", Look: new("RedX+",
            new Uri("neosdb:///155fa1d327923ae6202789ec87be071e31c25f9c36fa08fba511312352c3a15d.png"),
            new colorX(0.66157f, 0.06552f, 0.06552f, 1f, ColorProfile.Linear)));
    public static readonly Def BookStand = new("cj-bookstand", Kind.SpawnObject, "Creator Jam Book Stand",
        OfficialAssets.Graphics.Icons.General.Closed_Book, Orange, new Uri("resrec:///G-1StJATqZXay/R-01a118fc-bc79-72fd-81ae-633f8db80704"));
    public static readonly Def MirracleMirror = new("mirracle-mirror", Kind.InstallDockTab, "Install Mirracle Mirror",
        OfficialAssets.Graphics.Icons.General.Mirror, Teal, new Uri("resrec:///G-1StJATqZXay/R-2a5d6f3a-fd09-4b73-a0d8-7b7b8888257e"), FromTemplate: true,
        ScreenName: "CustomScreen [Mirracle]", Look: new("Mirracle",
            new Uri("resdb:///6ee6802bb64440f945d80f0ec9c6dedfa4bf48af024f044894726fb124e38d17.png"),
            new colorX(1f, 0.43f, 0.65f, 1f, ColorProfile.sRGB)));
    public static readonly Def SimpleMirror = new("simple-mirror", Kind.InstallDockTab, "Install Simple Reversible Mirror",
        OfficialAssets.Graphics.Icons.General.Mirror, Teal, new Uri("resrec:///G-1StJATqZXay/R-01a1811c-ec03-7f1d-8fbc-051d9e554bb9"));
    public static readonly Def ResoniteMirror = new("resonite-mirror", Kind.SpawnObject, "Spawn Resonite Mirror",
        OfficialAssets.Graphics.Icons.General.Mirror, Orange, new Uri("resrec:///G-Resonite/R-c96efd44-5127-4212-a8f4-ce618f6763b8"));
    public static readonly Def LocalLightButton = new("local-light", Kind.SpawnLocalLight, "Spawn Local Light",
        OfficialAssets.Graphics.Icons.General.Sparks, Purple, null);
    // The Respawn tile on top (Tiles.Respawn) presses this.
    public static readonly Def RespawnTile = new("respawn", Kind.Respawn, "Respawn", OfficialAssets.Graphics.Icons.General.Redo, Purple, null);
    public static readonly Def LocalMirrorButton = new("local-mirror", Kind.SpawnLocalMirror, "Spawn Local Mirror",
        OfficialAssets.Graphics.Icons.General.Mirror, Purple, null);

    // Not buttons: installed when the Home is built (Platform Info, and the side area of the bottom half).
    public static readonly Uri CommunityEvents = new("resrec:///G-1StJATqZXay/R-019d1dca-562a-7668-b60f-1e73f50a9c5b");
    public static readonly Uri WorldsYoureIn = new("resrec:///G-1StJATqZXay/R-aa476ea3-2874-420e-ad34-8bf507c4a03e");

    private static readonly Dictionary<string, Def> ById = new()
    {
        [RedX.Id] = RedX, [BookStand.Id] = BookStand, [MirracleMirror.Id] = MirracleMirror,
        [SimpleMirror.Id] = SimpleMirror, [ResoniteMirror.Id] = ResoniteMirror, [LocalMirrorButton.Id] = LocalMirrorButton,
        [LocalLightButton.Id] = LocalLightButton, [RespawnTile.Id] = RespawnTile,
    };

    public static Def? Find(string id) => ById.TryGetValue(id, out var def) ? def : null;

    private static Def? DefOf(Button button)
    {
        var tag = button.Slot.Tag;
        return tag != null && tag.StartsWith(TagPrefix, StringComparison.Ordinal) ? Find(tag.Substring(TagPrefix.Length)) : null;
    }

    public static void OnPressed(Button button)
    {
        if (!button.World.IsUserspace()) return;
        var def = DefOf(button);
        switch (def?.Kind)
        {
            case Kind.InstallFacet: InstallFromButton(button, def); break;
            case Kind.InstallDockTab: DockTabs.OnPressed(button, def); break;
            case Kind.SpawnObject: SpawnObject(button, def); break;
            case Kind.SpawnLocalMirror or Kind.SpawnLocalLight or Kind.Respawn:
                var world = button.Engine.WorldManager.FocusedWorld;
                if (world == null || world.IsUserspace()) break;
                if (def.Kind == Kind.SpawnLocalMirror) LocalMirror.Spawn(world);
                else if (def.Kind == Kind.SpawnLocalLight) LocalLight.Spawn(world);
                else Respawn(button, world);
                break;
        }
    }

    // Respawn in the world you're in, the same way the Session tab's Respawn button does it (the world spawns you
    // again once your user root is gone). The first press only warns.
    private static void Respawn(Button button, World world)
    {
        if (!button.Confirm("Save your work first! Press again to respawn", 4f, RadiantUI_Constants.MidLight.RED)) return;
        world.RunSynchronously(() => world.LocalUser.Root?.Slot?.DestroyPreservingAssets());
        ResoniteMod.Msg("Respawned.");
    }

    // What a button normally reads: dock-tab buttons say "Uninstall …" while their tab is on the dock.
    public static string LabelFor(Button button, Def def) =>
        def.Kind == Kind.InstallDockTab && DockTabs.IsInstalled(button, def)
            ? "Uninstall" + def.Label.Substring("Install".Length)
            : def.Label;

    // Brings every mod button in the dash up to date (run when the Home loads; the dock may have changed meanwhile).
    public static void RefreshLabels(RadiantDash dash)
    {
        foreach (var button in dash.Slot.GetComponentsInChildren<Button>(b => DefOf(b) != null))
        {
            var text = button.Slot.GetComponentInChildren<Text>();
            if (text != null) text.Content.Value = LabelFor(button, DefOf(button)!);
        }
    }

    // ---- Building a button facet --------------------------------------------------------------------------------

    public static Facet BuildFacet(World world, Def def)
    {
        var (facet, button, _) = BuildButtonFacet(world, def.Label, def.Icon, def.Color);
        button.Slot.Tag = TagPrefix + def.Id;
        return facet;
    }

    // An icon-and-label button facet in the style of the engine's own (also used by QuickSettings).
    public static (Facet Facet, Button Button, Text Label) BuildButtonFacet(World world, string label, Uri iconUri, colorX color)
    {

        var slot = world.AddSlot(label);
        var facet = slot.AttachComponent<Facet>();
        facet.PreferredSizes.Add(new float2(390f, 120f));
        facet.MinSize = new float2(160f, 64f);
        var canvas = facet.Canvas.Target.Slot;

        // The same outlined background the engine's facets use (FacetPreset.SetupBackground); the whole facet is the button.
        var background = canvas.AddSlot("Background");
        background.AttachComponent<RectTransform>();
        var image = background.AttachComponent<Image>();
        image.NineSliceSizing.Value = NineSliceSizing.FixedSize;
        image.Sprite.Target = RadiantUI_Constants.GetOutlinedSprite(world);
        image.Tint.Value = RadiantUI_Constants.BUTTON_COLOR.MulRGB(0.8f);
        var button = background.AttachComponent<Button>();


        var ui = new UIBuilder(background);
        ui.HorizontalLayout(16f, 12f).ForceExpandWidth.Value = false;
        ui.Style.MinWidth = 96f;
        ui.Style.PreferredWidth = 96f;
        ui.Style.FlexibleWidth = -1f;
        var iconBox = ui.Image(color);
        iconBox.Sprite.Target = RadiantUI_Constants.GetOutlinedSprite(world);
        iconBox.NineSliceSizing.Value = NineSliceSizing.FixedSize;
        ui.NestInto(iconBox.RectTransform);
        var icon = ui.Image(colorX.White);
        icon.Sprite.Target = icon.Slot.AttachSprite(iconUri);
        icon.PreserveAspect.Value = true;
        icon.RectTransform.AddFixedPadding(16f);
        ui.NestOut();
        ui.Style.MinWidth = -1f;
        ui.Style.PreferredWidth = -1f;
        ui.Style.FlexibleWidth = 1f;
        var text = ui.Text((LocaleString)label, alignment: Alignment.MiddleLeft);
        text.AutoSizeMax.Value = 40f;
        text.Color.Value = colorX.White;
        return (facet, button, text);
    }

    // ---- Install into a grid ------------------------------------------------------------------------------------

    private static void InstallFromButton(Button button, Def def)
    {
        var grid = GridOf(button.Slot);
        if (grid == null)
        {
            SetStatus(button, def, "Place this button in the dash first");
            return;
        }
        SetStatus(button, def, "Installing…", keep: true);
        InstallInto(grid, def.Record!, def.Label, message => SetStatus(button, def, message));
    }

    // The grid a facet is placed in: its slot sits under that grid's FacetsRoot.
    private static GridContainer? GridOf(Slot slot)
    {
        var facet = slot.GetComponentInParents<Facet>();
        var facetsRoot = facet?.Slot.Parent;
        var screen = slot.GetComponentInParents<RadiantDashScreen>();
        if (facetsRoot == null || screen == null) return null;
        foreach (var grid in screen.Slot.GetComponentsInChildren<GridContainer>())
            if (grid.FacetsRoot.Target == facetsRoot) return grid;
        return null;
    }

    // Loads a published facet into userspace and places it in the first free spot of the grid, top-left first.
    // `at` places it in a fixed spot instead (used when building the Home).
    public static void InstallInto(GridContainer grid, Uri record, string name, Action<string> report, IntRect? at = null)
    {
        var holder = grid.World.AddSlot(name);
        holder.StartTask(async () =>
        {
            var loaded = await holder.LoadObjectAsync(record);
            Guard.Actions.Run("installing " + name, () =>
            {
                if (!loaded || holder.IsRemoved)
                {
                    if (!holder.IsRemoved) holder.Destroy();
                    ResoniteMod.Warn($"Couldn't load {name} from {record}.");
                    report("Couldn't load it");
                    return;
                }
                var root = holder.GetComponent<InventoryItem>()?.Unpack(keepExistingPosition: false, new List<Slot>()) ?? holder;
                var facet = root.GetComponentInChildren<Facet>();
                if (facet == null)
                {
                    root.Destroy();
                    report("That item isn't a facet");
                    return;
                }
                // Some facets carry a "cloud spawning fix": ProtoFlux that, when the item loads, moves it in front of
                // your head (handy when spawning it from the inventory). In the dash that pulls the facet out of its
                // grid spot, and it would run again every time the dash loads, so it's removed before placing.
                foreach (var child in facet.Slot.Children.ToList())
                    if (child.Name.Contains("Cloudspawning", StringComparison.OrdinalIgnoreCase))
                    {
                        ResoniteMod.Msg($"Removed \"{child.Name}\" from {name} so it stays in its dash spot.");
                        child.Destroy();
                    }
                if (at != null && !grid.IsRemoved) grid.PlaceFacet(facet, at.Value);
                else if (grid.IsRemoved || !grid.IsReady || !TryPlace(grid, facet))
                {
                    root.Destroy();
                    report("No room in this tab");
                    return;
                }
                if (root != facet.Slot && !root.IsRemoved) root.Destroy(); // the facet has moved into the grid
                grid.NotifyModified();
                // In case anything else in the item moved it on load: put it back where the grid placed it.
                foreach (var delay in new[] { 0.5f, 2f })
                    grid.RunInSeconds(delay, () => Guard.Actions.Run("re-placing " + name, () => SnapToGrid(grid, facet)));
                ResoniteMod.Msg($"Installed {name}.");
                report("Installed!");
            });
        });
    }

    private static void SnapToGrid(GridContainer grid, Facet facet)
    {
        if (grid.IsRemoved || facet.IsRemoved) return;
        foreach (var nested in grid.ContentRoot.Slot.GetComponentsInChildren<NestedCanvas>())
            if (nested.TargetCanvas.Target == facet.Canvas.Target) facet.UpdatePlacedSize(grid, nested);
    }

    private static bool TryPlace(GridContainer grid, Facet facet)
    {
        var size = grid.GridSize;
        var rect = grid.FindInsertionPlacement(facet, new int2(0, size.y - 1), new int2(1, -1), verticalFirst: false);
        if (rect == null) return false;
        grid.PlaceFacet(facet, rect.Value);
        return true;
    }

    // ---- Spawn into the world -----------------------------------------------------------------------------------

    // Same steps as InventoryBrowser.SpawnItem, so world permissions are respected and it can be undone.
    private static void SpawnObject(Button button, Def def)
    {
        var world = button.Engine.WorldManager.FocusedWorld;
        if (world == null || world.IsUserspace()) return;
        world.RunSynchronously(() =>
        {
            if (!world.CanSpawnObjects())
            {
                NotificationMessage.SpawnTextMessage("Permissions.NotAllowedToSpawn".AsLocaleKey(), colorX.Red);
                return;
            }
            var slot = world.RootSlot.LocalUserSpace.AddSlot(def.Label);
            slot.StartTask(async () =>
            {
                await default(ToWorld);
                if (!await slot.LoadObjectAsync(def.Record!))
                {
                    if (!slot.IsRemoved) slot.Destroy();
                    ResoniteMod.Warn($"Couldn't load {def.Label} from {def.Record}.");
                    button.World.RunSynchronously(() => SetStatus(button, def, "Couldn't load it"));
                    return;
                }
                var items = new List<Slot>();
                slot.PositionInFrontOfUser(null, float3.Down * 0.2f, 0.5f);
                slot = slot.GetComponent<InventoryItem>()?.Unpack(keepExistingPosition: false, items) ?? slot;
                slot.World.BeginUndoBatch("Undo.Spawn".AsLocaleKey(("name", slot.Name_Field)));
                if (items.Count > 0)
                    foreach (var item in items) item.CreateSpawnUndoPoint();
                else
                    slot.CreateSpawnUndoPoint();
                slot.World.EndUndoBatch();
            });
        });
    }

    // ---- Status text on the button ------------------------------------------------------------------------------

    // Shows a message on the button for 3 seconds (or until the next one with `keep`), then its normal label again,
    // worked out fresh so an install/uninstall flips it.
    public static void SetStatus(Button button, Def def, string message, bool keep = false)
    {
        var text = button.Slot.GetComponentInChildren<Text>();
        if (text == null || text.IsRemoved) return;
        text.Content.Value = message;
        if (keep) return;
        button.RunInSeconds(3f, () =>
        {
            if (text.IsRemoved || text.Content.Value != message) return;
            text.Content.Value = LabelFor(button, def);
        });
    }
}
