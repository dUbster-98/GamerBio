using GamerBio.Models;

namespace GamerBio.Services;

/// <summary>
/// In-process fan-out of live updates to the server-rendered Blazor pages.
///
/// The pages run inside this very process, so subscribing them through a
/// SignalR <c>HubConnection</c> made the server dial its own public URL — out
/// through the Cloudflare Tunnel and back in — once per page visit, purely to
/// talk to itself. That handshake sat inside <c>OnInitializedAsync</c>, so the
/// interactive component could not finish initializing (and therefore could not
/// render its data) until the round trip completed.
///
/// A plain event does the same job with no I/O and no extra WebSocket. The
/// SignalR hub stays mapped for browser/external clients; server-side pages
/// subscribe here instead.
/// </summary>
public sealed class BioEventBus(ILogger<BioEventBus> logger)
{
    public event Action<BioSignal>? BioSignalReceived;
    public event Action<TensionReading>? TensionUpdated;
    public event Action<EmotionReading>? EmotionUpdated;
    public event Action<GalleryPhoto>? GalleryPhotoAdded;
    public event Action<DeadlyEvent>? DeadlyEventRecorded;

    public void PublishBioSignal(BioSignal signal) => Raise(BioSignalReceived, signal);

    public void PublishTension(TensionReading tension) => Raise(TensionUpdated, tension);

    public void PublishEmotion(EmotionReading emotion) => Raise(EmotionUpdated, emotion);

    public void PublishGalleryPhoto(GalleryPhoto photo) => Raise(GalleryPhotoAdded, photo);

    public void PublishDeadlyEvent(DeadlyEvent entry) => Raise(DeadlyEventRecorded, entry);

    // Subscribers are Blazor pages, which can be mid-teardown when an event
    // fires. One faulted handler must not fail the API request that published
    // the event, nor stop the remaining pages from getting it.
    private void Raise<T>(Action<T>? handlers, T payload)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<T>)handler)(payload);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "BioEventBus subscriber threw on {Event}", typeof(T).Name);
            }
        }
    }
}
