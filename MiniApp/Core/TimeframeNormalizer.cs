namespace ValutaBot.MiniApp;

/// <summary>
/// Normalizes timeframe representations (e.g. "5s", "s5", "5с", "1m", "m1", "1min") into canonical internal formats ("s5", "m1").
/// Guarantees that user requests across all supported timeframes (5s, 10s, 15s, 30s, 1m) resolve identically.
/// </summary>
public static class TimeframeNormalizer
{
    public static string Normalize(string? timeframe)
    {
        if (string.IsNullOrWhiteSpace(timeframe)) return "m1";

        string t = timeframe.Trim().ToLowerInvariant()
            .Replace(" ", "")
            .Replace("мин", "m")
            .Replace("sec", "s")
            .Replace("сек", "s")
            .Replace("\u0441", "s") // Russian 'с'
            .Replace("\u043c", "m") // Russian 'м'
            .Replace("\u0447", "h") // Russian 'ч'
            .Replace("\u0434", "d"); // Russian 'д'

        // Suffix notation: "5s", "10s", "15s", "30s" -> "s5", "s10", "s15", "s30"
        if (t.EndsWith("s") && int.TryParse(t[..^1], out int secVal))
        {
            return $"s{secVal}";
        }

        // Suffix notation: "1m", "3m", "5m", "15m", "30m" -> "m1", "m3", "m5", "m15", "m30"
        if (t.EndsWith("m") && int.TryParse(t[..^1], out int minVal))
        {
            return $"m{minVal}";
        }

        // Notation: "1min", "3min", "5min" -> "m1", "m3", "m5"
        if (t.EndsWith("min") && int.TryParse(t[..^3], out int minVal2))
        {
            return $"m{minVal2}";
        }

        // Suffix notation: "1h", "4h" -> "h1", "h4"
        if (t.EndsWith("h") && int.TryParse(t[..^1], out int hourVal))
        {
            return $"h{hourVal}";
        }

        // Suffix notation: "1d" -> "d1"
        if (t.EndsWith("d") && int.TryParse(t[..^1], out int dayVal))
        {
            return $"d{dayVal}";
        }

        if (t == "1min") return "m1";
        if (t == "1day") return "d1";

        return t;
    }

    public static int ToSeconds(string? timeframe)
    {
        string norm = Normalize(timeframe);
        if (norm.StartsWith("s") && int.TryParse(norm[1..], out int s)) return s;
        if (norm.StartsWith("m") && int.TryParse(norm[1..], out int m)) return m * 60;
        if (norm.StartsWith("h") && int.TryParse(norm[1..], out int h)) return h * 3600;
        if (norm == "d1") return 86400;
        return 60;
    }

    public static bool IsSubminute(string? timeframe)
    {
        string norm = Normalize(timeframe);
        return norm.StartsWith("s");
    }
}
