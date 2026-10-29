using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.UIX;
using Renderite.Shared;
using ResoniteModLoader;

namespace HomeTabOverhaul;

// The mod's own live facets, in place of cloud items: Worlds You're In, Resonite Events, and the controller and
// headset batteries. Each is a plain facet (saved with the dash, so it can be moved and resized in UI Edit mode)
// tagged "HomeTabOverhaul.Live:<kind>"; what's inside is filled in by the mod while the facet is showing, and isn't
// saved. One ticker in userspace (once a second) updates the facets that are visible:
//   - batteries every 2 s, read from the game's input devices;
//   - the worlds list when worlds open, close or change focus, and every 5 s for user counts;
//   - the events list from https://resonite-communities.com/v1/events (the public list the community events facet
//     uses), fetched at most every 15 minutes, and redrawn every minute so "NOW" and "Today" stay right.
internal static class LiveFacets
{
    public const string TagPrefix = "HomeTabOverhaul.Live:";
    private const string ContentName = "Live Content";

    private const string WorldsKind = "worlds";
    private const string EventsKind = "events";
    private const string LeftKind = "battery-left";
    private const string RightKind = "battery-right";
    private const string HeadsetKind = "battery-headset";

    private static readonly colorX PanelColor = RadiantUI_Constants.BUTTON_COLOR.MulRGB(0.8f);
    private static readonly colorX RowColor = new(0.1f, 0.12f, 0.18f, 0.9f);
    private static readonly colorX LiveColor = colorX.FromHexCode("#2b7a3d");
    private static readonly colorX FocusedColor = colorX.FromHexCode("#2b7a3d");
    private static readonly colorX CloseColor = RadiantUI_Constants.MidLight.RED;

    // ---- The facets -----------------------------------------------------------------------------------------------

    public static Facet Worlds(World world) => Shell(world, "Worlds You're In", WorldsKind, new float2(560f, 840f));
    public static Facet Events(World world) => Shell(world, "Resonite Events", EventsKind, new float2(1040f, 840f));
    public static Facet LeftBattery(World world) => Shell(world, "Left Controller Battery", LeftKind, new float2(390f, 120f));
    public static Facet RightBattery(World world) => Shell(world, "Right Controller Battery", RightKind, new float2(390f, 120f));
    public static Facet HeadsetBattery(World world) => Shell(world, "Headset Battery", HeadsetKind, new float2(390f, 120f));

    // The facet background (as the engine's facets have), tagged with its kind; the content goes inside it.
    private static Facet Shell(World world, string name, string kind, float2 size)
    {
        var slot = world.AddSlot(name);
        var facet = slot.AttachComponent<Facet>();
        facet.PreferredSizes.Add(size);
        facet.MinSize = new float2(160f, 64f);
        slot.Tag = TagPrefix + kind;
        var background = facet.Canvas.Target.Slot.AddSlot("Background");
        background.AttachComponent<RectTransform>();
        var image = background.AttachComponent<Image>();
        image.NineSliceSizing.Value = NineSliceSizing.FixedSize;
        image.Sprite.Target = RadiantUI_Constants.GetOutlinedSprite(world);
        image.Tint.Value = PanelColor;
        Rescan();
        return facet;
    }

    // ---- The ticker -----------------------------------------------------------------------------------------------

    private static readonly List<Slot> Found = new();
    private static double _lastScan = double.MinValue;
    private static World? _ticking;
    private static bool _worldsDirty = true;
    private static readonly Dictionary<Slot, double> LastDrawn = new();

    public static void Rescan() => _lastScan = double.MinValue;

    // One line in the log the first time each kind is drawn (to see the facets come alive).
    private static int _lastCount = -1;
    private static readonly HashSet<string> LoggedKinds = new();
    private static void Logged(string kind)
    {
        if (LoggedKinds.Add(kind)) ResoniteMod.Msg($"Live facets: drew {kind}.");
    }

    // Once per session, from the first time the dash shows a screen.
    public static void Start(RadiantDash dash)
    {
        var world = dash.World;
        if (_ticking == world) return;
        _ticking = world;
        var manager = world.Engine.WorldManager;
        manager.WorldsChanged += () => _worldsDirty = true;
        manager.WorldFocused += _ => _worldsDirty = true;
        ResoniteMod.Msg("Live facets: started.");
        Schedule(world);
    }

    private static void Schedule(World world) => world.RunInSeconds(1f, () =>
    {
        if (world.IsDestroyed || Guard.Home.Disabled) return;
        if (HomeTabOverhaul.Active) Guard.Home.Run("updating the live facets", () => Tick(world));
        Schedule(world);
    });

    private static void Tick(World world)
    {
        var now = world.Time.WorldTime;
        if (now - _lastScan > 10)
        {
            _lastScan = now;
            Found.Clear();
            Found.AddRange(world.RootSlot.GetComponentsInChildren<Facet>(f => f.Slot.Tag != null &&
                f.Slot.Tag.StartsWith(TagPrefix, StringComparison.Ordinal)).Select(f => f.Slot));
            foreach (var gone in LastDrawn.Keys.Where(s => s.IsRemoved).ToList()) LastDrawn.Remove(gone);
            if (Found.Count != _lastCount) ResoniteMod.Msg($"Live facets: {Found.Count} found.");
            _lastCount = Found.Count;
        }
        var worldsDirty = _worldsDirty;
        _worldsDirty = false;
        foreach (var slot in Found)
        {
            if (slot.IsRemoved || !slot.IsActive) continue;
            var kind = slot.Tag.Substring(TagPrefix.Length);
            LastDrawn.TryGetValue(slot, out var drawn);
            var content = ContentOf(slot);
            switch (kind)
            {
                case WorldsKind when content == null || worldsDirty || now - drawn > 5:
                    LastDrawn[slot] = now;
                    DrawWorlds(slot);
                    break;
                case EventsKind:
                    if (EventList.NeedsFetch(now)) EventList.Fetch(world, now);
                    if (content == null || EventList.Changed(slot) || now - drawn > 60)
                    {
                        LastDrawn[slot] = now;
                        DrawEvents(slot);
                    }
                    break;
                case LeftKind or RightKind or HeadsetKind when content == null || now - drawn > 2:
                    LastDrawn[slot] = now;
                    DrawBattery(slot, kind);
                    break;
            }
        }
    }

    // The facet already has the engine's own "Background" (made with the Facet); the mod's panel is the last one, drawn
    // over it, so the content goes in that.
    private static Slot? BackgroundOf(Slot facet) =>
        facet.GetComponent<Facet>()?.Canvas.Target?.Slot.Children.LastOrDefault(c => c.Name == "Background");

    private static Slot? ContentOf(Slot facet) => BackgroundOf(facet)?.FindChild(ContentName);

    // A fresh, unsaved content slot inside the facet's background.
    private static UIBuilder NewContent(Slot facet, out Slot content)
    {
        var background = BackgroundOf(facet)!;
        background.FindChild(ContentName)?.Destroy();
        content = background.AddSlot(ContentName);
        content.PersistentSelf = false;
        content.AttachComponent<RectTransform>();
        var ui = new UIBuilder(content);
        RadiantUI_Constants.SetupEditorStyle(ui);
        ui.Style.TextColor = colorX.White;
        return ui;
    }

    private static Text Label(UIBuilder ui, string text, float max, Alignment alignment = Alignment.MiddleLeft, bool richText = false)
    {
        var label = ui.Text((LocaleString)text, alignment: alignment, parseRTF: richText);
        label.AutoSizeMax.Value = max;
        label.Color.Value = colorX.White;
        return label;
    }

    private static Button SmallButton(UIBuilder ui, string text, colorX color, float width, Action action)
    {
        ui.Style.MinWidth = width;
        ui.Style.PreferredWidth = width;
        ui.Style.FlexibleWidth = -1f;
        var button = ui.Button((LocaleString)text, colorX.White);
        HomeBuilder.StyleListButton(button, color, 26f);
        button.LocalPressed += (_, _) => Guard.Home.Run("pressing a live facet button", action);
        return button;
    }

    // ---- Worlds You're In -----------------------------------------------------------------------------------------

    private static readonly Dictionary<World, double> CloseAsked = new();

    private static void DrawWorlds(Slot facet)
    {
        Logged("worlds");
        var manager = facet.Engine.WorldManager;
        var ui = NewContent(facet, out var content);
        ui.VerticalLayout(8f, 14f).ForceExpandHeight.Value = false;
        ui.Style.MinHeight = 48f;
        ui.Style.PreferredHeight = 48f;
        Label(ui, "Worlds You're In", 34f);
        ui.Style.MinHeight = -1f;
        ui.Style.PreferredHeight = -1f;
        ui.Style.FlexibleHeight = 1f;
        ui.ScrollArea();
        ui.VerticalLayout(8f).ForceExpandHeight.Value = false;
        ui.FitContent(SizeFit.Disabled, SizeFit.PreferredSize);
        ui.Style.FlexibleHeight = -1f;

        foreach (var world in manager.Worlds.Where(w => !w.IsUserspace() && !w.IsDestroyed).ToList())
        {
            var focused = manager.FocusedWorld == world;
            ui.Style.MinHeight = 104f;
            ui.Style.PreferredHeight = 104f;
            ui.Panel(focused ? FocusedColor : RowColor);
            ui.HorizontalLayout(8f, 8f).ForceExpandWidth.Value = false;

            // The name and who's hosting; pressing it switches to that world.
            ui.Style.FlexibleWidth = 1f;
            ui.Style.MinWidth = 100f;
            var open = ui.Button((LocaleString)"", RowColor);
            HomeBuilder.StyleListButton(open, focused ? FocusedColor : RowColor);
            var openRoot = open.Slot;
            open.LocalPressed += (_, _) => Guard.Home.Run("switching worlds", () => manager.FocusWorld(world));
            openRoot.GetComponentInChildren<Text>()?.Slot.Destroy();
            var inner = new UIBuilder(openRoot);
            RadiantUI_Constants.SetupEditorStyle(inner);
            inner.VerticalLayout(2f, 6f).ForceExpandHeight.Value = false;
            inner.Style.MinHeight = 46f;
            inner.Style.PreferredHeight = 46f;
            Label(inner, world.Name ?? "World", 30f, richText: true);
            inner.Style.MinHeight = 34f;
            inner.Style.PreferredHeight = 34f;
            var host = world.IsAuthority ? "Hosting" : "Host: " + (world.HostUser?.UserName ?? "?");
            Label(inner, $"{host} · {world.UserCount} {(world.UserCount == 1 ? "user" : "users")}", 22f).Color.Value = colorX.White.SetA(0.75f);

            // Close (press twice).
            SmallButton(ui, "✕", CloseColor, 64f, () =>
            {
                var now = facet.Time.WorldTime;
                if (!CloseAsked.TryGetValue(world, out var asked) || now - asked > 3)
                {
                    foreach (var gone in CloseAsked.Keys.Where(w => w.IsDestroyed).ToList()) CloseAsked.Remove(gone);
                    CloseAsked[world] = now;
                    NotificationMessage.SpawnTextMessage((LocaleString)"Press ✕ again to leave this world", colorX.White);
                    return;
                }
                CloseAsked.Remove(world);
                Userspace.ExitWorld(world);
            });
            ui.NestOut();
            ui.NestOut();
        }
    }

    // ---- Batteries ------------------------------------------------------------------------------------------------

    private static void DrawBattery(Slot facet, string kind)
    {
        Logged(kind);
        var input = facet.World.InputInterface;
        IBatteryLevel? device = kind switch
        {
            LeftKind => input.GetVRControllerNode(Chirality.Left) as IBatteryLevel,
            RightKind => input.GetVRControllerNode(Chirality.Right) as IBatteryLevel,
            _ => input.GetDevice<GeneralHeadset>(),
        };
        var level = input.VR_Active && device != null ? device.BatteryLevel.Value : -1f;
        var charging = input.VR_Active && device != null && device.BatteryCharging.Held;
        var known = level >= 0f;
        level = MathX.Clamp01(level);
        var color = !known ? colorX.White.SetA(0.4f)
            : level > 0.5f ? colorX.FromHexCode("#4cc24f") : level > 0.2f ? colorX.FromHexCode("#e0b030") : colorX.FromHexCode("#e0453c");
        var text = !known ? "N/A" : $"{MathX.RoundToInt(level * 100f)} %{(charging ? " ⚡" : "")}";

        // Built once; after that only the bar, colour and text change.
        var content = ContentOf(facet);
        var fill = content?.FindChild("Bar")?.FindChild("Fill");
        var value = content?.FindChild("Value")?.GetComponent<Text>();
        if (content == null || fill == null || value == null)
        {
            var ui = NewContent(facet, out content);
            ui.Style.FlexibleWidth = -1f;
            var icon = content.AddSlot("Icon");
            var iconRect = icon.AttachComponent<RectTransform>();
            iconRect.AnchorMax.Value = new float2(0f, 1f);
            iconRect.OffsetMin.Value = new float2(14f, 14f);
            iconRect.OffsetMax.Value = new float2(96f, -14f);
            // The hand icon is a left hand; the right controller's is mirrored.
            var image = icon.AttachComponent<RawImage>();
            image.Texture.Target = icon.AttachTexture(kind == HeadsetKind ? OfficialAssets.Graphics.Icons.General.VR_Headset : OfficialAssets.Common.Icons.Hand);
            image.PreserveAspect.Value = true;
            if (kind == RightKind) image.UVRect.Value = new Rect(1f, 0f, -1f, 1f);

            var bar = content.AddSlot("Bar");
            var barRect = bar.AttachComponent<RectTransform>();
            barRect.AnchorMin.Value = new float2(0f, 0.3f);
            barRect.AnchorMax.Value = new float2(1f, 0.7f);
            barRect.OffsetMin.Value = new float2(110f, 0f);
            barRect.OffsetMax.Value = new float2(-130f, 0f);
            bar.AttachComponent<Image>().Tint.Value = new colorX(0f, 0f, 0f, 0.4f);
            fill = bar.AddSlot("Fill");
            fill.AttachComponent<RectTransform>();
            fill.AttachComponent<Image>();

            var valueSlot = content.AddSlot("Value");
            var valueRect = valueSlot.AttachComponent<RectTransform>();
            valueRect.AnchorMin.Value = new float2(1f, 0f);
            valueRect.OffsetMin.Value = new float2(-124f, 0f);
            valueRect.OffsetMax.Value = new float2(-12f, 0f);
            value = valueSlot.AttachComponent<Text>();
            value.Align = Alignment.MiddleRight;
            value.HorizontalAutoSize.Value = true;
            value.VerticalAutoSize.Value = true;
            value.AutoSizeMax.Value = 34f;
            value.AutoSizeMin.Value = 8f;
            value.ParseRichText.Value = false;
        }
        fill.GetComponent<RectTransform>()!.AnchorMax.Value = new float2(known ? level : 0f, 1f);
        fill.GetComponent<Image>()!.Tint.Value = color;
        value.Content.Value = text;
        value.Color.Value = color.SetA(1f);
    }

    // ---- Resonite Events ------------------------------------------------------------------------------------------

    private static readonly HashSet<string> Expanded = new();

    private static void DrawEvents(Slot facet)
    {
        Logged("events");
        EventList.MarkDrawn(facet);
        var ui = NewContent(facet, out _);
        ui.VerticalLayout(8f, 14f).ForceExpandHeight.Value = false;
        ui.Style.MinHeight = 48f;
        ui.Style.PreferredHeight = 48f;
        ui.HorizontalLayout(8f).ForceExpandWidth.Value = false;
        ui.Style.FlexibleWidth = 1f;
        Label(ui, "Upcoming Resonite Events", 34f);
        SmallButton(ui, "↻", HomeBuilder.Navy, 56f, () => EventList.Fetch(facet.World, facet.World.Time.WorldTime, force: true));
        ui.NestOut();

        var list = EventList.Current;
        if (list.Count == 0)
        {
            ui.Style.MinHeight = 40f;
            ui.Style.PreferredHeight = 40f;
            Label(ui, EventList.Status, 26f).Color.Value = colorX.White.SetA(0.7f);
            return;
        }
        ui.Style.MinHeight = -1f;
        ui.Style.PreferredHeight = -1f;
        ui.Style.FlexibleHeight = 1f;
        ui.ScrollArea();
        ui.VerticalLayout(6f).ForceExpandHeight.Value = false;
        ui.FitContent(SizeFit.Disabled, SizeFit.PreferredSize);
        ui.Style.FlexibleHeight = -1f;

        var now = DateTimeOffset.Now;
        foreach (var item in list)
        {
            var live = item.Start <= now && now < item.End;
            var key = item.Name + item.Start.ToUnixTimeSeconds();
            var open = Expanded.Contains(key) && !string.IsNullOrWhiteSpace(item.Description);
            ui.Style.MinHeight = open ? 190f : 84f;
            ui.Style.PreferredHeight = open ? 190f : 84f;
            var row = ui.Button((LocaleString)"", RowColor);
            HomeBuilder.StyleListButton(row, live ? LiveColor : RowColor);
            row.Slot.GetComponentInChildren<Text>()?.Slot.Destroy();
            // Pressing an event shows or hides its description.
            row.LocalPressed += (_, _) => Guard.Home.Run("opening an event", () =>
            {
                if (!Expanded.Remove(key)) Expanded.Add(key);
                facet.RunInUpdates(1, () => Guard.Home.Run("showing the events", () => DrawEvents(facet)));
            });
            var inner = new UIBuilder(row.Slot);
            RadiantUI_Constants.SetupEditorStyle(inner);
            inner.HorizontalLayout(12f, 8f).ForceExpandWidth.Value = false;

            inner.Style.MinWidth = 230f;
            inner.Style.PreferredWidth = 230f;
            inner.Style.FlexibleWidth = -1f;
            inner.VerticalLayout(0f).ForceExpandHeight.Value = false;
            inner.Style.MinHeight = 30f;
            inner.Style.PreferredHeight = 30f;
            Label(inner, live ? "NOW" : DayOf(item.Start), 24f, Alignment.MiddleCenter);
            Label(inner, $"{item.Start.ToLocalTime():t} – {item.End.ToLocalTime():t}", 22f, Alignment.MiddleCenter);
            inner.NestOut();

            inner.Style.MinWidth = 100f;
            inner.Style.PreferredWidth = -1f;
            inner.Style.FlexibleWidth = 1f;
            inner.VerticalLayout(0f).ForceExpandHeight.Value = false;
            inner.Style.MinHeight = 34f;
            inner.Style.PreferredHeight = 34f;
            Label(inner, item.Name, 28f);
            inner.Style.MinHeight = 26f;
            inner.Style.PreferredHeight = 26f;
            var where = string.Join(" · ", new[] { item.Location, item.Community }.Where(s => !string.IsNullOrWhiteSpace(s)));
            Label(inner, where, 20f).Color.Value = colorX.White.SetA(0.7f);
            if (open)
            {
                inner.Style.MinHeight = 104f;
                inner.Style.PreferredHeight = 104f;
                var description = Label(inner, item.Description, 18f, Alignment.TopLeft);
                description.Color.Value = colorX.White.SetA(0.85f);
            }
            inner.NestOut();
        }
    }

    private static string DayOf(DateTimeOffset start)
    {
        var day = start.ToLocalTime().Date;
        var today = DateTime.Today;
        if (day == today) return "Today";
        if (day == today.AddDays(1)) return "Tomorrow";
        return day.ToString("ddd d MMM", CultureInfo.CurrentCulture);
    }

    // The list itself: fetched off the main thread, swapped in whole.
    private static class EventList
    {
        public sealed record Item(string Name, string Description, string Location, DateTimeOffset Start, DateTimeOffset End, string Community);

        private const string Url = "https://resonite-communities.com/v1/events";
        private const double RefreshSeconds = 15 * 60;
        private static readonly HttpClient Http = MakeClient();

        private static HttpClient MakeClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.UserAgent.TryParseAdd("HomeTabOverhaul");
            return client;
        }

        public static List<Item> Current { get; private set; } = new();
        public static string Status { get; private set; } = "Loading events…";
        private static double _fetchedAt = double.MinValue;
        private static bool _fetching;
        private static int _version;
        private static readonly Dictionary<Slot, int> DrawnVersion = new();

        public static bool NeedsFetch(double now) => !_fetching && now - _fetchedAt > RefreshSeconds;
        public static bool Changed(Slot facet) => !DrawnVersion.TryGetValue(facet, out var drawn) || drawn != _version;
        public static void MarkDrawn(Slot facet) => DrawnVersion[facet] = _version;

        public static void Fetch(World world, double now, bool force = false)
        {
            if (_fetching || (!force && now - _fetchedAt < 30)) return;
            _fetching = true;
            _fetchedAt = now;
            Task.Run(async () =>
            {
                List<Item>? items = null;
                string? error = null;
                try
                {
                    items = Parse(await Http.GetStringAsync(Url).ConfigureAwait(false));
                }
                catch (Exception ex)
                {
                    error = "Couldn't load the events (" + ex.GetType().Name + ")";
                    ResoniteMod.Warn("Couldn't load the Resonite events: " + ex.Message);
                }
                world.RunSynchronously(() =>
                {
                    _fetching = false;
                    if (items != null)
                    {
                        Current = items;
                        Status = items.Count == 0 ? "No upcoming events" : "";
                    }
                    else if (Current.Count == 0) Status = error ?? "Couldn't load the events";
                    _version++;
                });
            });
        }

        // One event per line, fields separated by backticks: name, description, location, start, end, community.
        private static List<Item> Parse(string text)
        {
            var items = new List<Item>();
            var now = DateTimeOffset.Now;
            foreach (var line in text.Split('\n'))
            {
                var fields = line.TrimEnd('\r').Split('`');
                if (fields.Length < 6) continue;
                if (!TryTime(fields[3], out var start) || !TryTime(fields[4], out var end) || end <= now) continue;
                items.Add(new Item(Clip(fields[0], 120), Clip(fields[1], 600), Clip(fields[2], 120), start, end, Clip(fields[5], 120)));
            }
            return items.OrderBy(i => i.Start).Take(40).ToList();
        }

        private static bool TryTime(string text, out DateTimeOffset time) =>
            DateTimeOffset.TryParseExact(text.Trim(), "yyyy/MM/dd HH:mm:sszzz", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out time);

        private static string Clip(string text, int max) => text.Length <= max ? text.Trim() : text.Substring(0, max).Trim() + "…";
    }
}
