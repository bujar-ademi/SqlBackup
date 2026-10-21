using System.Windows.Threading;

namespace SqlBackup.Tests;

public static class DispatcherChecks
{
    public static void Until(Func<bool> completed)
    {
        var frame = new DispatcherFrame();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(2) };
        timer.Tick += (_, _) =>
        {
            if (completed() || clock.Elapsed > TimeSpan.FromSeconds(5))
            {
                frame.Continue = false;
            }
        };
        timer.Start();
        try
        {
            Dispatcher.PushFrame(frame);
        }
        finally
        {
            timer.Stop();
        }

        if (!completed())
        {
            throw new TimeoutException("The dispatcher responsiveness fixture did not complete.");
        }
    }
}
