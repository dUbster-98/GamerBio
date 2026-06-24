using GamerBio.Models;

namespace GamerBio.Services;

/// <summary>
/// Owns the on-disk location of gallery image files. Files are kept outside
/// wwwroot so they survive (when pointed at a path outside the deploy dir) and
/// are never directly browsable — they are served through a guarded endpoint.
/// Configure <c>Gallery:StoragePath</c>; defaults to a folder under ContentRoot.
/// </summary>
public class GalleryStorage
{
    // Extensions we accept. Maps to the content-type stored with each photo.
    private static readonly Dictionary<string, string> AllowedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".png"] = "image/png",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
    };

    public const long MaxFileBytes = 15 * 1024 * 1024; // 15 MB per photo

    private readonly string _root;
    private readonly ILogger<GalleryStorage> _logger;

    public GalleryStorage(IConfiguration config, IWebHostEnvironment env, ILogger<GalleryStorage> logger)
    {
        _logger = logger;
        var configured = config["Gallery:StoragePath"];
        _root = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(env.ContentRootPath, "gallery-store")
            : configured;

        Directory.CreateDirectory(_root);
        _logger.LogInformation("Gallery files stored at {Root}", _root);
    }

    public static bool IsAllowed(string fileName, out string contentType) =>
        AllowedTypes.TryGetValue(Path.GetExtension(fileName), out contentType!);

    public string PathFor(GalleryPhoto photo) => Path.Combine(_root, photo.StoredName);

    /// <summary>Writes the upload stream to disk and returns the random storage name.</summary>
    public async Task<string> SaveAsync(string originalName, Stream content, CancellationToken ct)
    {
        var ext = Path.GetExtension(originalName);
        var storedName = $"{Guid.NewGuid():N}{ext.ToLowerInvariant()}";
        var target = Path.Combine(_root, storedName);

        await using (var fs = File.Create(target))
        {
            await content.CopyToAsync(fs, ct);
        }
        return storedName;
    }

    /// <summary>Writes raw bytes (e.g. an auto-captured JPEG) and returns the storage name.</summary>
    public async Task<string> SaveBytesAsync(byte[] data, string ext, CancellationToken ct)
    {
        var storedName = $"{Guid.NewGuid():N}{ext.ToLowerInvariant()}";
        var target = Path.Combine(_root, storedName);
        await File.WriteAllBytesAsync(target, data, ct);
        return storedName;
    }

    public void Delete(GalleryPhoto photo)
    {
        try
        {
            var path = PathFor(photo);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Failed to delete gallery file {Name}", photo.StoredName);
        }
    }
}
