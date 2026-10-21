using System.Reflection;
using System.Windows.Controls;
using System.Windows.Threading;
using SqlBackup.Core;
using SqlBackup.Desktop;

namespace SqlBackup.Tests;

public static class DialogResponsivenessChecks
{
    public static void Run()
    {
        var inspected = new ServerInspection("17.fixture", "Fixture", true, "Fixture",
            [new DatabaseInfo("Fixture", "ONLINE", "SIMPLE", true, "Ready")]);
        bool requested = false;
        bool cancelled = false;
        var reply = new TaskCompletionSource<ServerInspection>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialog = new DatabaseSelectionDialog("fictional-server", ["Fixture"], async token =>
        {
            requested = true;
            using var registration = token.Register(() => cancelled = true);
            return await reply.Task.WaitAsync(token);
        });
        Check(!requested && !((Button)dialog.FindName("AcceptButton")).IsEnabled,
            "Database window can be constructed immediately without waiting for SQL");
        var refresh = dialog.RefreshAsync();
        DispatcherChecks.Until(() => requested);
        bool heartbeat = false;
        Dispatcher.CurrentDispatcher.BeginInvoke(() => heartbeat = true);
        DispatcherChecks.Until(() => heartbeat);
        Check(!refresh.IsCompleted && !dialog.TryApplySelection(),
            "Dispatcher remains responsive while SQL is delayed and uninspected selections cannot be applied");
        dialog.Close();
        DispatcherChecks.Until(() => refresh.IsCompleted);
        Check(cancelled && dialog.LatestInspection is null && dialog.SelectedDatabases.SequenceEqual(["Fixture"]),
            "Closing database selection cancels loading without changing the original selection");
        reply.TrySetResult(inspected);
        Check(dialog.LatestInspection is null, "A late SQL response cannot modify a closed database dialog");

        int refreshes = 0;
        var cached = new DatabaseSelectionDialog("fictional-server", ["Fixture"], _ =>
        {
            refreshes++;
            return Task.FromResult(inspected);
        }, inspected);
        Check(cached.TryApplySelection() && refreshes == 0,
            "A recent same-connection inspection allows immediate selection without another SQL request");
        cached.Items[0].IsSelected = false;
        var cachedRefresh = cached.RefreshAsync();
        DispatcherChecks.Until(() => cachedRefresh.IsCompleted);
        Check(refreshes == 1 && !cached.Items[0].IsSelected,
            "Explicit database refresh preserves unsaved checkbox edits");
        cached.Close();
        CheckForegroundPolling();
    }

    private static void CheckForegroundPolling()
    {
        bool requested = false;
        var pending = new TaskCompletionSource<ServiceResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = new MainWindow(async (_, token) =>
        {
            requested = true;
            return await pending.Task.WaitAsync(token);
        });
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(MainWindow).GetField("connected", flags)!.SetValue(window, true);
        typeof(MainWindow).GetMethod("HistoryTimer_Tick", flags)!.Invoke(window, [null, EventArgs.Empty]);
        DispatcherChecks.Until(() => requested);
        Check(!(bool)typeof(MainWindow).GetField("busy", flags)!.GetValue(window)!,
            "Automatic polling does not make foreground controls busy or drop user actions");
        typeof(MainWindow).GetMethod("PausePolling", flags)!.Invoke(window, null);
        DispatcherChecks.Until(() => !(bool)typeof(MainWindow).GetField("polling", flags)!.GetValue(window)!);
        Check((bool)typeof(MainWindow).GetField("connected", flags)!.GetValue(window)!,
            "Foreground cancellation of polling is not misreported as a service outage");
        window.Close();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }

        Console.WriteLine("PASS: " + message);
    }
}
