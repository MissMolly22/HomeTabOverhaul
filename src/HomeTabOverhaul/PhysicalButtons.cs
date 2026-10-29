using System;
using Elements.Core;
using FrooxEngine;

namespace HomeTabOverhaul;

// Small solid buttons for the Local Mirror and Local Light: an opaque box you press (TouchButton, so lasers and
// fingers both work) with an icon or a word on its front (the -Z side, toward the user) and the same on its back, so
// they read from behind too. Solid materials so they show against the skybox, where see-through UI canvases vanish.
// All engine components.
internal static class PhysicalButtons
{
    public const float Height = 0.06f;
    public const float Depth = 0.02f;

    public static readonly colorX Grey = new(0.42f, 0.42f, 0.46f);
    public static readonly colorX Green = colorX.FromHexCode("#3ccf5a");
    public static readonly colorX Orange = colorX.FromHexCode("#ff8a1f");
    public static readonly colorX Blue = colorX.FromHexCode("#6aa6ff");

    public static Slot Box(Slot parent, string name, float3 position, float width = Height, float height = Height, float depth = Depth)
    {
        var slot = parent.AddSlot(name);
        slot.LocalPosition = position;
        var size = new float3(width, height, depth);
        slot.AttachComponent<BoxCollider>().Size.Value = size;
        var mesh = slot.AttachComponent<BoxMesh>();
        mesh.Size.Value = size;
        var material = slot.AttachComponent<PBS_Metallic>();
        material.AlbedoColor.Value = new colorX(0.12f, 0.12f, 0.15f);
        var renderer = slot.AttachComponent<MeshRenderer>();
        renderer.Mesh.Target = mesh;
        renderer.Material.Target = material;
        slot.AttachComponent<TouchButton>();
        return slot;
    }

    // An icon on both faces, sharing one material, coloured by the returned driver: onColor while its State is true,
    // grey otherwise.
    public static BooleanValueDriver<colorX> Icon(Slot button, Uri icon, colorX onColor)
    {
        var size = button.GetComponent<BoxMesh>().Size.Value;
        var material = button.AttachComponent<UnlitMaterial>();
        material.Texture.Target = button.AttachTexture(icon);
        material.BlendMode.Value = BlendMode.Cutout;
        material.AlphaCutoff.Value = 0.5f;
        foreach (var side in Faces(button, "Icon", size))
        {
            var quad = side.AttachComponent<QuadMesh>();
            quad.Size.Value = float2.One * (MathX.Min(size.x, size.y) * 0.75f);
            var renderer = side.AttachComponent<MeshRenderer>();
            renderer.Mesh.Target = quad;
            renderer.Material.Target = material;
        }
        return ColorDriver(button, material.TintColor, onColor);
    }

    // A word on both faces, coloured the same way (the back copies the front's colour).
    public static BooleanValueDriver<colorX> Label(Slot button, string text, colorX onColor)
    {
        var size = button.GetComponent<BoxMesh>().Size.Value;
        TextRenderer? front = null;
        foreach (var side in Faces(button, "Label", size))
        {
            side.LocalScale = float3.One * (size.y * 1.5f);
            var renderer = side.AttachComponent<TextRenderer>();
            renderer.Text.Value = text;
            if (front == null) front = renderer;
            else renderer.Color.DriveFrom(front.Color);
        }
        return ColorDriver(button, front!.Color, onColor);
    }

    // A slot just off the front face, and one just off the back turned around to face the other way.
    private static Slot[] Faces(Slot button, string name, float3 size)
    {
        var offset = size.z / 2f + 0.0005f;
        var front = button.AddSlot(name);
        front.LocalPosition = new float3(0f, 0f, -offset);
        var back = button.AddSlot(name + " (back)");
        back.LocalPosition = new float3(0f, 0f, offset);
        back.LocalRotation = floatQ.AxisAngle(float3.Up, 180f);
        return [front, back];
    }

    private static BooleanValueDriver<colorX> ColorDriver(Slot button, IField<colorX> target, colorX onColor)
    {
        var driver = button.AttachComponent<BooleanValueDriver<colorX>>();
        driver.TrueValue.Value = onColor;
        driver.FalseValue.Value = Grey;
        driver.TargetField.Target = target;
        return driver;
    }
}
