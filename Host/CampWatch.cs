namespace AgenticSdr.Host;

/// <summary>
/// While Camp mode is on, tunes the schedule entry that covers the local time.
/// Audio stays in the camp fan-out until a page subscribes. Explore mode leaves the radio alone.
/// </summary>
public sealed class CampWatch(CampBook book, RadioSession session, ILogger<CampWatch> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await book.WaitAsync(TimeSpan.FromSeconds(1), stoppingToken);
                await Apply(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Camp schedule tick failed.");
            }
        }

        await session.RetargetCampAsync(null, CancellationToken.None);
    }

    private async Task Apply(CancellationToken cancellationToken)
    {
        session.PublishCamp(book.Mode, book.Revision);
        session.SetCampSquelch(book.Squelch);
        CampRequest? request = book.Desired(DateTime.Now);
        if (request is not null && !session.IsOpen)
        {
            await session.RetargetCampAsync(null, cancellationToken);
            session.NoteCampIdle("Camp mode. Start the radio and it will tune the schedule.");
            return;
        }

        await session.RetargetCampAsync(request, cancellationToken);
        if (book.Mode != "camp")
        {
            session.ClearCampNote();
            return;
        }

        if (request is not null)
            return;
        session.NoteCampIdle(book.Paused
            ? "Camping paused."
            : "Camp workspace. Press Camp on a frequency, or wait for a scheduled window.");
    }
}
