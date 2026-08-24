namespace GamerBio.Models;

public class BioSignal
{
    public long Id { get; set; }

    /// <summary>Heart rate, or null when the PPG sensor had no skin contact.
    /// Absent BPM drops out of the fusion instead of being scored as zero.</summary>
    public int? Bpm { get; set; }
    public int Gsr { get; set; }
    public double? SkinTemp { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
}
