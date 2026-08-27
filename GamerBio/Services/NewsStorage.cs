using System.Globalization;

namespace GamerBio.Services;

// 스케줄러가 생성한 일일 뉴스 HTML 파일을 읽음.
public class NewsStorage
{
    public const string DateFormat = "yyyy-MM-dd";

    public const long MaxFileBytes = 10 * 1024 * 1024; // 10 MB per day

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

    // 파일이름은 항상 yyyy-MM-dd.html형식이어야 하며 잘못된 날짜 문자열은 null을 반환
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

    public async Task<string?> SaveHtmlAsync(string date, byte[] html, CancellationToken ct)
    {
        if (!DateOnly.TryParseExact(date, DateFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsed))
        {
            return null;
        }

        var key = parsed.ToString(DateFormat, CultureInfo.InvariantCulture);
        var path = Path.Combine(_root, key + ".html");
        await File.WriteAllBytesAsync(path, html, ct);
        _logger.LogInformation("News saved for {Date} ({Bytes} bytes)", key, html.Length);
        return key;
    }
}
