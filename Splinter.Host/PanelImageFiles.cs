using System.Security.Cryptography;

namespace Splinter.Host;

// Local image feature from AMP-Discord-Bot 1.35: game image URL is still the
// thumbnail; an optional local file is the embed's large image.
public sealed record PanelImage(byte[] Bytes, string FileName, string Hash);

public static class PanelImageFiles
{
    public const int MaxBytes = 8 * 1024 * 1024;
    public static bool ValidSetting(string? value) =>
        string.IsNullOrWhiteSpace(value) || (value.Length <= 512 &&
            !Path.IsPathRooted(value) && !value.Contains(':') &&
            !value.Any(char.IsControl) &&
            new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp" }
                .Contains(Path.GetExtension(value), StringComparer.OrdinalIgnoreCase) &&
            value.Split('/', '\\').All(s => s is not (".." or ".") && s.Length > 0));

    public static string Resolve(string appDirectory, string imageFile)
    {
        if (!ValidSetting(imageFile) || string.IsNullOrWhiteSpace(imageFile))
            throw new InvalidDataException("Invalid panel image filename.");
        string root = Path.GetFullPath(appDirectory);
        string file = Path.GetFullPath(Path.Combine(root, imageFile));
        string relative = Path.GetRelativePath(root, file);
        if (Path.IsPathRooted(relative) || relative is ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Panel image is outside the Shard application directory.");
        // Do not let a local symlink/junction escape the application directory.
        string cursor = root;
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("Linked application directory cannot supply an image.");
        string[] segments = relative.Split(Path.DirectorySeparatorChar);
        foreach (string segment in segments)
        {
            cursor = Path.Combine(cursor, segment);
            if (File.Exists(cursor) || Directory.Exists(cursor))
            {
                if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                    throw new UnauthorizedAccessException("Linked image paths are not permitted.");
            }
        }
        return file;
    }

    public static string Stamp(string appDirectory, string name)
    {
        string path = Resolve(appDirectory, name);
        var info = new FileInfo(path);
        return info.Exists ? $"{name}|{info.Length}|{info.LastWriteTimeUtc.Ticks}" : $"{name}|missing";
    }

    public static PanelImage? Read(string appDirectory, string name)
    {
        string path = Resolve(appDirectory, name);
        if (!File.Exists(path)) return null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                if (file.Length <= 0 || file.Length > MaxBytes)
                    throw new InvalidDataException("Panel image exceeds the supported size.");
                byte[] bytes = new byte[(int)file.Length];
                file.ReadExactly(bytes);
                string ext = Path.GetExtension(name).ToLowerInvariant();
                if (!IsImage(bytes, ext))
                    throw new InvalidDataException("Panel image content does not match its file extension.");
                return new PanelImage(bytes, "splinter-info" + ext,
                    Convert.ToHexString(SHA256.HashData(bytes)));
            }
            catch (IOException) when (attempt == 0) { Thread.Sleep(60); }
        }
        return null;
    }

    public static bool IsImage(ReadOnlySpan<byte> b, string extension) => extension switch
    {
        ".png" => b.Length > 8 && b[..8].SequenceEqual(new byte[] {137,80,78,71,13,10,26,10}),
        ".jpg" or ".jpeg" => b.Length > 4 && b[0] == 0xFF && b[1] == 0xD8 && b[^2] == 0xFF && b[^1] == 0xD9,
        ".gif" => b.Length > 6 && (b[..6].SequenceEqual("GIF87a"u8) || b[..6].SequenceEqual("GIF89a"u8)),
        ".webp" => b.Length > 12 && b[..4].SequenceEqual("RIFF"u8) && b.Slice(8,4).SequenceEqual("WEBP"u8),
        _ => false
    };
}
