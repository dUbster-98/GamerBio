using Microsoft.AspNetCore.SignalR;

namespace GamerBio.Hubs;

public class BioSignalHub : Hub
{
    public const string Path = "/hubs/biosignal";
    public const string BioSignalReceived = "BioSignalReceived";
    public const string TensionUpdated = "TensionUpdated";
}
