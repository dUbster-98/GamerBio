using System.Globalization;

namespace GamerBio.Services;

/// <summary>
/// Reads the daily news HTML files produced by the RPi's evening scheduler.
/// Files live in a configured folder (<c>News:StoragePath</c>) and are named
/// <c>yyyy-MM-dd.html</c>. Like <see cref="GalleryStorage"/> the folder is kept
/// outside wwwroot: dates are validated and files are streamed through a guarded
/// endpoint so the storage path stays server-side and nothing is directly
/// browsable (no path traversal — only well-formed dates resolve to a file).
/// </summary>
public class NewsStorage
{
    public const string DateFormat = "yyyy-MM-dd";

    private readonly string _root;
    private readonly ILogger<NewsStorage> _logger;

    public NewsStorage(IConfiguration config, IWebHostEnvironment env, ILogger<NewsStorage> logger)
    {
        _logger = logger;
        var configured = config["News:StoragePath"];
        _root = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(env.ContentRootPath, "news-store")
            : configured;

        Directory.CreateDirectory(_root);
        _logger.LogInformation("News files read from {Root}", _root);
    }

    /// <summary>Available news dates, newest first (parsed from file names).</summary>
    public IReadOnlyList<DateOnly> AvailableDates()
    {
        if (!Directory.Exists(_root))
        {
            return Array.Empty<DateOnly>();
        }

        var dates = new List<DateOnly>();
        foreach (var path in Directory.EnumerateFiles(_root, "*.html"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (DateOnly.TryParseExact(name, DateFormat, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var date))
            {
                dates.Add(date);
            }
        }

        dates.Sort();
        dates.Reverse();
        return dates;
    }

    /// <summary>
    /// Resolves the on-disk path for a validated date, or null if the date is
    /// malformed or the file is missing. Re-formatting the parsed date (rather
    /// than trusting the raw string) keeps traversal characters out of the path.
    /// </summary>
    public string? PathFor(string date)
    {
        if (!DateOnly.TryParseExact(date, DateFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsed))
        {
            return null;
        }

        var fileName = parsed.ToString(DateFormat, CultureInfo.InvariantCulture) + ".html";
        var path = Path.Combine(_root, fileName);
        return File.Exists(path) ? path : null;
    }
}
