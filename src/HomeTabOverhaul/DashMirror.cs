using System.Linq;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.UIX;
using Renderite.Shared;
using ResoniteModLoader;

namespace HomeTabOverhaul;

// One dock tab that shows you, built by the mod (in place of the cloud mirror tabs, whose features it combines):
//   - Mirror / Camera: the picture flipped like a mirror, or as others see you;
//   - Front / Back: the camera in the dash looking at you, or behind you looking the way you face (click the picture to
//     swap, as with the Simple Reversible Mirror);
//   - Near / Normal / Wide: the field of view.
// It works the way those tabs do: a camera inside the dash screen (where the dash is, in front of you) and its picture
// across the screen. The camera sits under the screen's own root, so it only renders while the tab is open. All
// engine components, so it keeps working without the mod.
internal static class DashMirror
{
    public const string Id = "dash-mirror";
    private const string Version = "HomeTabOverhaul.DashMirror.2";
    private static readonly colorX Teal = colorX.FromHexCode("#2a9da6");
    // Behind you: this far from the dash towards you, in the screen's units (as the Simple Reversible Mirror does).
    private const float BackDistance = 2500f;

    public static RadiantDashScreen Build(RadiantDash dash)
    {
        var screen = dash.AttachScreen("Mirror", Teal, OfficialAssets.Graphics.Icons.General.Mirror);
        Fill(screen);
        return screen;
    }

    // Builds (or rebuilds, for one made by an earlier version) the mirror inside the screen.
    public static void Fill(RadiantDashScreen screen)
    {
        var canvas = screen.ScreenCanvas.Slot;
        foreach (var child in canvas.Children.ToList()) child.Destroy();
        canvas.Tag = Version;
        var ui = new UIBuilder(canvas);
        RadiantUI_Constants.SetupEditorStyle(ui, extraPadding: true);
        ui.Style.TextColor = colorX.White;
        screen.BuildBackground(ui, nest: false);

        var settings = canvas.AddSlot("Settings");
        var mirrored = settings.AttachComponent<ValueField<bool>>();
        mirrored.Value.Value = true;
        var back = settings.AttachComponent<ValueField<bool>>();

        var cameraSlot = canvas.AddSlot("Mirror Camera");
        var camera = cameraSlot.AttachComponent<Camera>();
        var texture = cameraSlot.AttachComponent<RenderTextureProvider>();
        texture.Size.Value = new int2(1920, 1080);
        camera.RenderTexture.Target = texture;
        camera.FieldOfView.Value = 60f;
        camera.NearClipping.Value = 0.01f;
        camera.UseTransformScale.Value = false;
        camera.Clear.Value = CameraClearMode.Skybox;
        // Front: at the dash, turned round to look at you. Back: behind you, looking the way you face.
        var position = cameraSlot.AttachComponent<BooleanValueDriver<float3>>();
        position.FalseValue.Value = float3.Zero;
        position.TrueValue.Value = new float3(0f, 0f, -BackDistance);
        position.TargetField.Target = cameraSlot.Position_Field;
        position.State.DriveFrom(back.Value);
        var rotation = cameraSlot.AttachComponent<BooleanValueDriver<floatQ>>();
        rotation.FalseValue.Value = floatQ.AxisAngle(float3.Up, 180f);
        rotation.TrueValue.Value = floatQ.Identity;
        rotation.TargetField.Target = cameraSlot.Rotation_Field;
        rotation.State.DriveFrom(back.Value);

        // The picture fills the screen; clicking it swaps front and back.
        var view = canvas.AddSlot("View");
        view.AttachComponent<RectTransform>();
        var image = view.AttachComponent<RawImage>();
        image.Texture.Target = texture;
        var uv = view.AttachComponent<BooleanValueDriver<Rect>>();
        uv.TrueValue.Value = new Rect(1f, 0f, -1f, 1f);
        uv.FalseValue.Value = new Rect(0f, 0f, 1f, 1f);
        uv.TargetField.Target = image.UVRect;
        uv.State.DriveFrom(mirrored.Value);
        var swap = view.AttachComponent<Button>();
        swap.ColorDrivers.Clear(); // the picture keeps its colours when hovered
        view.AttachComponent<ButtonToggle>().TargetValue.Target = back.Value;

        // The controls along the bottom.
        var bar = canvas.AddSlot("Controls");
        var barRect = bar.AttachComponent<RectTransform>();
        barRect.AnchorMin.Value = new float2(0.5f, 0f);
        barRect.AnchorMax.Value = new float2(0.5f, 0f);
        barRect.OffsetMin.Value = new float2(-560f, 24f);
        barRect.OffsetMax.Value = new float2(560f, 96f);
        var controls = new UIBuilder(bar);
        RadiantUI_Constants.SetupEditorStyle(controls, extraPadding: true);
        controls.Style.TextColor = colorX.White;
        controls.HorizontalLayout(10f);
        controls.Style.FlexibleWidth = 1f;

        Toggle(controls, mirrored, "Mirror", "Camera");
        Toggle(controls, back, "Back", "Front");
        foreach (var (name, fov) in new[] { ("Near", 40f), ("Normal", 60f), ("Wide", 85f) })
        {
            var button = controls.Button((LocaleString)name, colorX.White);
            HomeBuilder.StyleListButton(button, HomeBuilder.Navy, 28f);
            var set = button.Slot.AttachComponent<ButtonValueSet<float>>();
            set.TargetValue.Target = camera.FieldOfView;
            set.SetValue.Value = fov;
            // The chosen one is the brighter blue.
            var shade = button.Slot.AttachComponent<BooleanValueDriver<colorX>>();
            shade.TrueValue.Value = HomeBuilder.NavyOpen;
            shade.FalseValue.Value = HomeBuilder.Navy;
            shade.TargetField.Target = button.BaseColor;
            var chosen = button.Slot.AttachComponent<ValueEqualityDriver<float>>();
            chosen.TargetValue.Target = camera.FieldOfView;
            chosen.Reference.Value = fov;
            chosen.Target.Target = shade.State;
        }
        screen.NotifyModified();
    }

    // A button that flips a setting, labelled with the current state.
    private static void Toggle(UIBuilder ui, ValueField<bool> state, string whenOn, string whenOff)
    {
        var button = ui.Button((LocaleString)whenOn, colorX.White);
        HomeBuilder.StyleListButton(button, HomeBuilder.Navy, 28f);
        button.Slot.AttachComponent<ButtonToggle>().TargetValue.Target = state.Value;
        var label = button.Slot.AttachComponent<BooleanValueDriver<string>>();
        label.TrueValue.Value = whenOn;
        label.FalseValue.Value = whenOff;
        label.TargetField.Target = button.Slot.GetComponentInChildren<Text>().Content;
        label.State.DriveFrom(state.Value);
    }

    // When the dash loads: one mirror tab. The cloud mirror tabs earlier versions installed (Mirracle, Simple
    // Reversible) are replaced by this one, in the place of the first; a Dash Mirror from an earlier version is
    // rebuilt with the current features.
    public static void Upgrade(RadiantDash dash)
    {
        var old = dash.Screens.Where(s => s.Slot.Tag is DockTabs.InstalledTag + "simple-mirror" or DockTabs.InstalledTag + "mirracle-mirror").ToList();
        var mine = dash.Screens.Where(s => s.Slot.Tag == DockTabs.InstalledTag + Id).ToList();
        if (old.Count > 0 && mine.Count == 0)
        {
            var place = old[0].Slot;
            var screen = Build(dash);
            screen.Slot.Tag = DockTabs.InstalledTag + Id;
            if (place.Parent != dash.ScreensContainer) screen.Slot.SetParent(place.Parent, keepGlobalTransform: false);
            screen.Slot.SetIdentityTransform();
            screen.Slot.OrderOffset = place.OrderOffset;
            mine.Add(screen);
        }
        foreach (var screen in old)
        {
            ResoniteMod.Msg($"Replaced the cloud mirror tab \"{screen.Label.Value}\" with the Dash Mirror.");
            if (dash.CurrentScreen.Target == screen) dash.CurrentScreen.Target = mine.FirstOrDefault();
            screen.Slot.Destroy();
        }
        foreach (var screen in mine.Where(s => s.ScreenCanvas?.Slot.Tag != Version))
        {
            Fill(screen);
            ResoniteMod.Msg("Updated the Dash Mirror.");
        }
    }
}
