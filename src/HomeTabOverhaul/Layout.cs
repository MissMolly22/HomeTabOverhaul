using System;
using Elements.Core;
using FrooxEngine;

namespace HomeTabOverhaul;

// The default contents of the new Home. Rectangles are grid cells: [X, Y, W, H] with Y counting UP from the bottom,
// the same as the engine's own HomeScreenInitializer. Everything here is only the starting point; people rearrange
// it with UI Edit mode afterwards.
internal static class Layout
{
    // Bump when the panel's structure changes: a Home built with an older layout is rebuilt on the next load.
    public const string Version = "HomeTabOverhaul.layout.15";

    // The screen grid the engine uses is 64 x 36 cells. The fixed facets take the top 15 rows across the whole width;
    // below them the tab column takes the left 12 columns, the tab grids the middle, and a side grid (always shown,
    // holding Worlds You're In) the right 14.
    public const int TotalColumns = 64;
    public const int TotalRows = 36;
    public const int FixedRows = 15;
    public const int TabRows = TotalRows - FixedRows;
    public const int TabListColumns = 12;
    public const int SideColumns = 14;
    public const int TabColumns = TotalColumns - TabListColumns - SideColumns;
    public const float SplitY = TabRows / (float)TotalRows;
    public const float TabListRight = TabListColumns / (float)TotalColumns;
    public const float SideLeft = (TotalColumns - SideColumns) / (float)TotalColumns;
    public const float Padding = 8f;
    public const float TabButtonHeight = 60f;
    public const float PagerHeight = 36f;
    public const float TabListFallbackHeight = 600f; // the column's height before it has been laid out

    // A built-in facet (Preset) or one the mod builds itself (Build: button facets and icon tiles).
    public record struct Placement(Type? Preset, int X, int Y, int W, int H, Func<World, Facet>? Build = null)
    {
        public IntRect Rect => new(new int2(X, Y), new int2(W, H));
    }

    // A published facet loaded from the cloud into the tab once it's built.
    public record Install(Uri Record, int X, int Y, int W, int H)
    {
        public IntRect Rect => new(new int2(X, Y), new int2(W, H));
    }

    public record Tab(string Name, Placement[] Facets, Install[]? Installs = null);

    // The Dock tab (the dock editor) comes after the facet tabs.
    public static int DockTabId => Tabs.Length;

    // Top, as Molly arranged it (2026-10-28): the online status on the left (wide enough to show its labels), the
    // profile with storage under it beside it, then a row of big icon tiles; the batteries take the right end.
    public static readonly Placement[] Fixed =
    [
        new(typeof(OnlineStatusFacetPreset), 0, 2, 9, 13),
        new(typeof(SimpleProfileFacetPreset), 9, 7, 16, 8),
        new(typeof(StorageIndicatorFacetPreset), 9, 2, 16, 4),
        new(null, 25, 3, 7, 10, Tiles.OpenHome),
        new(null, 32, 3, 7, 10, Tiles.FullBodyCalibrator),
        new(null, 39, 3, 7, 10, Tiles.SeatedMode),
        new(null, 46, 3, 7, 10, Tiles.Respawn),
    ];

    // Three 12-wide buttons per row, filled from the top of the 38 x 21 tab grid.
    private static Placement Cell(Type preset, int column, int row) => new(preset, column * 12, TabRows - 4 - row * 4, 12, 4);
    private static Placement Cell(Actions.Def action, int column, int row) =>
        new(null, column * 12, TabRows - 4 - row * 4, 12, 4, world => Actions.BuildFacet(world, action));

    public static readonly Tab[] Tabs =
    [
        new("Create",
        [
            Cell(typeof(AvatarCreatorFacetPreset), 0, 0),
            Cell(typeof(NewWorldFacetPreset), 1, 0),
            Cell(typeof(ClipboardPasteFacetPreset), 2, 0),
            Cell(Actions.RedX, 0, 1),
            Cell(Actions.BookStand, 1, 1),
        ]),
        new("Streaming/Camera",
        [
            Cell(typeof(StreamingCameraFacetPreset), 0, 0),
            Cell(typeof(StreamAudioFacetPreset), 1, 0),
            Cell(typeof(StreamVideoFacetPreset), 2, 0),
        ]),
        new("Quick Settings",
        [
            Cell(typeof(NameplateVisibilityFacetPreset), 0, 0),
            Cell(typeof(NameplateStyleFacetPreset), 1, 0),
            Cell(typeof(FreeformDashFacetPreset), 2, 0),
            Cell(typeof(Setup2FAFacetPreset), 0, 1),
            Cell(typeof(OpenDebugFacetPreset), 1, 1),
            new(null, 24, TabRows - 8, 12, 4, QuickSettings.Earmuff),
            new(null, 0, TabRows - 12, 12, 4, QuickSettings.EditDashUI),
            new(null, 12, TabRows - 12, 12, 4, QuickSettings.Dimness),
            new(null, 0, TabRows - 18, 36, 5, QuickSettings.DashTint),
        ]),
        // The three status facets stacked on the left, the events list filling the rest so it's easy to read.
        new("Platform Info",
        [
            Cell(typeof(VersionNumberFacetPreset), 0, 0),
            Cell(typeof(OnlineUserCountFacetPreset), 0, 1),
            Cell(typeof(ServerStatusFacetPreset), 0, 2),
        ], [new(Actions.CommunityEvents, 12, 0, TabColumns - 12, TabRows)]),
        new("Local Mirrors/Tools",
        [
            Cell(Actions.MirracleMirror, 0, 0),
            Cell(Actions.SimpleMirror, 1, 0),
            Cell(Actions.ResoniteMirror, 2, 0),
            Cell(Actions.LocalMirrorButton, 0, 1),
            Cell(Actions.LocalLightButton, 1, 1),
        ]),
    ];

    // The Home without tabs (the "Home tabs" setting off, or the mod switched off with no "Home (old)" to go back to):
    // one 64 x 36 grid with the game's own facets where its default Home puts them, and the mod's facets in the space
    // the game leaves free (the middle, and the top right).
    private static Placement Flat(Type preset, int x, int y, int w, int h) => new(preset, x, y, w, h);
    private static Placement Flat(Actions.Def action, int x, int y, int w = 9) =>
        new(null, x, y, w, 3, world => Actions.BuildFacet(world, action));

    public static readonly Placement[] FlatFacets =
    [
        // The game's default Home (HomeScreenInitializer).
        Flat(typeof(SimpleProfileFacetPreset), 0, 28, 20, 8),
        Flat(typeof(OnlineStatusFacetPreset), 20, 28, 10, 8),
        Flat(typeof(NameplateStyleFacetPreset), 53, 25, 11, 3),
        Flat(typeof(NameplateVisibilityFacetPreset), 53, 22, 11, 3),
        Flat(typeof(ClipboardPasteFacetPreset), 53, 16, 11, 3),
        Flat(typeof(SeatedModeFacetPreset), 53, 13, 11, 3),
        Flat(typeof(FreeformDashFacetPreset), 53, 10, 11, 3),
        Flat(typeof(Setup2FAFacetPreset), 53, 7, 11, 3),
        Flat(typeof(OpenDebugFacetPreset), 53, 4, 11, 3),
        Flat(typeof(FullBodyCalibratorFacetPreset), 0, 25, 11, 3),
        Flat(typeof(AvatarCreatorFacetPreset), 0, 22, 11, 3),
        Flat(typeof(StreamingCameraFacetPreset), 0, 19, 11, 3),
        Flat(typeof(StreamAudioFacetPreset), 0, 16, 11, 3),
        Flat(typeof(StreamVideoFacetPreset), 0, 13, 11, 3),
        Flat(typeof(NewWorldFacetPreset), 0, 7, 11, 3),
        Flat(typeof(OpenHomeFacetPreset), 0, 4, 11, 3),
        Flat(typeof(VersionNumberFacetPreset), 56, 0, 8, 4),
        Flat(typeof(OnlineUserCountFacetPreset), 41, 0, 15, 4),
        Flat(typeof(ServerStatusFacetPreset), 28, 0, 13, 4),
        Flat(typeof(StorageIndicatorFacetPreset), 0, 0, 28, 4),
        // The mod's.
        new(null, 30, 28, 6, 8, Tiles.Respawn),
        new(null, 50, 28, 14, 4, QuickSettings.Dimness),
        Flat(Actions.LocalMirrorButton, 11, 15),
        Flat(Actions.LocalLightButton, 20, 15),
        new(null, 29, 15, 10, 3, QuickSettings.Earmuff),
        Flat(Actions.MirracleMirror, 11, 12),
        Flat(Actions.SimpleMirror, 20, 12),
        Flat(Actions.ResoniteMirror, 29, 12, 10),
        new(null, 11, 9, 9, 3, QuickSettings.EditDashUI),
        Flat(Actions.RedX, 20, 9),
        Flat(Actions.BookStand, 29, 9, 10),
        new(null, 11, 4, 28, 5, QuickSettings.DashTint),
    ];

    public static readonly Install[] FlatInstalls =
    [
        new(new Uri(Templates + "LeftControllerBattery"), 36, 32, 14, 4),
        new(new Uri(Templates + "RightControllerBattery"), 36, 28, 14, 4),
        new(new Uri(Templates + "HeadsetBattery"), 50, 32, 14, 4),
        new(Actions.CommunityEvents, 11, 18, 28, 10),
        new(Actions.WorldsYoureIn, 39, 4, 14, 24),
    ];

    // The side grid on the right of the bottom half, shown whichever tab is open.
    public static readonly Install[] Side = [new(Actions.WorldsYoureIn, 0, 0, SideColumns, TabRows)];

    // The game's own battery facets (cloud templates), stacked at the right end of the top.
    private const string Templates = "resrec:///G-Resonite/Inventory/Facets/Templates/";
    public static readonly Install[] FixedInstalls =
    [
        new(new Uri(Templates + "LeftControllerBattery"), 53, 10, 11, 4),
        new(new Uri(Templates + "RightControllerBattery"), 53, 6, 11, 4),
        new(new Uri(Templates + "HeadsetBattery"), 53, 2, 11, 4),
    ];
}
