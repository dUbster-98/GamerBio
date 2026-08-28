namespace GamerBio.Models;

public class BioSignal
{
    public long Id { get; set; }

    /// <summary>Heart rate, or null when the PPG sensor had no skin contact.
    /// Absent BPM drops out of the fusion instead of being scored as zero.</summary>
    public int? Bpm { get; set; }
    public int Gsr { get; set; }

    /// <summary>Perfusion index [%] = pulsatile AC ÷ DC of the IR PPG waveform.
    /// Falls as sympathetic arousal constricts peripheral vessels, so it tracks the
    /// same autonomic axis as GSR through a different effector (vessels vs sweat glands).
    ///
    /// Null whenever the firmware could not measure a beat (no contact, motion, or
    /// amplitude below its noise floor). Null is NOT zero here — zero would read as
    /// "vessels fully constricted", i.e. maximum stress. The analyzer drops the factor
    /// instead, the same way it handles an absent BPM.
    ///
    /// The absolute value is device- and placement-specific and is never scored directly;
    /// see <see cref="Services.TensionAnalyzer"/>, which scores the drop from a rolling
    /// personal baseline.</summary>
    public double? Pi { get; set; }

    public double? SkinTemp { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
}
