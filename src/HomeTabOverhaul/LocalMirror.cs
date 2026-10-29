using Elements.Core;
using FrooxEngine;
using FrooxEngine.Undo;

namespace HomeTabOverhaul;

// A plain mirror only you can see, spawned in front of you in the world you're in. It's a normal (networked) object so
// it can be grabbed, scaled, duplicated and deleted, but everything visible sits under "Content", whose Active is a
// per-user value: on for you, off for everyone else (ValueUserOverride, the same thing per-user UI uses). Others see
// nothing and can't grab it, and it costs them no rendering.
// The mirror is built like the default Resonite one: a quad with a ReflectionMaterial fed by a CameraPortal in Mirror
// mode. Under it is a row of small solid buttons: a hand switches the Grabbable on and off, a pin parents the mirror to your
// user root so it follows you (press again to put it back), and Low / Medium / High set the mirror's resolution. All
// plain engine components.
internal static class LocalMirror
{
    private static readonly float2 Size = new(0.8f, 1.4f);

    // Render texture sizes, all the mirror's 4:7 shape.
    private static readonly (string Name, int2 Size)[] Resolutions =
    [
        ("Low", new int2(512, 896)),
        ("Medium", new int2(1024, 1792)),
        ("High", new int2(2048, 3584)),
    ];

    public static void Spawn(World world)
    {
        world.RunSynchronously(() =>
        {
            if (!world.CanSpawnObjects())
            {
                NotificationMessage.SpawnTextMessage("Permissions.NotAllowedToSpawn".AsLocaleKey(), colorX.Red);
                return;
            }
            var root = world.RootSlot.LocalUserSpace.AddSlot("Local Mirror");
            root.AttachComponent<ObjectRoot>();
            var grabbable = root.AttachComponent<Grabbable>();
            grabbable.Scalable.Value = true;

            var content = AddPerUserContent(root);

            var collider = content.AttachComponent<BoxCollider>();
            collider.Size.Value = new float3(Size.x + 0.04f, Size.y + 0.04f, 0.03f);

            var surface = content.AddSlot("Surface");
            var quad = surface.AttachComponent<QuadMesh>();
            quad.Size.Value = Size;
            var renderer = surface.AttachComponent<MeshRenderer>();
            var material = surface.AttachComponent<ReflectionMaterial>();
            renderer.Mesh.Target = quad;
            renderer.Material.Target = material;

            var camera = surface.AddSlot("Camera");
            var texture = camera.AttachComponent<RenderTextureProvider>();
            texture.Size.Value = Resolutions[1].Size;
            var portal = camera.AttachComponent<CameraPortal>();
            portal.Renderer.Target = renderer;
            portal.ReflectionTexture.Target = texture;
            portal.RenderMode.Value = CameraPortal.Mode.Mirror;
            portal.PlaneNormal.Value = new float3(0f, 0f, -1f);
            portal.PlaneOffset.Value = 0.001f;
            portal.OverrideNearClip.Value = 0.01f;
            material.ReflectionTexture.Target = texture;

            // A thin dark frame behind the glass, so the back and the edges read as an object.
            var frame = content.AddSlot("Frame");
            frame.LocalPosition = new float3(0f, 0f, 0.012f);
            var box = frame.AttachComponent<BoxMesh>();
            box.Size.Value = new float3(Size.x + 0.04f, Size.y + 0.04f, 0.02f);
            var frameRenderer = frame.AttachComponent<MeshRenderer>();
            var frameMaterial = frame.AttachComponent<PBS_Metallic>();
            frameMaterial.AlbedoColor.Value = new colorX(0.08f, 0.08f, 0.1f);
            frameRenderer.Mesh.Target = box;
            frameRenderer.Material.Target = frameMaterial;

            // Under the frame: grab and pin on the left, the resolutions on the right.
            var y = -Size.y / 2f - 0.02f - PhysicalButtons.Height * 0.75f;
            BuildControls(content, root, grabbable, new float3(-Size.x / 2f + 0.07f, y, 0f));
            for (var i = 0; i < Resolutions.Length; i++)
            {
                var (name, size) = Resolutions[i];
                var button = PhysicalButtons.Box(content, name, new float3(Size.x / 2f - 0.06f - (Resolutions.Length - 1 - i) * 0.11f, y, 0f), 0.1f);
                var set = button.AttachComponent<ButtonValueSet<int2>>();
                set.TargetValue.Target = texture.Size;
                set.SetValue.Value = size;
                // The chosen resolution's word is blue.
                var shade = PhysicalButtons.Label(button, name, PhysicalButtons.Blue);
                var isChosen = button.AttachComponent<ValueEqualityDriver<int2>>();
                isChosen.TargetValue.Target = texture.Size;
                isChosen.Reference.Value = size;
                isChosen.Target.Target = shade.State;
            }

            PlaceFacingUser(root, 1.2f);
            root.CreateSpawnUndoPoint();
        });
    }

    // "Content" under the root, shown only to the local user. Its front (-Z, the side the glass and the buttons face) is
    // turned to the root's +Z, which PlaceFacingUser points at you. Also used by LocalLight.
    public static Slot AddPerUserContent(Slot root)
    {
        var content = root.AddSlot("Content");
        var visible = root.AttachComponent<ValueUserOverride<bool>>();
        visible.Default.Value = false;
        visible.PersistentOverrides.Value = true;
        visible.ClearOnUserLeave.Value = false;
        visible.Target.Target = content.ActiveSelf_Field;
        visible.SetOverride(root.World.LocalUser, true);
        content.LocalRotation = floatQ.AxisAngle(float3.Up, 180f);
        return content;
    }

    // In front of you, upright, turned to face your head whichever way you're looking.
    public static void PlaceFacingUser(Slot root, float distance)
    {
        root.PositionInFrontOfUser(null, null, distance);
        var user = root.World.LocalUser.Root;
        if (user == null) return;
        var up = user.Slot.Up;
        var toUser = user.HeadPosition - root.GlobalPosition;
        toUser -= up * MathX.Dot(toUser, up);
        if (MathX.Dot(toUser, toUser) < 1e-6f) return;
        var forward = toUser.Normalized;
        root.GlobalRotation = floatQ.LookRotation(in forward, in up);
    }

    // Two solid buttons side by side, the left one at `position`: a hand (grabbing on/off; green when on, grey when
    // off) and a pin (follow you / put back; orange while pinned). Also used by LocalLight.
    public static void BuildControls(Slot content, Slot root, Grabbable grabbable, float3 position, float size = PhysicalButtons.Height, float depth = PhysicalButtons.Depth)
    {
        var grab = PhysicalButtons.Box(content, "Grab", position, size, size, depth);
        grab.AttachComponent<ButtonToggle>().TargetValue.Target = grabbable.EnabledField;
        PhysicalButtons.Icon(grab, OfficialAssets.Common.Icons.Hand, PhysicalButtons.Green).State.DriveFrom(grabbable.EnabledField);

        var pin = PhysicalButtons.Box(content, "Pin", position + new float3(size * 1.25f, 0f, 0f), size, size, depth);
        var parentUnder = pin.AttachComponent<ButtonParentUnderUser>();
        parentUnder.Root.Target = root;
        // While pinned, ButtonParentUnderUser remembers where the object was; that's the pinned state.
        var pinned = pin.AttachComponent<ReferenceEqualityDriver<Slot>>();
        pinned.TargetReference.Target = parentUnder._originalSpace;
        pinned.Invert.Value = true;
        pinned.Target.Target = PhysicalButtons.Icon(pin, OfficialAssets.Common.Icons.Pin, PhysicalButtons.Orange).State;
    }
}
