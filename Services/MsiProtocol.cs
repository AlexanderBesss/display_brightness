namespace DisplayBrightness.Services;

/// <summary>MSI USB (HID) text protocol: a one-byte verb, a five-character feature code, an optional value, and a CR.</summary>
internal static class MsiProtocol
{
    public const string GetVerb = "58";
    public const string ReplyVerb = "5b";
    public const string ScalerEventGetVerb = "68";
    public const string ScalerEventReplyVerb = "6b";
    public const string AckSuccess = "5600+";
    public const string AckFailure = "5600-";
    public const int FeatureCodeLength = 5;

    /// <summary>Register that reports the display's chassis serial number.</summary>
    public const string SerialNumberFeatureCode = "00130";

    public static string GetCommand(string featureCode) =>
        GetVerb + featureCode + "\r";

    public static string GetScalerEventCommand(string featureCode) =>
        ScalerEventGetVerb + featureCode + "\r";

    public static string SetCommand(string featureCode, string value) =>
        ReplyVerb + featureCode + value + "\r";

    public static bool TryParseReply(
        string payload,
        string featureCode,
        out string value) =>
        TryParseReply(payload, ReplyVerb, featureCode, out value);

    public static bool TryParseScalerEventReply(
        string payload,
        string featureCode,
        out string value) =>
        TryParseReply(payload, ScalerEventReplyVerb, featureCode, out value);

    public static bool TryParseReply(
        string payload,
        string replyVerb,
        string featureCode,
        out string value)
    {
        value = string.Empty;
        string expected = replyVerb + featureCode;
        if (!payload.StartsWith(expected, StringComparison.Ordinal))
            return false;

        value = payload[expected.Length..];
        return true;
    }
}
