namespace GamerBio.Models;

// A single emotion snapshot from the PC's DeepFace analyzer.
// Scores are percentages over the gaming-relevant labels and sum to ~100:
//   angry / fear / happy / surprise / neutral
public record EmotionReading(
    string Dominant,
    IReadOnlyDictionary<string, double> Scores,
    DateTimeOffset ReceivedAt
);
