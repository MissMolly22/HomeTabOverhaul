using Elements.Core;
using FrooxEngine;
using FrooxEngine.Undo;
using Renderite.Shared;

namespace HomeTabOverhaul;

// A small warm light only you can see, built like the Local Mirror: a normal grabbable object whose "Content" is
// switched on for you alone (ValueUserOverride), so the light only lights your view. Same grab and pin buttons.
// Clicking the orb itself switches the light on and off (a TouchButton + ButtonToggle); the orb stays, dimmed, when
// off. Deleting it is the usual delete.
internal static class LocalLight
{
    private const float OrbRadius = 0.06f;
    private static readonly colorX Warm = new(1f, 0.85f, 0.6f);

    public static void Spawn(World world)
    {
        world.RunSynchronously(() =>
        {
            if (!world.CanSpawnObjects())
            {
                NotificationMessage.SpawnTextMessage("Permissions.NotAllowedToSpawn".AsLocaleKey(), colorX.Red);
                return;
            }
            var root = world.RootSlot.LocalUserSpace.AddSlot("Local Light");
            root.AttachComponent<ObjectRoot>();
            var grabbable = root.AttachComponent<Grabbable>();
            grabbable.Scalable.Value = true;

            var content = LocalMirror.AddPerUserContent(root);

            var collider = content.AttachComponent<SphereCollider>();
            collider.Radius.Value = OrbRadius;
            var mesh = content.AttachComponent<SphereMesh>();
            mesh.Radius.Value = OrbRadius;
            var material = content.AttachComponent<UnlitMaterial>();
            material.TintColor.Value = Warm;
            var renderer = content.AttachComponent<MeshRenderer>();
            renderer.Mesh.Target = mesh;
            renderer.Material.Target = material;

            var light = content.AttachComponent<Light>();
            light.LightType.Value = LightType.Point;
            light.Color.Value = Warm;
            light.Intensity.Value = 1.5f;
            light.Range.Value = 8f;
            light.ShadowType.Value = ShadowType.None;

            content.AttachComponent<TouchButton>();
            content.AttachComponent<ButtonToggle>().TargetValue.Target = light.EnabledField;
            var glow = content.AttachComponent<BooleanValueDriver<colorX>>();
            glow.TrueValue.Value = Warm;
            glow.FalseValue.Value = new colorX(0.25f, 0.22f, 0.2f);
            glow.TargetField.Target = material.TintColor;
            glow.State.DriveFrom(light.EnabledField);

            // Smaller, thinner buttons just under the orb.
            const float button = 0.035f;
            LocalMirror.BuildControls(content, root, grabbable, new float3(-button * 1.25f / 2f, -OrbRadius - button, 0f), button, 0.008f);

            LocalMirror.PlaceFacingUser(root, 0.8f);
            root.CreateSpawnUndoPoint();
        });
    }
}
