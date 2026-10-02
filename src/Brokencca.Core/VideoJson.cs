using System.Text;
using System.Text.Json;

namespace Brokencca.Core;

/// <summary>Strict video-control JSON schema; caps that depend on the peer are checked by the session owner.</summary>
public static class VideoJson
{
    public static void Validate(VideoMessageType type, JsonElement root)
    {
        switch (type)
        {
            case VideoMessageType.Hello:
                bool timing = root.TryGetProperty("frameTiming", out JsonElement timingValue);
                if (timing) {
                    Fields(root, "sessionToken", "videoVersion", "frameTiming");
                    if (timingValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) Bad("Invalid frame timing option.");
                }
                else Fields(root, "sessionToken", "videoVersion");
                string token = String(root, "sessionToken", 32);
                if (token.Length != 32 || !token.All(Uri.IsHexDigit)) Bad("Invalid session token.");
                Equal(root, "videoVersion", 1);
                break;
            case VideoMessageType.HelloAck:
                Fields(root, "maxWidth", "maxHeight", "maxPixels", "maxFps", "maxAuBytes", "profiles", "maxLevel");
                Int(root, "maxWidth", 1, 1920); Int(root, "maxHeight", 1, 1920);
                Int(root, "maxPixels", 1, 2_073_600); Int(root, "maxFps", 1, 60);
                Int(root, "maxAuBytes", 65_536, VideoProtocol.MaxAccessUnit);
                Int(root, "maxLevel", 1, 42);
                JsonElement profiles = root.GetProperty("profiles");
                if (profiles.ValueKind != JsonValueKind.Array || profiles.GetArrayLength() is < 1 or > 2) Bad("Invalid profile list.");
                HashSet<string> seen = new(StringComparer.Ordinal);
                foreach (JsonElement profile in profiles.EnumerateArray())
                {
                    if (profile.ValueKind != JsonValueKind.String) Bad("Invalid profile.");
                    string name = profile.GetString()!;
                    if (name is not ("main" or "baseline") || !seen.Add(name)) Bad("Invalid profile.");
                }
                break;
            case VideoMessageType.Config: ValidateConfig(root); break;
            case VideoMessageType.Ready:
                Fields(root, "hardwareVerified");
                if (root.GetProperty("hardwareVerified").ValueKind is not (JsonValueKind.True or JsonValueKind.False)) Bad("Invalid decoder hardware verification.");
                break;
            case VideoMessageType.Feedback:
                Fields(root, "receivedId", "decodedId", "presentedId", "presentedAtUs", "pendingDecode", "replacedDecoded", "thermal", "displayMilliHz");
                ulong received = Decimal(root, "receivedId"), decoded = Decimal(root, "decodedId"), presented = Decimal(root, "presentedId");
                Decimal(root, "presentedAtUs");
                if (presented > decoded || decoded > received) Bad("Invalid feedback order.");
                Int(root, "pendingDecode", 0, 3); Int(root, "replacedDecoded", 0, uint.MaxValue);
                OneOf(root, "thermal", "nominal", "fair", "serious", "critical");
                Int(root, "displayMilliHz", 0, 240_000);
                break;
            case VideoMessageType.RequestIdr:
                Fields(root, "reason"); OneOf(root, "reason", "decode-error", "missing-reference", "stale"); break;
            case VideoMessageType.ClockPing:
                Fields(root, "t1Us"); Decimal(root, "t1Us"); break;
            case VideoMessageType.ClockPong:
                Fields(root, "t1Us", "t2Us", "t3Us"); Decimal(root, "t1Us");
                if (Decimal(root, "t3Us") < Decimal(root, "t2Us")) Bad("Clock reply moved backward.");
                break;
            case VideoMessageType.Status:
                Fields(root, "state", "reason");
                OneOf(root, "state", "running", "source-idle", "paused", "stopped");
                String(root, "reason", 256); break;
            case VideoMessageType.Error:
                Fields(root, "code", "detail");
                OneOf(root, "code", "unsupported-config", "hardware-unavailable", "invalid-stream", "internal");
                String(root, "detail", 256); break;
            default: Bad("Unknown video JSON message."); break;
        }
    }

    private static void ValidateConfig(JsonElement root)
    {
        Fields(root, "codec", "profile", "level", "codedWidth", "codedHeight", "fpsNum", "fpsDen",
            "bitrateBps", "nalLengthBytes", "color", "rotation", "sourceWidth", "sourceHeight",
            "crop", "contentRect", "circle", "sps", "pps");
        OneOf(root, "codec", "h264"); OneOf(root, "profile", "main", "baseline");
        Int(root, "level", 1, 42);
        long width = Int(root, "codedWidth", 2, 1920), height = Int(root, "codedHeight", 2, 1920);
        if ((width & 1) != 0 || (height & 1) != 0 || width * height > 2_073_600) Bad("Invalid coded dimensions.");
        Equal(root, "fpsNum", 60); Equal(root, "fpsDen", 1);
        Int(root, "bitrateBps", 4_000_000, 20_000_000); Equal(root, "nalLengthBytes", 4);
        OneOf(root, "color", "bt709-limited"); Equal(root, "rotation", 0);
        long sourceWidth = Int(root, "sourceWidth", 1, 16_384), sourceHeight = Int(root, "sourceHeight", 1, 16_384);
        double[] crop = Doubles(root, "crop", 4), content = Doubles(root, "contentRect", 4), circle = Doubles(root, "circle", 3);
        try
        {
            new NormalizedCrop(crop[0], crop[1], crop[2], crop[3]).Validate();
            new PlayfieldCircle(circle[0], circle[1], circle[2], "stream").Validate();
        }
        catch (ArgumentException e) { throw new InvalidDataException("Invalid stream crop or playfield.", e); }
        if (content[0] < 0 || content[1] < 0 || content[2] <= 0 || content[3] <= 0 ||
            content[0] + content[2] > width || content[1] + content[3] > height) Bad("Invalid content rectangle.");
        double rx = circle[2] * Math.Min(sourceWidth, sourceHeight) / sourceWidth;
        double ry = circle[2] * Math.Min(sourceWidth, sourceHeight) / sourceHeight;
        if (circle[0] - rx < crop[0] || circle[0] + rx > crop[0] + crop[2] ||
            circle[1] - ry < crop[1] || circle[1] + ry > crop[1] + crop[3]) Bad("Crop clips calibrated circle.");
        Nal(root, "sps", 7); Nal(root, "pps", 8);
    }

    private static void Fields(JsonElement root, params string[] expected)
    {
        if (root.EnumerateObject().Count() != expected.Length ||
            expected.Any(name => !root.TryGetProperty(name, out _))) Bad("Unexpected or missing video JSON field.");
    }
    private static long Int(JsonElement root, string name, long min, long max)
    {
        JsonElement value = root.GetProperty(name);
        long number = 0;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out number) || number < min || number > max)
            Bad($"Invalid {name}.");
        return number;
    }
    private static void Equal(JsonElement root, string name, long expected)
    {
        if (Int(root, name, expected, expected) != expected) Bad($"Invalid {name}.");
    }
    private static string String(JsonElement root, string name, int maxUtf8)
    {
        JsonElement value = root.GetProperty(name);
        if (value.ValueKind != JsonValueKind.String) Bad($"Invalid {name}.");
        string result = value.GetString()!;
        if (Encoding.UTF8.GetByteCount(result) > maxUtf8) Bad($"Invalid {name}.");
        return result;
    }
    private static ulong Decimal(JsonElement root, string name)
    {
        string text = String(root, name, 20);
        ulong value = 0;
        if (text.Length == 0 || text.Length > 20 || text.Any(c => c is < '0' or > '9') ||
            (text.Length > 1 && text[0] == '0') || !ulong.TryParse(text, out value)) Bad($"Invalid {name}.");
        return value;
    }
    private static void OneOf(JsonElement root, string name, params string[] options)
    {
        if (!options.Contains(String(root, name, 256), StringComparer.Ordinal)) Bad($"Invalid {name}.");
    }
    private static double[] Doubles(JsonElement root, string name, int count)
    {
        JsonElement values = root.GetProperty(name);
        if (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() != count) Bad($"Invalid {name}.");
        double[] result = new double[count];
        int i = 0;
        foreach (JsonElement value in values.EnumerateArray())
        {
            double number = 0;
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out number) || !double.IsFinite(number))
                Bad($"Invalid {name}.");
            result[i++] = number;
        }
        return result;
    }
    private static void Nal(JsonElement root, string name, int expectedType)
    {
        string text = String(root, name, 1500);
        byte[] nal;
        try { nal = Convert.FromBase64String(text); }
        catch (FormatException e) { throw new InvalidDataException($"Invalid {name} base64.", e); }
        if (nal.Length is < 1 or > 1024 || (nal[0] & 31) != expectedType) Bad($"Invalid {name} NAL.");
    }
    private static void Bad(string reason) => throw new InvalidDataException(reason);
}
