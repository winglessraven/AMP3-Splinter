using System.Globalization;
using System.Security.Cryptography;
using SkiaSharp;

namespace Splinter.Host;

// A single Discord attachment. Its only message components are native Discord
// buttons; no embed fields are needed. Classic remains the unchanged fallback.
public static class PrettyPanelRenderer
{
    public const int Width = 1024;
    public const int Height = 420; // Minimum; enabled sections grow the image.

    private static readonly SKColor BackgroundColor = new(20, 34, 45);
    private static readonly SKColor Ink = SKColors.White;
    private static readonly SKColor Muted = new(199, 208, 216);

    public static PanelImage Render(ShardBotConfig config, ShardSnapshot snapshot,
        byte[]? artwork = null, DateTimeOffset? now = null,
        string? playtimeLeaderboard = null, string? valheimJoinCode = null)
    {
        if (artwork is not null && !PrettyPanelArtwork.SafeImage(artwork)) artwork = null;
        bool onlineSection = config.DisplayOnlinePlayerList && snapshot.IsRunning;
        bool leaderboardSection = config.DisplayPlaytimeLeaderboard;
        bool sections = onlineSection || leaderboardSection;
        var extras = new List<(string Label, string Value)>();
        if (!string.IsNullOrWhiteSpace(config.ModpackUrl))
            extras.Add(("MODPACK", config.ModpackUrl));
        if (!string.IsNullOrWhiteSpace(config.ServerPassword))
            extras.Add(("SERVER PASSWORD", config.ServerPassword));
        if (config.ValheimJoinCode && !string.IsNullOrWhiteSpace(valheimJoinCode))
            extras.Add(("VALHEIM JOIN CODE", valheimJoinCode));
        if (!string.IsNullOrWhiteSpace(config.AdditionalEmbedFieldTitle) &&
            !string.IsNullOrWhiteSpace(config.AdditionalEmbedFieldText))
            extras.Add((config.AdditionalEmbedFieldTitle, config.AdditionalEmbedFieldText));
        string footer = string.IsNullOrWhiteSpace(config.BotTagline) ? "Powered by Splinter" : config.BotTagline;
        int height = Height + (sections ? 234 : 0) + extras.Count * 58 + 44;

        using SKSurface surface = SKSurface.Create(new SKImageInfo(Width, height))
            ?? throw new InvalidOperationException("Skia could not create the Pretty Panel surface.");
        SKCanvas canvas = surface.Canvas;
        canvas.Clear(BackgroundColor);
        if (artwork is not null)
        {
            using SKBitmap? background = SKBitmap.Decode(artwork);
            if (background is not null)
            {
                float sw = background.Width;
                float sh = background.Height;
                float ratio = (float)Width / height;
                SKRect crop = sw / sh > ratio
                    ? new SKRect((sw - sh * ratio) / 2, 0, (sw + sh * ratio) / 2, sh)
                    : new SKRect(0, (sh - sw / ratio) / 2, sw, (sh + sw / ratio) / 2);
                using SKImage image = SKImage.FromBitmap(background);
                using SKImageFilter blur = SKImageFilter.CreateBlur(6f, 6f);
                using var paint = new SKPaint { IsAntialias = true, ImageFilter = blur };
                canvas.DrawImage(image, crop, new SKRect(0, 0, Width, height),
                    new SKSamplingOptions(SKFilterMode.Linear), paint);
            }
        }
        Rect(canvas, new SKRect(0, 0, Width, height), new SKColor(5, 13, 20, 172));
        bool transitioning = InfoPanelRenderer.IsTransitionStatus(snapshot);
        SKColor accent = transitioning ? new SKColor(230, 164, 60) :
            snapshot.IsRunning ? new SKColor(36, 204, 120) : new SKColor(239, 86, 98);
        string state = transitioning ? LifecycleNotifications.StatusLabel(snapshot.Status) :
            snapshot.IsRunning ? "READY" : "OFFLINE";
        string name = string.IsNullOrWhiteSpace(config.ServerDisplayName)
            ? snapshot.Name : config.ServerDisplayName.Trim();
        string address = OneLine(config.ServerConnectionUrl, 140);
        string players = config.ShowPlayerCount
            ? (snapshot.Players?.ToString(CultureInfo.InvariantCulture) ?? "–") + " / " +
              (snapshot.MaxPlayers?.ToString(CultureInfo.InvariantCulture) ?? "–")
            : "HIDDEN";
        string cpu = snapshot.IsRunning && snapshot.CpuPercent is double cpuNumber
            ? cpuNumber.ToString("0.#", CultureInfo.InvariantCulture) + "%" : "–";
        string memory = snapshot.IsRunning && snapshot.MemoryMb is double memoryNumber
            ? (memoryNumber >= 1024 ? (memoryNumber / 1024).ToString("0.0", CultureInfo.InvariantCulture) + " GB" :
                memoryNumber.ToString("0", CultureInfo.InvariantCulture) + " MB") : "–";
        DateTimeOffset currentTime = now ?? DateTimeOffset.UtcNow;
        string uptime = snapshot.IsRunning && snapshot.StartedAt is DateTimeOffset started &&
            started <= currentTime && started > currentTime.AddYears(-1)
            ? Duration(currentTime - started) : "–";

        using SKTypeface normal = HostTypeface(bold: false);
        using SKTypeface bold = HostTypeface(bold: true);
        using var nameFont = new SKFont(bold, 48);
        using var labelFont = new SKFont(bold, 17);
        using var valueFont = new SKFont(bold, 31);
        using var textFont = new SKFont(normal, 21);
        using var smallFont = new SKFont(normal, 17);
        using var statusFont = new SKFont(bold, 21);
        using var headerFont = new SKFont(bold, 23);

        Rect(canvas, new SKRect(0, 0, 12, height), accent);
        RoundRect(canvas, new SKRect(34, 24, 225, 68), 8, accent);
        FitText(canvas, OneLine(state.ToUpperInvariant(), 25), 49, 54, 164, statusFont, new SKColor(7, 20, 20));
        FitText(canvas, name, 42, 137, 938, nameFont, Ink);
        FitText(canvas, snapshot.Name, 44, 176, 930, textFont, Muted);

        DrawMetric(canvas, 42, "PLAYERS", players, labelFont, valueFont,
            config.ShowPlayerCount && snapshot.MaxPlayers is > 0 && snapshot.Players is int count
                ? Math.Clamp((float)count / snapshot.MaxPlayers.Value, 0, 1) : null, accent);
        DrawMetric(canvas, 289, "UPTIME", uptime, labelFont, valueFont, null, accent);
        DrawMetric(canvas, 536, "CPU", cpu, labelFont, valueFont,
            snapshot.CpuPercent is double c ? Math.Clamp((float)c / 100f, 0, 1) : null, accent);
        DrawMetric(canvas, 783, "MEMORY", memory, labelFont, valueFont,
            snapshot.MemoryMb is double used && snapshot.MemoryLimitMb is > 0
                ? Math.Clamp((float)(used / snapshot.MemoryLimitMb.Value), 0, 1) : null, accent);

        FitText(canvas, !string.IsNullOrWhiteSpace(address) ? "JOIN: " + address : "JOIN ADDRESS NOT CONFIGURED",
            44, 381, 936, textFont, !string.IsNullOrWhiteSpace(address) ? Ink : Muted);

        int y = Height;
        if (sections)
        {
            int x1 = 42;
            int x2 = onlineSection && leaderboardSection ? 536 : 42;
            int w1 = onlineSection && leaderboardSection ? 446 : 940;
            int w2 = onlineSection && leaderboardSection ? 446 : 940;
            if (onlineSection)
            {
                RoundRect(canvas, new SKRect(x1, y, x1 + w1, y + 212), 12, new SKColor(16, 27, 38, 230));
                FitText(canvas, "ONLINE PLAYERS", x1 + 18, y + 34, w1 - 36, headerFont, Ink);
                string[] names = snapshot.OnlinePlayers?.Select(p => OneLine(p.Name, 52))
                    .Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
                if (snapshot.OnlinePlayers is null)
                    FitText(canvas, "Not reported by AMP", x1 + 18, y + 72, w1 - 36, textFont, Muted);
                else if (names.Length == 0)
                    FitText(canvas, "No players online", x1 + 18, y + 72, w1 - 36, textFont, Muted);
                else
                {
                    int linesPerColumn = onlineSection && leaderboardSection ? 5 : 5;
                    int columns = 2;
                    int maxNames = linesPerColumn * columns;
                    int columnWidth = (w1 - 36) / columns;
                    for (int i = 0; i < Math.Min(names.Length, maxNames); i++)
                        FitText(canvas, names[i], x1 + 18 + (i / linesPerColumn) * columnWidth,
                            y + 74 + (i % linesPerColumn) * 25, columnWidth - 8, smallFont, Ink);
                    if (names.Length > maxNames)
                        FitText(canvas, $"+{names.Length - maxNames} more online", x1 + 18, y + 202,
                            w1 - 36, smallFont, Muted);
                }
            }
            if (leaderboardSection)
            {
                RoundRect(canvas, new SKRect(x2, y, x2 + w2, y + 212), 12, new SKColor(16, 27, 38, 230));
                FitText(canvas, "TOP 5 BY PLAYTIME", x2 + 18, y + 34, w2 - 36, headerFont, Ink);
                string[] lines = (playtimeLeaderboard ?? "No playtime recorded yet.")
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                for (int i = 0; i < Math.Min(5, lines.Length); i++)
                    FitText(canvas, OneLine(lines[i], 90), x2 + 18, y + 74 + i * 26,
                        w2 - 36, smallFont, i == 0 ? Ink : Muted);
            }
            y += 234;
        }
        foreach ((string label, string value) in extras)
        {
            RoundRect(canvas, new SKRect(42, y, 982, y + 48), 8, new SKColor(16, 27, 38, 230));
            FitText(canvas, OneLine(label.ToUpperInvariant(), 35), 57, y + 30, 220, labelFont, Muted);
            FitText(canvas, OneLine(value, 240), 286, y + 31, 672, textFont, Ink);
            y += 58;
        }
        FitText(canvas, footer, 44, height - 16, 925, smallFont, Muted);
        using SKImage outputImage = surface.Snapshot();
        using SKData png = outputImage.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("Skia could not encode the Pretty Panel PNG.");
        byte[] bytes = png.ToArray();
        return new PanelImage(bytes, "splinter-info.png", Convert.ToHexString(SHA256.HashData(bytes)));
    }

    private static void DrawMetric(SKCanvas canvas, int x, string label, string value,
        SKFont labelFont, SKFont valueFont, float? progress, SKColor accent)
    {
        RoundRect(canvas, new SKRect(x, 211, x + 231, 338), 12, new SKColor(20, 30, 40, 213));
        FitText(canvas, label, x + 17, 248, 198, labelFont, Muted);
        FitText(canvas, value, x + 17, 292, 198, valueFont, Ink);
        RoundRect(canvas, new SKRect(x + 17, 315, x + 214, 321), 3, new SKColor(255, 255, 255, 40));
        if (progress is > 0)
            RoundRect(canvas, new SKRect(x + 17, 315, x + 17 + 197 * progress.Value, 321), 3, accent);
    }

    private static void Rect(SKCanvas canvas, SKRect rect, SKColor color)
    {
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        canvas.DrawRect(rect, paint);
    }

    private static void RoundRect(SKCanvas canvas, SKRect rect, float radius, SKColor color)
    {
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        canvas.DrawRoundRect(rect, radius, radius, paint);
    }

    private static void FitText(SKCanvas canvas, string? value, float x, float baseline,
        float maxWidth, SKFont font, SKColor color)
    {
        string text = OneLine(value, 300);
        if (maxWidth <= 0) return;
        while (text.Length > 1 && font.MeasureText(text) > maxWidth)
            text = text[..^2] + "…";
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        canvas.DrawText(text, x, baseline, SKTextAlign.Left, font, paint);
    }

    private static SKTypeface HostTypeface(bool bold)
    {
        string[] fonts = OperatingSystem.IsWindows()
            ? (bold ? [@"C:\Windows\Fonts\segoeuib.ttf", @"C:\Windows\Fonts\arialbd.ttf"]
                    : [@"C:\Windows\Fonts\segoeui.ttf", @"C:\Windows\Fonts\arial.ttf"])
            : (bold ? ["/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf",
                       "/usr/share/fonts/truetype/liberation2/LiberationSans-Bold.ttf",
                       "/usr/share/fonts/truetype/noto/NotoSans-Bold.ttf"]
                    : ["/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
                       "/usr/share/fonts/truetype/liberation2/LiberationSans-Regular.ttf",
                       "/usr/share/fonts/truetype/noto/NotoSans-Regular.ttf"]);
        foreach (string path in fonts)
        {
            if (!File.Exists(path)) continue;
            SKTypeface? typeface = SKTypeface.FromFile(path);
            if (typeface is not null) return typeface;
        }
        SKTypeface? fallback = SKTypeface.FromFamilyName(
            OperatingSystem.IsWindows() ? "Segoe UI" : "DejaVu Sans",
            bold ? SKFontStyle.Bold : SKFontStyle.Normal);
        if (fallback is not null) return fallback;
        throw new InvalidOperationException("No host font available for Pretty Panel rendering.");
    }

    internal static string OneLine(string? value, int maxChars)
    {
        string normalized = (value ?? "").Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
        return normalized.Length <= maxChars ? normalized : normalized[..(maxChars - 1)] + "…";
    }

    private static string Duration(TimeSpan duration) => duration.TotalDays >= 1
        ? $"{(int)duration.TotalDays}d {duration.Hours}h" : duration.TotalHours >= 1
        ? $"{(int)duration.TotalHours}h {duration.Minutes}m" : duration.TotalMinutes >= 1
        ? $"{(int)duration.TotalMinutes}m" : "<1m";
}
