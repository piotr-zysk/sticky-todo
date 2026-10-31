using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Google.Apis.Auth.OAuth2.Responses;
using StickyTodo;

static partial class Program
{
    const string DummyCredentials = "{\"installed\":{\"client_id\":\"test.apps.googleusercontent.com\",\"client_secret\":\"dummy\"}}";

    static GoogleConnection CachedTestConnection(string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "client.json"), DummyCredentials);
        File.WriteAllText(Path.Combine(directory, "connection.json"), JsonSerializer.Serialize(new GoogleConnection.ConnectionState
        {
            AccountId = "test-account", Email = "test@example.com",
            LastSync = new() { ["test-account"] = Baseline, ["other-account"] = Baseline.AddDays(-1) }
        }));
        new ProtectedTokenStore(Path.Combine(directory, "tokens")).StoreAsync("google", new TokenResponse
        {
            AccessToken = "dummy-access", RefreshToken = "dummy-refresh", ExpiresInSeconds = 3600
        }).GetAwaiter().GetResult();
        return new GoogleConnection(directory);
    }

    static void CheckGoogleConnectionAndClose() => WithCleanStorage(() =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "StickyTodo-connection-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var connection = CachedTestConnection(directory);
            Check(connection.IsSignedIn && connection.LastSync == Baseline, "cached sign-in and account sync time restore without network access");
            var settings = new SettingsWindow(new(), _ => { }, connection);
            Check(Find<Button>(settings, "GoogleSignOutButton").Visibility == Visibility.Visible &&
                  !Find<Button>(settings, "ImportGoogleButton").IsEnabled, "settings displays the connected account and sign-out action");
            settings.Close();
            Check(connection.IsSignedIn, "closing appearance settings does not undo authentication");
            using (var locked = new FileStream(Path.Combine(directory, "connection.json.tmp"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                ExpectStorageFailure(connection.RecordSuccess);
            Check(connection.LastSync == Baseline, "failed success-time persistence preserves previous time");
            connection.RecordSuccess();
            Check(new GoogleConnection(directory).LastSync == connection.LastSync && connection.LastSync > Baseline, "last successful sync persists across restarts");
            var state = JsonSerializer.Deserialize<GoogleConnection.ConnectionState>(File.ReadAllText(Path.Combine(directory, "connection.json")))!;
            Check(state.LastSync["other-account"] == Baseline.AddDays(-1), "success time is tracked per account");
            connection.SignOutAsync().GetAwaiter().GetResult();
            Check(!connection.IsSignedIn && !new GoogleConnection(directory).IsSignedIn, "sign-out persists and removes cached authorization");
            var invalid = Path.Combine(directory, "invalid.json");
            File.WriteAllText(invalid, "{\"web\":{}}");
            ExpectFailure<InvalidDataException>(() => connection.ImportCredentials(invalid), "web credentials cannot be imported");
            File.WriteAllText(Path.Combine(directory, "connection.json"), "invalid");
            var broken = new GoogleConnection(directory);
            Check(!broken.IsSignedIn && broken.ConnectionError != null, "broken account cache does not prevent local startup");
            CachedTestConnection(directory);
            File.WriteAllText(Path.Combine(directory, "client.json"), "{\"web\":{}}");
            broken = new GoogleConnection(directory);
            Check(!broken.IsSignedIn && broken.ConnectionError != null, "invalid cached OAuth configuration does not prevent local startup");

            foreach (var choice in new[] { SyncFailureChoice.Cancel, SyncFailureChoice.Retry, SyncFailureChoice.CloseAnyway })
            {
                connection = CachedTestConnection(directory);
                var task = new TaskItem { Title = "Close test" };
                Storage.SaveTasks(new[] { task });
                var fake = new FakeDrive(Snapshot()) { FailRead = true };
                var main = new MainWindow(google: connection, driveTransportFactory: () => fake);
                bool closed = false;
                main.Closed += (_, _) => closed = true;
                main.Show();
                Check(fake.Reads == 0, "startup does not sync automatically");
                Check(Find<Button>(main, "SyncButton").Visibility == Visibility.Visible &&
                      Find<Button>(main, "SyncButton").ToolTip.ToString()!.Contains("Last synced"), "signed-in header shows sync time");
                _ = main.Dispatcher.BeginInvoke(new Action(() =>
                {
                    var dialog = Application.Current.Windows.OfType<SyncFailureWindow>().Single(w => w.IsVisible);
                    if (choice == SyncFailureChoice.Cancel) dialog.Close();
                    else
                    {
                        if (choice == SyncFailureChoice.Retry) fake.FailRead = false;
                        Find<Button>(dialog, choice == SyncFailureChoice.Retry ? "RetryButton" : "CloseAnywayButton")
                            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                }));
                main.Close();
                PumpDispatcher(TimeSpan.FromMilliseconds(30));
                Check(closed == (choice != SyncFailureChoice.Cancel), $"close-time failure honors {choice}: closed={closed}, reads={fake.Reads}, writes={fake.Writes}, visible={main.IsVisible}, lastSync={connection.LastSync:O}");
                Check(fake.Reads == (choice == SyncFailureChoice.Retry ? 2 : 1), "close-time retry count matches " + choice);
                Check((connection.LastSync > Baseline) == (choice == SyncFailureChoice.Retry), "only successful close sync updates its timestamp");
                if (!closed)
                {
                    fake.FailRead = false;
                    main.Close();
                    PumpDispatcher(TimeSpan.FromMilliseconds(30));
                    Check(closed, "cancelled close can be retried later");
                }
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    });
}
