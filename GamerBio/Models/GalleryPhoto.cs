namespace GamerBio.Models;

/// <summary>
/// Metadata row for a user-uploaded gallery photo. The image bytes live on the
/// RPi5 filesystem (see <c>GalleryStorage</c>); this row only points at them.
/// </summary>
public class GalleryPhoto
{
    public long Id { get; set; }

    /// <summary>Random storage filename on disk (e.g. <c>{guid}.jpg</c>).</summary>
    public string StoredName { get; set; } = string.Empty;

    /// <summary>Original filename as uploaded by the user (display only).</summary>
    public string OriginalName { get; set; } = string.Empty;

    public string ContentType { get; set; } = "application/octet-stream";

    public long SizeBytes { get; set; }

    public string? Caption { get; set; }

    public DateTimeOffset UploadedAt { get; set; }
}
