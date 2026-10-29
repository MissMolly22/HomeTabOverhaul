using System;
using Elements.Assets;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.UIX;
using Renderite.Shared;

namespace HomeTabOverhaul;

// Big icon tiles for the top of the Home: a large rounded icon with a small caption underneath. The engine's own
// facets for these come from cloud templates whose layout can only put the icon beside the text, so these are built
// here from the same engine components the templates use (ButtonOpenHome, FullBodyCalibratorSpawner, ButtonToggle +
// UserTrackingSpaceSync). Everything is a plain engine component, so the tiles save with the dash and keep working
// without the mod. Colours are the ones the templates use.
internal static class Tiles
{
    // The game's built-in icons (the cloud templates use their own images).
    private static readonly Uri HomeIcon = OfficialAssets.Graphics.Icons.Dash.CloudHome;
    private static readonly Uri CalibratorIcon = OfficialAssets.Graphics.Icons.Dash.VivePuck; // the tracker, as the game's facet has
    private static readonly colorX Brown = new(0.2823529541492462f, 0.22352942824363708f, 0.16470588743686676f, 1f, ColorProfile.sRGB);
    private static readonly colorX Purple = new(0.2862745225429535f, 0.18431372940540314f, 0.3921568989753723f, 1f, ColorProfile.sRGB);

    public static Facet OpenHome(World world)
    {
        var tile = Build(world, "Open Home Tile", "Open Home", HomeIcon, Brown);
        tile.Button.Slot.AttachComponent<ButtonOpenHome>();
        return tile.Facet;
    }

    public static Facet FullBodyCalibrator(World world)
    {
        var tile = Build(world, "Full Body Calibrator Tile", "Full Body Calibrator", CalibratorIcon, Purple);
        tile.Button.Slot.AttachComponent<FullBodyCalibratorSpawner>();
        return tile.Facet;
    }

    // Respawn needs the mod (it acts on the world you're in, and warns first): the tile's button is tagged as one of
    // the mod's actions (Actions.Respawn).
    public static Facet Respawn(World world)
    {
        var tile = Build(world, "Respawn Tile", "Respawn", OfficialAssets.Graphics.Icons.General.Redo, Purple);
        tile.Button.Slot.Tag = Actions.TagPrefix + Actions.RespawnTile.Id;
        return tile.Facet;
    }

    public static Facet SeatedMode(World world)
    {
        var tile = Build(world, "Seated Mode Tile", "Seated Mode: Off", OfficialAssets.Graphics.Icons.General.Unseated, Purple);
        // The same pairing as the engine's template: UserTrackingSpaceSync mirrors the real seated mode both ways,
        // and the button toggles it.
        var sync = tile.Facet.Slot.AttachComponent<UserTrackingSpaceSync>();
        tile.Button.Slot.AttachComponent<ButtonToggle>().TargetValue.Target = sync.SeatedMode;
        var label = tile.Facet.Slot.AttachComponent<BooleanValueDriver<string>>();
        label.FalseValue.Value = "Seated Mode: Off";
        label.TrueValue.Value = "Seated Mode: On";
        label.TargetField.Target = tile.Label.Content;
        label.State.DriveFrom(sync.SeatedMode);
        var icon = tile.Facet.Slot.AttachComponent<BooleanValueDriver<Uri>>();
        icon.FalseValue.Value = OfficialAssets.Graphics.Icons.General.Unseated;
        icon.TrueValue.Value = OfficialAssets.Graphics.Icons.General.Seated;
        icon.TargetField.Target = tile.Icon.URL;
        icon.State.DriveFrom(sync.SeatedMode);
        return tile.Facet;
    }

    private readonly record struct Tile(Facet Facet, Button Button, Text Label, StaticTexture2D Icon);

    private static Tile Build(World world, string name, string label, Uri icon, colorX color)
    {
        var slot = world.AddSlot(name);
        var facet = slot.AttachComponent<Facet>();
        facet.PreferredSizes.Add(new float2(200f, 280f));
        facet.MinSize = new float2(96f, 120f);
        var canvas = facet.Canvas.Target.Slot;

        // The facet background (as FacetPreset.SetupBackground); the whole tile is the button.
        var background = canvas.AddSlot("Background");
        background.AttachComponent<RectTransform>();
        var image = background.AttachComponent<Image>();
        image.NineSliceSizing.Value = NineSliceSizing.FixedSize;
        image.Sprite.Target = RadiantUI_Constants.GetOutlinedSprite(world);
        image.Tint.Value = RadiantUI_Constants.BUTTON_COLOR.MulRGB(0.8f);
        var button = background.AttachComponent<Button>();

        // Icon square on top (fills what the caption leaves), caption underneath.
        var iconBox = background.AddSlot("Icon");
        var iconRect = iconBox.AttachComponent<RectTransform>();
        iconRect.AnchorMin.Value = new float2(0f, 0.24f);
        iconRect.OffsetMin.Value = new float2(14f, 0f);
        iconRect.OffsetMax.Value = new float2(-14f, -14f);
        var square = iconBox.AttachComponent<AspectRatioFitter>();
        square.AspectRatio.Value = 1f;
        var box = iconBox.AttachComponent<Image>();
        box.Sprite.Target = RadiantUI_Constants.GetOutlinedSprite(world);
        box.NineSliceSizing.Value = NineSliceSizing.FixedSize;
        box.Tint.Value = color;
        var glyph = iconBox.AddSlot("Glyph");
        var glyphRect = glyph.AttachComponent<RectTransform>();
        glyphRect.AnchorMin.Value = new float2(0.18f, 0.18f);
        glyphRect.AnchorMax.Value = new float2(0.82f, 0.82f);
        var glyphImage = glyph.AttachComponent<Image>();
        var sprite = glyph.AttachSprite(icon, getExisting: false);
        glyphImage.Sprite.Target = sprite;
        glyphImage.PreserveAspect.Value = true;

        var caption = background.AddSlot("Caption");
        var captionRect = caption.AttachComponent<RectTransform>();
        captionRect.AnchorMax.Value = new float2(1f, 0.24f);
        captionRect.OffsetMin.Value = new float2(8f, 6f);
        captionRect.OffsetMax.Value = new float2(-8f, 0f);
        var text = caption.AttachComponent<Text>();
        text.Content.Value = label;
        text.Align = Alignment.MiddleCenter;
        text.Size.Value = 22f;
        text.AutoSizeMin.Value = 10f;
        text.AutoSizeMax.Value = 22f;
        text.AutoSize = true;
        text.Color.Value = colorX.White;

        return new Tile(facet, button, text, (StaticTexture2D)sprite.Texture.Target);
    }

    // Tiles built by an earlier version with a stand-in icon get the tracker icon.
    private static readonly Uri[] OldCalibratorIcons = [OfficialAssets.Graphics.Icons.Dash.Armature];

    public static void EnsureCalibratorIcon(World userspace)
    {
        foreach (var facet in userspace.RootSlot.GetComponentsInChildren<Facet>(f => f.Slot.Name == "Full Body Calibrator Tile"))
        {
            var texture = facet.Slot.FindChildInHierarchy("Glyph")?.GetComponent<StaticTexture2D>();
            if (texture != null && Array.IndexOf(OldCalibratorIcons, texture.URL.Value) >= 0) texture.URL.Value = CalibratorIcon;
        }
    }
}
