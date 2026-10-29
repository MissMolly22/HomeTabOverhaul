using System.Runtime.CompilerServices;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.UIX;
using ResoniteModLoader;

namespace HomeTabOverhaul;

// Moving and resizing facets in UI Edit mode on desktop. In VR a facet is picked up with the controller's grab (and
// resized by dragging while held), but on desktop the dash is driven by the mouse pointer
// (PointerInteractionController → RelayTouchSource), which can only touch, so the engine offers neither. Here, on
// every dash grid:
//   - Move: press in the middle of a facet → (moved more than ~a cell) it's lifted out of the grid → the usual
//     green/red placement preview follows the mouse → release drops it there, or puts it back if it doesn't fit.
//   - Resize (config desktop_resize): hovering near an edge or corner shows an orange handle on it; press and drag
//     that edge/corner, the new size is previewed green (fits) or red (overlaps, too small, off the grid), and
//     release applies it.
// A press that doesn't move is left to the engine, so clicking still works as before.
internal static class DesktopDrag
{
    private sealed class Drag(TouchSource source, PlacedFacet target, float2 start, Edges edges)
    {
        public readonly TouchSource Source = source;
        public readonly PlacedFacet Target = target;
        public readonly float2 Start = start;
        public readonly Edges Edges = edges;
        public bool Started;
        public IntRect Original;
        public IntRect Resized;
        public bool ResizeFits;
    }

    private readonly record struct Edges(bool Left, bool Right, bool Bottom, bool Top)
    {
        public bool Any => Left || Right || Bottom || Top;
    }

    private static readonly ConditionalWeakTable<GridContainer, Drag> Drags = new();
    private static bool _loggedEvents, _loggedLift, _loggedResize;

    private static readonly colorX HandleColor = new(1f, 0.6f, 0.15f);

    // Returns true when the event was ours (the engine's handling is skipped and `result` is what it returns).
    public static bool Handle(GridContainer grid, Canvas.InteractionData data, ref bool result)
    {
        if (data.source is not RelayTouchSource || !grid.World.IsUserspace()) return false;
        Drags.TryGetValue(grid, out var drag);
        if (drag == null && (!grid.EditMode.Value || grid.FacetsRoot.Target == null)) return false;
        if (drag != null && drag.Source != data.source) return false;

        var point = grid.GetLocalPoint(data);
        if (!_loggedEvents)
        {
            _loggedEvents = true;
            ResoniteMod.Msg("Desktop drag: mouse events are reaching a facet grid in UI Edit mode.");
        }
        var touching = data.touch is EventState.Begin or EventState.Stay;

        if (drag == null)
        {
            var placed = grid.GetFacetAtPoint(point);
            var edges = placed.facet != null && placed.nestedCanvas != null
                ? EdgesAt(grid, placed.nestedCanvas.RectTransform, point)
                : default;
            if (data.touch == EventState.Begin && placed.facet != null && placed.nestedCanvas != null)
                Drags.Add(grid, new Drag(data.source, placed, point, edges));
            if (!edges.Any || data.hover == EventState.End) return false; // the engine draws its hover highlight
            ShowHandle(grid, placed.nestedCanvas!.RectTransform, edges);
            result = true;
            return true;
        }

        if (drag.Edges.Any) return HandleResize(grid, drag, point, touching, ref result);

        if (!drag.Started)
        {
            if (!touching)
            {
                Drags.Remove(grid); // a plain click
                return false;
            }
            if ((point - drag.Start).Magnitude < grid.ActualCellSize.x * 0.75f) return false;
            Lift(grid, drag);
        }

        var facet = drag.Target.facet;
        result = true;
        if (facet.IsRemoved)
        {
            Drags.Remove(grid);
            grid.ClearVisualization();
            return true;
        }
        if (touching)
        {
            grid.UpdateVisualization(grid.GetCursorRect(point), colorX.Yellow, isCursor: true);
            grid.PreviewFacetPlacement(facet, point, data);
            return true;
        }

        Drags.Remove(grid);
        grid.EndFacetPlacementPreview(facet);
        if (data.touch != EventState.End || !grid.PlaceFacet(facet, point, data)) grid.PlaceFacet(facet, drag.Original);
        grid.NotifyModified();
        return true;
    }

    // ---- Resize -------------------------------------------------------------------------------------------------

    private static bool HandleResize(GridContainer grid, Drag drag, float2 point, bool touching, ref bool result)
    {
        result = true;
        var nested = drag.Target.nestedCanvas;
        if (nested.IsRemoved || drag.Target.facet.IsRemoved)
        {
            Drags.Remove(grid);
            grid.ClearVisualization();
            return true;
        }
        if (!drag.Started)
        {
            if (!touching)
            {
                Drags.Remove(grid); // a click on the edge
                return true;
            }
            if ((point - drag.Start).Magnitude < grid.ActualCellSize.x * 0.25f)
            {
                ShowHandle(grid, nested.RectTransform, drag.Edges);
                return true;
            }
            drag.Started = true;
            drag.Original = CellRect(grid, nested.RectTransform);
            drag.Resized = drag.Original;
            if (!_loggedResize)
            {
                _loggedResize = true;
                ResoniteMod.Msg($"Desktop drag: resizing {drag.Target.facet.Slot.Name} from {drag.Original}.");
            }
        }

        if (touching)
        {
            drag.Resized = ResizedRect(grid, drag, point);
            drag.ResizeFits = Fits(grid, drag, drag.Resized);
            grid.UpdateVisualization(grid.ToVisualRect(drag.Resized), drag.ResizeFits ? colorX.Green : colorX.Red, isCursor: false);
            grid.UpdateVisualization(grid.GetCursorRect(point), HandleColor, isCursor: true);
            return true;
        }

        Drags.Remove(grid);
        grid.ClearVisualization();
        if (drag.ResizeFits && !drag.Resized.Equals(drag.Original))
        {
            // Re-placing is how the engine resizes too: a new nested canvas at the new rect, and the facet's canvas
            // takes that size.
            nested.Slot.Destroy();
            grid.PlaceFacet(drag.Target.facet, drag.Resized);
            grid.NotifyModified();
        }
        return true;
    }

    // Moves only the dragged edges to the cell under the mouse, keeping at least one cell.
    private static IntRect ResizedRect(GridContainer grid, Drag drag, float2 point)
    {
        var cell = MathX.FloorToInt((point - grid.CenteringOffset) / grid.ActualCellSize);
        int minX = drag.Original.Position.x, minY = drag.Original.Position.y;
        int maxX = minX + drag.Original.Size.x, maxY = minY + drag.Original.Size.y;
        if (drag.Edges.Left) minX = MathX.Min(cell.x, maxX - 1);
        if (drag.Edges.Right) maxX = MathX.Max(cell.x + 1, minX + 1);
        if (drag.Edges.Bottom) minY = MathX.Min(cell.y, maxY - 1);
        if (drag.Edges.Top) maxY = MathX.Max(cell.y + 1, minY + 1);
        return IntRect.FromMinMax(new int2(minX, minY), new int2(maxX, maxY));
    }

    private static bool Fits(GridContainer grid, Drag drag, IntRect rect)
    {
        var size = grid.GridSize;
        if (rect.Position.x < 0 || rect.Position.y < 0 || rect.Position.x + rect.Size.x > size.x || rect.Position.y + rect.Size.y > size.y)
            return false;
        var visual = grid.ToVisualRect(rect).size;
        var facet = drag.Target.facet;
        if (visual.x < facet.MinWidth.Value || visual.y < facet.MinHeight.Value) return false;
        foreach (var child in grid.ContentRoot.Slot.Children)
        {
            if (child == drag.Target.nestedCanvas.Slot) continue;
            var other = child.GetComponent<RectTransform>();
            if (other != null && Overlaps(rect, CellRect(grid, other))) return false;
        }
        return true;
    }

    private static bool Overlaps(IntRect a, IntRect b) =>
        a.Position.x < b.Position.x + b.Size.x && b.Position.x < a.Position.x + a.Size.x &&
        a.Position.y < b.Position.y + b.Size.y && b.Position.y < a.Position.y + a.Size.y;

    // Which edges of a placed facet the point is close to. The margin is small enough to leave the middle for moving.
    private static Edges EdgesAt(GridContainer grid, RectTransform nested, float2 point)
    {
        var rect = Rect.FromMinMax((float2)nested.OffsetMin, (float2)nested.OffsetMax);
        var cell = grid.ActualCellSize;
        var margin = MathX.Min(18f, MathX.Min(rect.size.x, rect.size.y) * 0.2f, MathX.Min(cell.x, cell.y) * 0.6f);
        return new Edges(
            point.x - rect.ExtentMin.x < margin,
            rect.ExtentMax.x - point.x < margin,
            point.y - rect.ExtentMin.y < margin,
            rect.ExtentMax.y - point.y < margin);
    }

    // The "adjust" handle: the facet outlined, and an orange bar on the edge (or a square on the corner) under the mouse.
    private static void ShowHandle(GridContainer grid, RectTransform nested, Edges edges)
    {
        var rect = Rect.FromMinMax((float2)nested.OffsetMin, (float2)nested.OffsetMax);
        const float thickness = 10f;
        var min = rect.ExtentMin;
        var max = rect.ExtentMax;
        float x0 = min.x, y0 = min.y, x1 = max.x, y1 = max.y;
        if (edges.Left) x1 = min.x + thickness;
        if (edges.Right) x0 = max.x - thickness;
        if (edges.Bottom) y1 = min.y + thickness;
        if (edges.Top) y0 = max.y - thickness;
        var handleMin = new float2(x0, y0);
        var handleMax = new float2(x1, y1);
        // A corner gets a square a bit bigger than the bar is thick.
        if ((edges.Left || edges.Right) && (edges.Bottom || edges.Top))
        {
            var corner = new float2(edges.Left ? min.x : max.x, edges.Bottom ? min.y : max.y);
            handleMin = corner - thickness * 1.5f;
            handleMax = corner + thickness * 1.5f;
        }
        grid.UpdateVisualization(rect, colorX.Cyan, isCursor: false);
        grid.UpdateVisualization(Rect.FromMinMax(handleMin, handleMax), HandleColor, isCursor: true);
    }

    // ---- Move ---------------------------------------------------------------------------------------------------

    // Pointer events try the deepest element first: over a facet that is the grid's NestedCanvas (the facet's own
    // buttons), and only when that has nothing under the pointer does the grid itself get the event. In VR that
    // doesn't matter (the controller's grab goes to the grid either way), but a desktop press over a facet's button
    // never reached the grid. So in UI Edit mode, desktop pointer events skip the facets and go to the grid.
    public static bool ShouldSkipFacet(NestedCanvas nested, Canvas.InteractionData data)
    {
        if (data.source is not RelayTouchSource) return false;
        var parent = nested.Slot.Parent;
        var grid = parent?.Parent?.GetComponent<GridContainer>();
        return grid != null && grid.ContentRoot?.Slot == parent && grid.EditMode.Value && grid.FacetsRoot.Target != null &&
               grid.World.IsUserspace();
    }

    // Takes the facet out of the grid, like the engine's PickFacetAtPoint, so the preview no longer collides with its
    // own old spot. The old spot is kept to fall back on.
    private static void Lift(GridContainer grid, Drag drag)
    {
        drag.Original = CellRect(grid, drag.Target.nestedCanvas.RectTransform);
        // Like the engine, the facet itself stays active: it shows faintly at its old spot until it's dropped. (Hiding
        // it here broke its draw order, so after the drop it was drawn behind the dash background.)
        drag.Target.nestedCanvas.Slot.Destroy();
        drag.Started = true;
        if (!_loggedLift)
        {
            _loggedLift = true;
            ResoniteMod.Msg($"Desktop drag: lifted {drag.Target.facet.Slot.Name} from {drag.Original}.");
        }
    }

    // A placed facet's spot in grid cells (the same maths as GridContainer.GetCurrentRects).
    private static IntRect CellRect(GridContainer grid, RectTransform rectTransform)
    {
        var rect = Rect.FromMinMax((float2)rectTransform.OffsetMin, (float2)rectTransform.OffsetMax);
        var padding = grid.Padding.Value;
        rect.position -= grid.CenteringOffset + padding * 0.5f;
        rect.size += padding;
        var cell = grid.ActualCellSize;
        return IntRect.FromMinMax(MathX.FloorToInt((rect.ExtentMin + 0.01f) / cell), MathX.CeilToInt((rect.ExtentMax - 0.01f) / cell));
    }
}
