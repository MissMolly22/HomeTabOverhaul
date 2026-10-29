using System;
using System.Collections.Generic;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.UIX;
using ResoniteModLoader;

namespace HomeTabOverhaul;

// The mod's own button facets. Each one is built from engine components only (Facet + Canvas + Button) so it saves
// with the dash like any other facet; the Button's slot carries the tag "HomeTabOverhaul.Action:<id>" and the mod runs
// the action when it's pressed (ButtonPatches). Everything they make is built by the mod itself (no cloud items):
//   - InstallDockTab adds a dock tab the mod builds (the dash mirror); the button then toggles to "Uninstall …"
//     (DockTabs).
//   - SpawnLocalMirror / SpawnLocalLight build a mirror or a light only you can see (LocalMirror, LocalLight).
//   - Respawn respawns you in the world you're in (the Respawn tile).
internal static class Actions
{
    public const string TagPrefix = "HomeTabOverhaul.Action:";

    public enum Kind { InstallDockTab, SpawnLocalMirror, SpawnLocalLight, Respawn }

    // BuildScreen: for InstallDockTab, makes the dock tab.
    public sealed record Def(string Id, Kind Kind, string Label, Uri Icon, colorX Color,
        Func<RadiantDash, RadiantDashScreen>? BuildScreen = null);

    private static readonly colorX Purple = new(0.45f, 0.25f, 0.75f);
    private static readonly colorX Teal = new(0.1f, 0.5f, 0.5f);

    public static readonly Def DashMirror = new("dash-mirror", Kind.InstallDockTab, "Install Dash Mirror",
        OfficialAssets.Graphics.Icons.General.Mirror, Teal, global::HomeTabOverhaul.DashMirror.Build);
    public static readonly Def LocalMirrorButton = new("local-mirror", Kind.SpawnLocalMirror, "Spawn Local Mirror",
        OfficialAssets.Graphics.Icons.General.Mirror, Purple);
    public static readonly Def LocalLightButton = new("local-light", Kind.SpawnLocalLight, "Spawn Local Light",
        OfficialAssets.Graphics.Icons.General.Sparks, Purple);
    // The Respawn tile on top (Tiles.Respawn) presses this.
    public static readonly Def RespawnTile = new("respawn", Kind.Respawn, "Respawn", OfficialAssets.Graphics.Icons.General.Redo, Purple);

    private static readonly Dictionary<string, Def> ById = new()
    {
        [DashMirror.Id] = DashMirror, [LocalMirrorButton.Id] = LocalMirrorButton,
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
            case Kind.InstallDockTab: DockTabs.OnPressed(button, def); break;
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
