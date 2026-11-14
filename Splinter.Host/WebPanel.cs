using System.Net;
using System.Text;

namespace Splinter.Host;

// Optional standalone status document. No HTTP endpoints, AMP credentials or Discord token access.
public static class WebPanelRenderer
{
    public static string Render(ShardBotConfig config, ShardSnapshot snapshot)
    {
        static string H(string? text) => WebUtility.HtmlEncode(text ?? "");
        string title = string.IsNullOrWhiteSpace(config.ServerDisplayName) ? snapshot.Name : config.ServerDisplayName.Trim();
        bool transitioning = InfoPanelRenderer.IsTransitionStatus(snapshot);
        string status = transitioning ? LifecycleNotifications.StatusLabel(snapshot.Status) :
            snapshot.IsRunning ? (string.IsNullOrWhiteSpace(config.OnlineStatusText) ? "Online" : config.OnlineStatusText)
            : (string.IsNullOrWhiteSpace(config.OfflineStatusText) ? "Offline" : config.OfflineStatusText);
        string configuredColor = transitioning ? config.TransitionEmbedColor :
            snapshot.IsRunning ? config.OnlineEmbedColor : config.OfflineEmbedColor;
        string accent = InfoPanelRenderer.TryParseColor(configuredColor, out _) ? configuredColor : "#8E9AAF";
        var html = new StringBuilder(3600);
        html.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        html.Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'; img-src https: http:; base-uri 'none'; form-action 'none'\">");
        html.Append("<title>").Append(H(title)).Append(" - Server Status</title>");
        html.Append("<style>:root{color-scheme:dark;--accent:").Append(accent)
            .Append("}*{box-sizing:border-box}body{font-family:system-ui,Arial,sans-serif;margin:0;background:#101620;color:#ecf1f8;padding:clamp(12px,4vw,32px)}")
            .Append("main{max-width:850px;margin:auto;background:#1c2634;border:1px solid #354154;border-left:5px solid var(--accent);border-radius:12px;padding:clamp(16px,4vw,30px)}")
            .Append("h1{font-size:clamp(1.5rem,4vw,2rem);margin:.2em 0}h2{font-size:1rem;margin:0 0 10px;color:#bcc8d9}")
            .Append(".status{color:var(--accent);font-weight:700}.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(170px,1fr));gap:12px;margin:24px 0}")
            .Append(".box{background:#141d29;padding:14px;border-radius:8px;overflow-wrap:anywhere}.value{font-weight:600;font-size:1.15rem}")
            .Append("a{color:#94c5ff}img{max-width:100%;height:auto;border-radius:8px;margin-top:16px}footer{color:#9caec4;font-size:.85rem;margin-top:18px}</style></head><body><main>");
        html.Append("<h1>").Append(H(title)).Append("</h1><p class=\"status\">").Append(H(status)).Append("</p><div class=\"grid\">");
        if (!string.IsNullOrWhiteSpace(config.ServerConnectionUrl)) AddBox(html, "Connect", config.ServerConnectionUrl);
        if (config.ShowPlayerCount)
        {
            string players = snapshot.Players.HasValue ? snapshot.Players.Value.ToString() : "Not reported";
            if (snapshot.MaxPlayers.HasValue) players += " / " + snapshot.MaxPlayers.Value;
            AddBox(html, "Players", players);
        }
        if (snapshot.IsRunning)
        {
            AddBox(html, "CPU", snapshot.CpuPercent.HasValue ? $"{snapshot.CpuPercent.Value:0.#}%" : "Not reported");
            string memory = snapshot.MemoryMb.HasValue ? $"{snapshot.MemoryMb.Value:0.#} MB" : "Not reported";
            if (snapshot.MemoryLimitMb is > 0) memory += $" / {snapshot.MemoryLimitMb.Value:0.#} MB";
            AddBox(html, "RAM", memory);
        }
        html.Append("</div>");
        if (!string.IsNullOrWhiteSpace(config.ServerPassword))
            html.Append("<p><strong>Server Password:</strong> <code>").Append(H(config.ServerPassword)).Append("</code></p>");
        if (!string.IsNullOrWhiteSpace(config.ModpackUrl))
        {
            html.Append("<p><strong>Modpack:</strong> ");
            if (InfoPanelRenderer.TryModpackUrl(config.ModpackUrl, out string link))
                html.Append("<a rel=\"noopener noreferrer\" target=\"_blank\" href=\"").Append(H(link)).Append("\">Open modpack</a>");
            else html.Append(H(config.ModpackUrl));
            html.Append("</p>");
        }
        if (!string.IsNullOrWhiteSpace(config.AdditionalEmbedFieldTitle) &&
            !string.IsNullOrWhiteSpace(config.AdditionalEmbedFieldText))
            html.Append("<section class=\"box\"><h2>").Append(H(config.AdditionalEmbedFieldTitle)).Append("</h2><p>")
                .Append(H(config.AdditionalEmbedFieldText)).Append("</p></section>");
        string image = InfoPanelRenderer.TryWebUrl(config.GameImageUrl, out string small) ? small : "";
        if (image.Length > 0) html.Append("<img alt=\"Game image\" loading=\"lazy\" src=\"").Append(H(image)).Append("\">");
        if (config.DisplayManageButton && InfoPanelRenderer.TryWebUrl(config.ManagementUrl, out string management))
            html.Append("<p><a rel=\"noopener noreferrer\" target=\"_blank\" href=\"").Append(H(management)).Append("\">Manage Server</a></p>");
        html.Append("<footer>").Append(H(config.BotTagline)).Append("</footer></main></body></html>");
        return html.ToString();

        static void AddBox(StringBuilder html, string name, string value) =>
            html.Append("<div class=\"box\"><h2>").Append(H(name)).Append("</h2><div class=\"value\">")
                .Append(H(value)).Append("</div></div>");
    }
}

public static class WebPanelPublisher
{
    public static void Update(ShardBotConfig config, ShardSnapshot snapshot)
    {
        string file = ConfigStore.WebPanelPath(snapshot.Id);
        if (!config.Enabled || !config.EnableWebPanel)
        {
            if (File.Exists(file)) File.Delete(file);
            return;
        }

        string document = WebPanelRenderer.Render(config, snapshot);
        // Avoid rewriting public files every discovery poll when nothing changed.
        if (File.Exists(file) && File.ReadAllText(file) == document) return;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        string temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, document, new UTF8Encoding(false));
            File.Move(temp, file, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
