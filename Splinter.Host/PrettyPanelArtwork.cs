using System.Net;
using System.Net.Sockets;
using SkiaSharp;

namespace Splinter.Host;

// Resolves AMP template artwork without deploying duplicate game-image assets.
// Only HTTPS external images; all remote connections enforce public IPv4 at
// connect time to prevent private-network/server-metadata requests.
internal static partial class PrettyPanelArtwork
{
    private const int MaxImageBytes = 5 * 1024 * 1024;
    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        ConnectCallback = async (context, cancellation) =>
        {
            IPAddress[] addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellation);
            Exception? failure = null;
            foreach (IPAddress ip in addresses.Where(IsPublicIpv4))
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(ip, context.DnsEndPoint.Port, cancellation);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (Exception ex) when (ex is SocketException or IOException)
                {
                    failure = ex;
                    socket.Dispose();
                }
            }
            throw new IOException("Artwork host has no reachable public IPv4 address.", failure);
        }
    }) { Timeout = TimeSpan.FromSeconds(8) };

    internal static Uri? ToUri(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;
        string value = source.Trim();
        if (value.StartsWith("none:", StringComparison.OrdinalIgnoreCase)) return null;
        if (value.StartsWith("steam:", StringComparison.OrdinalIgnoreCase))
        {
            string id = value[6..];
            if (id.Length is < 1 or > 12 || !id.All(char.IsAsciiDigit)) return null;
            return new Uri($"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{id}/library_hero.jpg");
        }
        if (value.StartsWith("url:", StringComparison.OrdinalIgnoreCase)) value = value[4..];
        if (value.Length > 2048 || !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrWhiteSpace(uri.UserInfo) ||
            uri.Host.Length > 253 || uri.Port != 443 || uri.IsLoopback)
            return null;
        // Avoid IP literals, credentials, link-local hostnames and internal FQDNs.
        if (IPAddress.TryParse(uri.Host, out _) || !uri.Host.Contains('.') ||
            uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) return null;
        return uri;
    }

    private static bool IsPublicIpv4(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        byte[] b = address.GetAddressBytes();
        if (b[0] is 0 or 10 or 127 or 169 or >= 224) return false;
        if (b[0] == 100 && b[1] is >= 64 and <= 127) return false;
        if (b[0] == 172 && b[1] is >= 16 and <= 31) return false;
        if (b[0] == 192 && (b[1] is 0 or 168)) return false;
        if (b[0] == 198 && b[1] is 18 or 19) return false;
        if (b[0] == 192 && b[1] == 0 && b[2] == 2) return false;
        if (b[0] == 198 && b[1] == 51 && b[2] == 100) return false;
        if (b[0] == 203 && b[1] == 0 && b[2] == 113) return false;
        return true;
    }

    // The browser serves Minecraft artwork from the currently running AMP 3
    // Web API's wwwroot. Read the same on-disk asset, not an HTTP port that
    // may require authentication or move between releases.
    internal static string? LocalIconPath(string? source, string activeWebApiDirectory)
    {
        string? file = source switch
        {
            "amp-icon:MinecraftJava" => "MinecraftJava.jpg",
            "amp-icon:MinecraftBedrock" => "MinecraftBedrock.jpg",
            _ => null
        };
        return file is null ? null : Path.Combine(activeWebApiDirectory, "wwwroot", "assets", "app-icons", file);
    }

    private static byte[]? ReadLocalIcon(string? source)
    {
        // AppContext.BaseDirectory normally points to the running webapi
        // directory. ProcessPath is an alternative for the startup-hook host.
        foreach (string? root in new[] { AppContext.BaseDirectory,
                     Path.GetDirectoryName(Environment.ProcessPath) }.Distinct(StringComparer.Ordinal))
        {
            if (string.IsNullOrEmpty(root)) continue;
            string? path = LocalIconPath(source, root);
            if (path is null || !File.Exists(path)) continue;
            try
            {
                var info = new FileInfo(path);
                if (info.Length is <= 0 or > MaxImageBytes) continue;
                byte[] bytes = File.ReadAllBytes(path);
                if (SafeImage(bytes)) return bytes;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return null;
    }

    public static async Task<byte[]?> DownloadAsync(string? source, CancellationToken ct)
    {
        if (source?.StartsWith("amp-icon:", StringComparison.Ordinal) == true)
            return ReadLocalIcon(source);
        Uri? uri = ToUri(source);
        if (uri is null) return null;
        // Match AMP's wide Steam library hero where available, but fall back
        // to the standard header if a title has no hero image.
        Uri[] urls = source?.StartsWith("steam:", StringComparison.OrdinalIgnoreCase) == true
            ? [uri, new Uri(uri.AbsoluteUri.Replace("/library_hero.jpg", "/header.jpg", StringComparison.Ordinal))]
            : [uri];
        foreach (Uri target in urls)
        {
            try
            {
                byte[]? image = await DownloadOneAsync(target, ct).ConfigureAwait(false);
                if (image is not null) return image;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
            {
                // Steam sometimes lacks a hero image; try its header instead.
                // Other failures are nonfatal and the caller uses a neutral card.
            }
        }
        return null;
    }

    private static async Task<byte[]?> DownloadOneAsync(Uri uri, CancellationToken ct)
    {
        using var response = await Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK ||
            response.Content.Headers.ContentLength is > MaxImageBytes) return null;
        await using Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var data = new MemoryStream();
        byte[] buffer = new byte[32 * 1024];
        while (true)
        {
            int n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (n <= 0) break;
            if (data.Length + n > MaxImageBytes) return null;
            data.Write(buffer, 0, n);
        }
        byte[] bytes = data.ToArray();
        return SafeImage(bytes) ? bytes : null;
    }

    public static bool SafeImage(byte[]? bytes)
    {
        if (bytes is not { Length: > 0 and <= MaxImageBytes }) return false;
        try
        {
            using var stream = new SKMemoryStream(bytes);
            using SKCodec? codec = SKCodec.Create(stream);
            if (codec is null) return false;
            var info = codec.Info;
            return info.Width is >= 100 and <= 4096 && info.Height is >= 100 and <= 4096 &&
                codec.EncodedFormat is SKEncodedImageFormat.Png or SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Webp;
        }
        catch { return false; }
    }
}
