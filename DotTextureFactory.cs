using VRRealityWindow.Core;

namespace BatteryDotOverlay;

internal static class DotTextureFactory
{
    public static CameraFrame CreateFrame(VisualConfig visual)
    {
        var size = Math.Clamp(visual.TextureSizePixels, 32, 1024);
        var fillRatio = Math.Clamp(visual.CircleFillRatio, 0.10f, 0.95f);
        var opacity = Math.Clamp(visual.Opacity, 0.0f, 1.0f);
        var feather = Math.Clamp(visual.EdgeFeatherPixels, 0.5f, size / 6f);
        var color = ParseColor(visual.ColorHex);
        var buffer = new byte[size * size * 4];

        var center = (size - 1) / 2f;
        var radius = (size * fillRatio) / 2f;
        var edgeStart = Math.Max(0f, radius - feather);
        var edgeEnd = radius + feather;

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var dx = x - center;
                var dy = y - center;
                var distance = MathF.Sqrt((dx * dx) + (dy * dy));
                var coverage = distance <= edgeStart
                    ? 1f
                    : distance >= edgeEnd
                        ? 0f
                        : 1f - ((distance - edgeStart) / (edgeEnd - edgeStart));

                var alpha = coverage * opacity;
                if (alpha <= 0f)
                {
                    continue;
                }

                var offset = ((y * size) + x) * 4;
                buffer[offset + 0] = color.B;
                buffer[offset + 1] = color.G;
                buffer[offset + 2] = color.R;
                buffer[offset + 3] = (byte)Math.Clamp((int)MathF.Round(alpha * 255f), 0, 255);
            }
        }

        return new CameraFrame(buffer, size, size, 4, 0, 0, "battery-dot");
    }

    private static ColorRgb ParseColor(string value)
    {
        var normalized = value.Trim();
        if (normalized.StartsWith('#'))
        {
            normalized = normalized[1..];
        }

        if (normalized.Length != 6)
        {
            throw new InvalidOperationException("color_hex must be in #RRGGBB format.");
        }

        return new ColorRgb(
            Convert.ToByte(normalized[..2], 16),
            Convert.ToByte(normalized[2..4], 16),
            Convert.ToByte(normalized[4..6], 16));
    }

    private readonly record struct ColorRgb(byte R, byte G, byte B);
}
