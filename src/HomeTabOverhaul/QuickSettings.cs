using System;
using System.Linq;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.UIX;
using ResoniteModLoader;

namespace HomeTabOverhaul;

// The Quick Settings facets the mod builds:
//   - Earmuff and Edit Dash UI: toggle buttons made only of engine parts (a ButtonToggle on a field kept in sync with
//     the game setting / UI Edit mode, and a label driven from it), so they work without the mod too.
//   - Dimness: a slider (0–80 %) that darkens everything you see (Dimmer). Its value is the slider's own, saved with
//     the dash; the mod applies it.
//   - Dash Colour: a row of colour swatches that tint the background of every dash screen. The chosen swatch is kept
//     in a ValueField on the facet, saved with the dash; the mod applies it.
internal static class QuickSettings
{
    public const string DimnessTag = "HomeTabOverhaul.Dimness";
    public const string TintFacetTag = "HomeTabOverhaul.TintFacet";
    public const string TintPrefix = "HomeTabOverhaul.Tint:";

    private static readonly colorX Teal = new(0.1f, 0.5f, 0.5f);
    private static readonly colorX Purple = new(0.45f, 0.25f, 0.75f);

    // Swatch 0 is the dash's own background; the rest are dark tints (sRGB), with the same see-through-ness.
    private static readonly (string Name, colorX Color)[] Tints =
    [
        ("Default", UserspaceRadiantDash.DEFAULT_BACKGROUND),
        ("Navy", colorX.FromHexCode("#1a2e5c")),
        ("Purple", colorX.FromHexCode("#341f5b")),
        ("Teal", colorX.FromHexCode("#14444a")),
        ("Forest", colorX.FromHexCode("#1c4129")),
        ("Wine", colorX.FromHexCode("#4a192e")),
        ("Amber", colorX.FromHexCode("#4d3213")),
    ];

    // The colour in use, so screens built later (older screens build their background the first time they're shown)
    // can be tinted when they appear.
    private static colorX? _tint;

    // ---- Toggles ------------------------------------------------------------------------------------------------

    public static Facet Earmuff(World world)
    {
        var (facet, button, label) = Actions.BuildButtonFacet(world, "Earmuff: Off", OfficialAssets.Graphics.Icons.General.SpatializedAudio, Teal);
        var state = facet.Slot.AttachComponent<ValueField<bool>>();
        state.Value.SyncWithSetting<bool, EarmuffSettings>(nameof(EarmuffSettings.EarmuffEnabled));
        Toggle(facet, button, label, state.Value, "Earmuff: On", "Earmuff: Off");
        return facet;
    }

    public static Facet EditDashUI(World world)
    {
        var (facet, button, label) = Actions.BuildButtonFacet(world, "Edit Dash UI: Off", OfficialAssets.Graphics.Icons.General.EditText, Purple);
        // Writing this sync's value in userspace switches UI Edit mode (and it follows the mode when changed elsewhere).
        var sync = facet.Slot.AttachComponent<UserInterfaceEditModeSync>();
        Toggle(facet, button, label, sync.EditModeActive, "Edit Dash UI: On", "Edit Dash UI: Off");
        return facet;
    }

    private static void Toggle(Facet facet, Button button, Text label, IField<bool> state, string on, string off)
    {
        button.Slot.AttachComponent<ButtonToggle>().TargetValue.Target = state;
        var text = facet.Slot.AttachComponent<BooleanValueDriver<string>>();
        text.TrueValue.Value = on;
        text.FalseValue.Value = off;
        text.TargetField.Target = label.Content;
        text.State.DriveFrom(state);
    }

    // ---- Dimness ------------------------------------------------------------------------------------------------

    public static Facet Dimness(World world)
    {
        var (facet, ui) = PanelFacet(world, "Dimness", new float2(390f, 120f));
        ui.Style.MinHeight = 40f;
        var title = ui.Text((LocaleString)"Dimness");
        title.Color.Value = colorX.White;
        var slider = ui.Slider(40f, 0f, 0f, 0.8f);
        slider.Slot.Tag = DimnessTag;
        return facet;
    }

    // ---- Dash colour ------------------------------------------------------------------------------------------------

    public static Facet DashTint(World world)
    {
        var (facet, ui) = PanelFacet(world, "Dash Colour", new float2(1080f, 180f));
        facet.Slot.Tag = TintFacetTag;
        facet.Slot.AttachComponent<ValueField<int>>();
        ui.Style.MinHeight = 40f;
        var title = ui.Text((LocaleString)"Dash Colour");
        title.Color.Value = colorX.White;
        ui.Style.MinHeight = 64f;
        ui.Style.FlexibleHeight = 1f;
        ui.HorizontalLayout(10f);
        ui.Style.FlexibleWidth = 1f;
        for (var i = 0; i < Tints.Length; i++)
        {
            var swatch = ui.Button((LocaleString)Tints[i].Name, colorX.White);
            swatch.Slot.Tag = TintPrefix + i;
            HomeBuilder.StyleListButton(swatch, Tints[i].Color.SetA(1f).MulRGB(i == 0 ? 2.5f : 1.2f), 20f);
        }
        ui.NestOut();
        return facet;
    }

    public static bool IsTintButton(Button button) =>
        button.Slot.Tag != null && button.Slot.Tag.StartsWith(TintPrefix, StringComparison.Ordinal);

    public static void OnTintPressed(Button button)
    {
        if (!int.TryParse(button.Slot.Tag.Substring(TintPrefix.Length), out var index) || index < 0 || index >= Tints.Length) return;
        var dash = button.Slot.GetComponentInParents<RadiantDash>();
        var store = button.Slot.GetComponentInParents<Facet>()?.Slot.GetComponent<ValueField<int>>();
        if (dash == null) return;
        if (store != null) store.Value.Value = index;
        ApplyTint(dash, Tints[index].Color);
        store?.NotifyModified();
        ResoniteMod.Msg($"Dash colour set to {Tints[index].Name}.");
    }

    // Every dash screen's background.
    private static void ApplyTint(RadiantDash dash, colorX color)
    {
        _tint = color.SetA(UserspaceRadiantDash.DEFAULT_BACKGROUND.a);
        foreach (var screen in dash.Screens) TintScreen(screen, _tint.Value);
    }

    // The mod switched off: every dash screen back to its own background, and no dimming.
    public static void Off(RadiantDash dash)
    {
        foreach (var screen in dash.Screens) TintScreen(screen, UserspaceRadiantDash.DEFAULT_BACKGROUND);
        _tint = null;
        Dimmer.Set(0f);
    }

    // Called when any dash screen is shown: older screens (Settings, Inventory, Contacts…) build their background then,
    // and don't save it, so it has to be tinted again.
    public static void OnScreenShown(RadiantDashScreen screen)
    {
        if (_tint.HasValue) TintScreen(screen, _tint.Value);
    }

    private static void TintScreen(RadiantDashScreen screen, colorX color)
    {
        if (screen is GridContainerScreen grid && grid.GridContainer != null)
        {
            SetTint(grid.GridContainer.BackgroundRoot.Slot, color);
            // The Home without tabs has its own.
            var flat = grid.ScreenCanvas?.Slot.FindChild(HomeModes.FlatName)?.GetComponent<GridContainer>();
            if (flat != null) SetTint(flat.BackgroundRoot.Slot, color);
            // The Home's tab half has its own background.
            var panelBackground = grid.ScreenCanvas?.Slot.FindChild(HomeBuilder.PanelName)?.FindChild("Background");
            if (panelBackground != null) SetTint(panelBackground, color);
            return;
        }
        // Older screens: the first thing on the canvas is the background image (RadiantDashScreen.BuildBackground);
        // their Background field isn't wired to it.
        if (screen is LegacyRadiantScreenWrapper legacy) legacy.Background.Value = color;
        var canvas = screen.ScreenCanvas?.Slot;
        if (canvas != null && canvas.ChildrenCount > 0) SetTint(canvas[0], color);
    }

    // Only images that look like a dash background (its see-through-ness), so nothing else gets recoloured.
    private static void SetTint(Slot slot, colorX color)
    {
        var tiled = slot.GetComponent<TiledRawImage>();
        if (tiled != null && !tiled.Tint.IsDriven && IsBackground(tiled.Tint.Value)) tiled.Tint.Value = color;
        var image = slot.GetComponent<Image>();
        if (image != null && !image.Tint.IsDriven && IsBackground(image.Tint.Value)) image.Tint.Value = color;
    }

    private static bool IsBackground(colorX tint) => MathX.Abs(tint.a - UserspaceRadiantDash.DEFAULT_BACKGROUND.a) < 0.02f;

    // ---- Applying saved values when the Home loads -------------------------------------------------------------------

    public static void Apply(RadiantDash dash)
    {
        // The Home with tabs and the one without each have their own; the saved values come from the one in use.
        var flat = HomeModes.FlatShown;
        foreach (var slider in dash.Slot.GetComponentsInChildren<Slider<float>>(s => s.Slot.Tag == DimnessTag))
        {
            if (HomeModes.IsFlatFacet(slider.Slot) == flat) Dimmer.Set(slider.Value.Value);
            slider.Value.OnValueChange -= OnDimnessChanged;
            slider.Value.OnValueChange += OnDimnessChanged;
        }
        var tint = dash.Slot.GetComponentsInChildren<Facet>(f => f.Slot.Tag == TintFacetTag)
            .FirstOrDefault(f => HomeModes.IsFlatFacet(f.Slot) == flat)?.Slot.GetComponent<ValueField<int>>();
        if (tint != null && tint.Value.Value > 0 && tint.Value.Value < Tints.Length) ApplyTint(dash, Tints[tint.Value.Value].Color);
    }

    private static void OnDimnessChanged(SyncField<float> field) => Guard.Actions.Run("changing the dimness", () => Dimmer.Set(field.Value));

    // A facet with the engine facet background and a vertical layout to fill.
    private static (Facet, UIBuilder) PanelFacet(World world, string name, float2 size)
    {
        var slot = world.AddSlot(name);
        var facet = slot.AttachComponent<Facet>();
        facet.PreferredSizes.Add(size);
        facet.MinSize = new float2(200f, 100f);
        var background = facet.Canvas.Target.Slot.AddSlot("Background");
        background.AttachComponent<RectTransform>();
        var image = background.AttachComponent<Image>();
        image.NineSliceSizing.Value = NineSliceSizing.FixedSize;
        image.Sprite.Target = RadiantUI_Constants.GetOutlinedSprite(world);
        image.Tint.Value = RadiantUI_Constants.BUTTON_COLOR.MulRGB(0.8f);
        var ui = new UIBuilder(background);
        RadiantUI_Constants.SetupEditorStyle(ui);
        ui.Style.TextColor = colorX.White;
        ui.VerticalLayout(8f, 14f).ForceExpandHeight.Value = false;
        return (facet, ui);
    }
}

// Darkens everything you see: a black, see-through sphere around your head in userspace, drawn after everything else
// (userspace draws over the world, and this is the last thing in it). Local to you and never saved.
internal static class Dimmer
{
    private static Slot? _sphere;
    private static UnlitMaterial? _material;

    public static void Set(float amount)
    {
        var world = Userspace.UserspaceWorld;
        if (world == null) return;
        world.RunSynchronously(() => Guard.Actions.Run("dimming", () =>
        {
            amount = MathX.Clamp01(amount);
            if (_sphere == null || _sphere.IsRemoved)
            {
                if (amount < 0.01f) return;
                var head = world.LocalUser?.Root?.HeadSlot;
                if (head == null) return;
                _sphere = head.AddLocalSlot("HomeTabOverhaul Dimness");
                var mesh = _sphere.AttachComponent<SphereMesh>();
                mesh.Radius.Value = 0.12f;
                mesh.DualSided.Value = true;
                _material = _sphere.AttachComponent<UnlitMaterial>();
                _material.BlendMode.Value = BlendMode.Alpha;
                _material.ZWrite.Value = ZWrite.Off;
                _material.Sidedness.Value = Sidedness.Double;
                _material.RenderQueue.Value = 4500;
                var renderer = _sphere.AttachComponent<MeshRenderer>();
                renderer.Mesh.Target = mesh;
                renderer.Material.Target = _material;
            }
            _material!.TintColor.Value = new colorX(0f, 0f, 0f, amount);
            _sphere.ActiveSelf = amount >= 0.01f;
        }));
    }
}
