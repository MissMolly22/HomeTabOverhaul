using System;
using ResoniteModLoader;

namespace HomeTabOverhaul;

// The runtime safety net, one per feature. Every patch body runs through its feature's Guard.Run; the first exception
// switches that feature off for the rest of the session (the others keep working). What the mod has already built is
// plain engine UI, so there is nothing to undo.
internal sealed class Guard(string feature)
{
    public static readonly Guard Home = new("the Home screen");
    public static readonly Guard Drag = new("desktop facet dragging");
    public static readonly Guard Actions = new("the install and spawn buttons");

    public bool Disabled { get; private set; }

    public bool Run(string what, Action action)
    {
        if (Disabled) return false;
        try
        {
            action();
            return true;
        }
        catch (Exception ex)
        {
            Disable(what, ex);
            return false;
        }
    }

    public void Disable(string what, Exception ex)
    {
        if (Disabled) return;
        Disabled = true;
        ResoniteMod.Error($"HomeTabOverhaul hit an error in {what}; {feature} is switched off until restart. Details: " + ex);
    }
}
