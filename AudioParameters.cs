using System;
using System.Globalization;
using System.IO;

namespace MuhabbetKusu;

public static class AudioParameters
{
    public static decimal NormalizeSpeed(double speedValue, bool isAntalia)
    {
        if (double.IsNaN(speedValue) || double.IsInfinity(speedValue))
            return isAntalia ? 0.95m : 1.00m;
        return (decimal)Math.Clamp(speedValue, 0.25, 4.0);
    }

    public static double NormalizeCfg(double cfgValue)
    {
        if (double.IsNaN(cfgValue) || double.IsInfinity(cfgValue))
            return 2.0;
        return Math.Clamp(cfgValue, 0.5, 10.0);
    }

    public static long? NormalizeSeed(bool isFixedSeed, double seedValue)
    {
        if (!isFixedSeed) return null;
        if (double.IsNaN(seedValue) || double.IsInfinity(seedValue) || seedValue <= 0)
            return 0;
        if (seedValue >= long.MaxValue)
            return long.MaxValue;
        return (long)seedValue;
    }

    public static int ParseSteps(object? item)
    {
        if (item is null) return 8;
        string s = item.ToString()?.Trim() ?? string.Empty;
        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var val) && val > 0)
            return val;
        return 8;
    }

    public static int ParseSampleRate(object? item)
    {
        if (item is null) return 48000;
        string raw = item.ToString()?.Trim().ToLowerInvariant() ?? string.Empty;

        if (raw.EndsWith("hz"))
        {
            raw = raw[..^2].Trim();
        }
        if (raw.EndsWith("k"))
        {
            string numStr = raw[..^1].Trim();
            if (double.TryParse(numStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var kVal) && kVal > 0)
                return (int)(kVal * 1000);
        }
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var val) && val > 0)
            return val;

        return 48000;
    }

    public static string GetSuggestedFileName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "muhabbet_ses";
        var name = Path.GetFileNameWithoutExtension(path);
        return string.IsNullOrWhiteSpace(name) ? "muhabbet_ses" : name;
    }
}
