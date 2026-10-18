namespace DisplayBrightness.Utilities;

/// <summary>
/// Shared translation between raw wheel units and brightness percent. One
/// standard wheel notch is 120 units and moves brightness by three percent.
/// Precision touchpads report fractions of a notch, so callers accumulate the
/// remainder before converting.
/// </summary>
internal static class WheelNotch
{
    public const int DetentUnits = 120;
    public const int BrightnessStep = 3;
}
