namespace DisplayBrightness.Utilities;

/// <summary>
/// Shared translation between raw wheel units and brightness percent. Precision touchpads
/// report fractions of a notch, so callers accumulate the remainder before converting.
/// </summary>
internal static class WheelNotch
{
    public const int DetentUnits = 120;
    public const int BrightnessStep = 3;
}
