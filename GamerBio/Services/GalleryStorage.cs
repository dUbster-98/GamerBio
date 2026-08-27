using GamerBio.Models;

namespace GamerBio.Services;

/// 파이의 내부 스토리지에 사진을 저장하고, 외부에서 직접 접근할 수 없도록 보호
public class GalleryStorage
{
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
