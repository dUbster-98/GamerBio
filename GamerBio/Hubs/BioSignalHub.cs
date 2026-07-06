using Microsoft.AspNetCore.SignalR;

namespace GamerBio.Hubs;

public class BioSignalHub : Hub
{
    public const string Path = "/hubs/biosignal";
    public const string BioSignalReceived = "BioSignalReceived";
    public const string TensionUpdated = "TensionUpdated";
    public const string EmotionUpdated = "EmotionUpdated";
    public const string GalleryPhotoAdded = "GalleryPhotoAdded";
    public const string DeadlyEventRecorded = "DeadlyEventRecorded";
}
