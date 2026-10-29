using Elements.Core;
using FrooxEngine;
using FrooxEngine.UIX;
using Renderite.Shared;

namespace HomeTabOverhaul;

// A dock tab that shows you, like a mirror, built by the mod (in place of the cloud mirror tabs). It works the way
// those do: a camera inside the dash screen, where the dash is in front of you, turned round to face you, and its
// picture shown across the screen, flipped so it reads like a mirror. The camera sits under the screen's own root,
// so it only renders while the tab is open.
// Under the picture: Mirror / Camera (flipped or not) and Near / Normal / Wide (field of view). All engine
// components, so it keeps working without the mod.
internal static class DashMirror
{
    private static readonly colorX Teal = colorX.FromHexCode("#2a9da6");

    public static RadiantDashScreen Build(RadiantDash dash)
    {
        var screen = dash.AttachScreen("Mirror", Teal, OfficialAssets.Graphics.Icons.General.Mirror);
        var canvas = screen.ScreenCanvas.Slot;
        var ui = new UIBuilder(canvas);
        RadiantUI_Constants.SetupEditorStyle(ui, extraPadding: true);
        ui.Style.TextColor = colorX.White;
        screen.BuildBackground(ui, nest: false);

        var cameraSlot = canvas.AddSlot("Mirror Camera");
        cameraSlot.LocalRotation = floatQ.AxisAngle(float3.Up, 180f); // looking back out of the dash, at you
        var camera = cameraSlot.AttachComponent<Camera>();
        var texture = cameraSlot.AttachComponent<RenderTextureProvider>();
        texture.Size.Value = new int2(1920, 1080);
        camera.RenderTexture.Target = texture;
        camera.FieldOfView.Value = 60f;
        camera.NearClipping.Value = 0.01f;
        camera.UseTransformScale.Value = false;
        camera.Clear.Value = CameraClearMode.Skybox;

        var view = canvas.AddSlot("View");
        view.AttachComponent<RectTransform>();
        var image = view.AttachComponent<RawImage>();
        image.Texture.Target = texture;
        var mirrored = view.AttachComponent<ValueField<bool>>();
        mirrored.Value.Value = true;
        var uv = view.AttachComponent<BooleanValueDriver<Rect>>();
        uv.TrueValue.Value = new Rect(1f, 0f, -1f, 1f);
        uv.FalseValue.Value = new Rect(0f, 0f, 1f, 1f);
        uv.TargetField.Target = image.UVRect;
        uv.State.DriveFrom(mirrored.Value);

        // The controls along the bottom.
        var bar = canvas.AddSlot("Controls");
        var barRect = bar.AttachComponent<RectTransform>();
        barRect.AnchorMin.Value = new float2(0.5f, 0f);
        barRect.AnchorMax.Value = new float2(0.5f, 0f);
        barRect.OffsetMin.Value = new float2(-420f, 24f);
        barRect.OffsetMax.Value = new float2(420f, 96f);
        var controls = new UIBuilder(bar);
        RadiantUI_Constants.SetupEditorStyle(controls, extraPadding: true);
        controls.Style.TextColor = colorX.White;
        controls.HorizontalLayout(10f);
        controls.Style.FlexibleWidth = 1f;

        var flip = controls.Button((LocaleString)"Mirror", colorX.White);
        HomeBuilder.StyleListButton(flip, HomeBuilder.Navy, 28f);
        flip.Slot.AttachComponent<ButtonToggle>().TargetValue.Target = mirrored.Value;
        var flipLabel = flip.Slot.AttachComponent<BooleanValueDriver<string>>();
        flipLabel.TrueValue.Value = "Mirror";
        flipLabel.FalseValue.Value = "Camera";
        flipLabel.TargetField.Target = flip.Slot.GetComponentInChildren<Text>().Content;
        flipLabel.State.DriveFrom(mirrored.Value);

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
        return screen;
    }
}
