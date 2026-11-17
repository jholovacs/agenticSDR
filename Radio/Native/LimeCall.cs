using System.Collections.Concurrent;

namespace AgenticSdr.Radio;

/// <summary>
/// LimeSuiteNG's Cypress USB layer keeps its device on the thread that created it.
/// A later call from another thread waits forever, so every Lime call uses this thread.
/// </summary>
internal static class LimeCall
{
    private static readonly BlockingCollection<Action> Queue = new();

    static LimeCall()
    {
        var thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "LimeSDR"
        };
        if (OperatingSystem.IsWindows())
            thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    public static T Run<T>(Func<T> call)
    {
        _ = Queue;
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Queue.Add(() =>
        {
            try
            {
                done.SetResult(call());
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        });
        return done.Task.GetAwaiter().GetResult();
    }

    private static void Loop()
    {
        foreach (Action action in Queue.GetConsumingEnumerable())
            action();
    }
}
