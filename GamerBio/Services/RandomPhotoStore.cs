namespace GamerBio.Services;

/// <summary>
/// A dedicated folder of curated images, separate from the user gallery
/// (<c>Gallery:StoragePath</c>). The Discord bot picks one at random from here.
/// Unlike the gallery there is no DB metadata — images are just dropped into the
/// folder and the bot reads them directly. Configure <c>RandomGallery:StoragePath</c>;
/// defaults to a folder under ContentRoot.
/// </summary>
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

    /// <summary>Returns a random image file path from the store, or null if empty.</summary>
    public string? PickRandom()
    {
        var files = Directory.EnumerateFiles(_root)
            .Where(f => AllowedExtensions.Contains(Path.GetExtension(f)))
            .ToList();

        return files.Count == 0 ? null : files[Random.Shared.Next(files.Count)];
    }
}
