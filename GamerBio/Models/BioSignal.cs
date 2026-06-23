namespace GamerBio.Models;

public class BioSignal
{
    public long Id { get; set; }
    public int Bpm { get; set; }
    public int Gsr { get; set; }
    public double? SkinTemp { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
}
