namespace GamerBio.Services;

/// 랜덤 사진을 저장하는 전용 폴더. 사용자 갤러리와는 별도로 관리되며,
/// Discord 봇이 RandomGallery:StoragePath 무작위로 하나를 선택한다.
public class RandomPhotoStore
{
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp",
    };

    private readonly string _root;
    private readonly ILogger<RandomPhotoStore> _logger;

    public RandomPhotoStore(IConfiguration config, IWebHostEnvironment env, ILogger<RandomPhotoStore> logger)
    {
        _logger = logger;
        var configured = config["RandomGallery:StoragePath"];
        _root = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(env.ContentRootPath, "random-store")
            : configured;

        Directory.CreateDirectory(_root);
        _logger.LogInformation("Random photo store at {Root}", _root);
    }

    public string? PickRandom()
    {
        var files = Directory.EnumerateFiles(_root)
            .Where(f => AllowedExtensions.Contains(Path.GetExtension(f)))
            .ToList();

        return files.Count == 0 ? null : files[Random.Shared.Next(files.Count)];
    }
}
