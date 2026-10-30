namespace Iris.Clipboard.IsolatedHelper;

internal interface IClipboardOccupancyNative
{
    void Bind();
    nint CreateWindow();
    bool Open(nint window);
    bool Close();
    bool Destroy(nint window);
    void Restore();
    void Record(string action);
}

// The work callback completes its actual service task/cleanup before the owner may release.
internal static class ClipboardOccupancyCoordinator
{
    internal static void Run(IClipboardOccupancyNative native, Action work, TimeSpan readyBudget,
        Func<ManualResetEventSlim, TimeSpan, bool>? readinessWaitForTesting = null)
    {
        using var ready = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var ownerErrors = new List<Exception>();
        bool accepted = false;
        var owner = new Thread(() =>
        {
            nint window = 0;
            bool opened = false;
            try
            {
                native.Bind();
                window = native.CreateWindow();
                if (window == 0) throw new InvalidOperationException("Occupancy window was not created.");
                opened = native.Open(window);
                if (!opened) throw new InvalidOperationException("Occupancy Open did not succeed.");
                native.Record("ready-before-signal");
                accepted = true;
                ready.Set();
                release.Wait();
                native.Record("release-received");
            }
            catch (Exception error) { ownerErrors.Add(error); ready.Set(); }
            finally
            {
                if (opened)
                    try { if (!native.Close()) ownerErrors.Add(new InvalidOperationException("Occupancy Close failed.")); }
                    catch (Exception error) { ownerErrors.Add(error); }
                if (window != 0)
                    try { if (!native.Destroy(window)) ownerErrors.Add(new InvalidOperationException("Occupancy Destroy failed.")); }
                    catch (Exception error) { ownerErrors.Add(error); }
                try { native.Restore(); }
                catch (Exception error) { ownerErrors.Add(error); }
            }
        });
        owner.SetApartmentState(ApartmentState.MTA);
        Exception? primary = null;
        owner.Start();
        try
        {
            bool readyAccepted = readinessWaitForTesting == null ? ready.Wait(readyBudget) : readinessWaitForTesting(ready, readyBudget);
            if (!readyAccepted) throw new TimeoutException("Occupancy ready timed out.");
            if (!accepted) throw new InvalidOperationException("Occupancy readiness was rejected.");
            native.Record("work-start");
            work();
            native.Record("work-ended-before-release");
        }
        catch (Exception error) { primary = error; }
        finally { release.Set(); owner.Join(); }
        try { native.Record("owner-joined"); }
        catch (Exception error) { ownerErrors.Add(error); }
        if (primary != null) ownerErrors.Insert(0, primary);
        if (ownerErrors.Count != 0) throw new AggregateException(ownerErrors);
    }
}
