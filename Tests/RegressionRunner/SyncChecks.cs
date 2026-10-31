using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Drive.v3;
using Google.Apis.Http;
using Google.Apis.Services;
using StickyTodo;

static partial class Program
{
    static readonly DateTimeOffset Baseline = new(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);

    static SyncTask Record(Guid id, string title, int seconds = 0, bool completed = false) => new()
    {
        Id = id, Title = title, UpdatedAt = Baseline.AddSeconds(seconds),
        CompletedAt = completed ? Baseline.UtcDateTime.AddSeconds(seconds) : null,
        Details = completed ? "" : title + " details"
    };

    static SyncSnapshot Snapshot(params SyncTask[] tasks) => new() { Tasks = tasks.ToList() };

    static void ExpectFailure<T>(Action action, string description) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected failure: " + description);
    }

    static void ExpectStorageFailure(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        throw new InvalidOperationException("Expected a storage failure.");
    }

    static void WithCleanStorage(Action action)
    {
        Storage.InitializeTaskStorage();
        var directory = Storage.TasksDirectory;
        var previous = Directory.GetFiles(directory).ToDictionary(path => Path.GetFileName(path)!, File.ReadAllBytes);
        try
        {
            foreach (var path in Directory.GetFiles(directory)) File.Delete(path);
            Storage.SaveTasks(Array.Empty<TaskItem>());
            action();
        }
        finally
        {
            foreach (var path in Directory.GetFiles(directory)) File.Delete(path);
            foreach (var pair in previous) File.WriteAllBytes(Path.Combine(directory, pair.Key!), pair.Value);
        }
    }

    static void CheckSyncTimestampsAndStorage() => WithCleanStorage(() =>
    {
        var task = new TaskItem { Title = "Original", UpdatedAt = Baseline };
        task.Title = task.Title;
        task.IsHigh = false;
        task.IsDone = false;
        task.SetDue(null, null);
        task.IsEditingTitle = true;
        Check(task.UpdatedAt == Baseline, "unchanged values and editor state do not update timestamps");
        task.Title = "Renamed";
        Check(task.UpdatedAt > Baseline, "title edits update the timestamp");
        var before = task.UpdatedAt;
        task.SetDue(new DateTime(2026, 10, 31), TimeSpan.FromHours(9));
        Check(task.UpdatedAt > before, "due edits update the timestamp");
        before = task.UpdatedAt;
        task.IsHigh = true;
        Check(task.UpdatedAt > before, "priority edits update the timestamp");
        before = task.UpdatedAt;
        task.IsDone = true;
        Check(task.UpdatedAt > before, "completion updates the timestamp");
        task.IsDone = false;
        Storage.SaveTasks(new[] { task });
        before = task.UpdatedAt;
        Check(Storage.LoadTasks().Single().UpdatedAt == before, $"YAML timestamps round-trip without advancing on load: expected {before:O}, actual {Storage.LoadTasks().Single().UpdatedAt:O}; {File.ReadAllText(Storage.TasksPath)}");
        var now = Baseline;
        var session = new DetailsSaveSession("", text => Storage.SaveTaskDetails(task, text, new[] { task }), () => now);
        session.Edit("First");
        session.Save();
        var firstSave = task.UpdatedAt;
        Check(firstSave > before && Storage.LoadTasks().Single().UpdatedAt == firstSave, "details and timestamp save together");
        session.Edit("Pending");
        now = now.AddSeconds(59);
        session.Save();
        Check(task.UpdatedAt == firstSave && Storage.LoadDetails(task.Id) == "First", "throttled details do not advance UpdatedAt");
        now = now.AddSeconds(1);
        session.Save();
        Check(task.UpdatedAt > firstSave && Storage.LoadDetails(task.Id) == "Pending", "minute-boundary save advances UpdatedAt");
        session.Edit("Forced");
        session.Save(force: true);
        Check(Storage.LoadDetails(task.Id) == "Forced", "forced sync flush bypasses throttle");
        before = task.UpdatedAt;
        using (var locked = new FileStream(Storage.TasksPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            ExpectFailure<IOException>(() => Storage.SaveTaskDetails(task, "Must not leak", new[] { task }), "locked metadata");
        Check(Storage.LoadDetails(task.Id) == "Forced" && task.UpdatedAt == before, "failed details transaction changes neither content nor timestamp");

        // Failure after the first file replacement must undo the replacement.
        var incoming = Record(task.Id, "Remote", 2);
        using (var locked = new FileStream(Storage.DetailsPath(task.Id), FileMode.Open, FileAccess.Read, FileShare.Read))
            ExpectStorageFailure(() => Storage.ApplySnapshot(Snapshot(incoming)));
        Storage.InitializeTaskStorage();
        Check(Storage.LoadTasks().Single().Title == task.Title && Storage.LoadDetails(task.Id) == "Forced", "multi-file failure rolls back metadata and details");

        // Simulate a process interruption with an outstanding undo journal.
        var oldYaml = File.ReadAllText(Storage.TasksPath);
        File.WriteAllText(Path.Combine(Storage.TasksDirectory, ".transaction.json"),
            JsonSerializer.Serialize(new Dictionary<string, string?> { ["tasks.yaml"] = oldYaml }));
        File.WriteAllText(Storage.TasksPath, "[]");
        Storage.InitializeTaskStorage();
        Check(Storage.LoadTasks().Single().Id == task.Id, "startup recovers an interrupted task transaction");

        task.IsDone = true;
        Storage.CompleteTask(task, Array.Empty<TaskItem>());
        var completed = Storage.LoadSnapshot().Tasks.Single();
        Check(completed.CompletedAt.HasValue && completed.Details == "" && !File.Exists(Storage.DetailsPath(task.Id)), "completion archives metadata and removes details");

        File.WriteAllText(Storage.TasksPath, "- title: Legacy\n");
        File.WriteAllText(Storage.ArchivePath, "- title: Old completed\n  completedAt: 2024-01-01T12:00:00Z\n");
        File.SetLastWriteTimeUtc(Storage.TasksPath, Baseline.UtcDateTime);
        File.SetLastWriteTimeUtc(Storage.ArchivePath, Baseline.AddDays(-1).UtcDateTime);
        var migrated = Storage.LoadTasks().Single();
        Check(migrated.Id != Guid.Empty && migrated.UpdatedAt == Baseline, "legacy tasks get stable IDs and file-derived timestamps");
        Check(Storage.LoadTasks().Single().UpdatedAt == Baseline, "migration does not restamp tasks on subsequent loads");
        var archived = Storage.LoadSnapshot().Tasks.Single(t => t.CompletedAt.HasValue);
        Check(archived.Id != Guid.Empty && archived.UpdatedAt == Baseline.AddDays(-1), "legacy archives also migrate");
    });

    static void CheckTaskMerging()
    {
        var id = Guid.NewGuid();
        var localOnly = Record(Guid.NewGuid(), "Local only");
        var remoteOnly = Record(Guid.NewGuid(), "Remote only");
        var newerLocal = Record(id, "Local", 2);
        var olderRemote = Record(id, "Remote", 1);
        var merged = TaskMerge.Merge(Snapshot(localOnly, newerLocal), Snapshot(olderRemote, remoteOnly));
        Check(merged.Tasks.Select(t => t.Id).SequenceEqual(new[] { localOnly.Id, id, remoteOnly.Id }), "merge unions IDs and keeps local order");
        Check(ReferenceEquals(merged.Tasks[1], newerLocal), "newer local record replaces entire remote record");
        var newerRemote = Record(id, "Remote", 3);
        newerRemote.Priority = "high";
        newerRemote.Due = "2027-01-02 09:15";
        Check(ReferenceEquals(TaskMerge.Merge(Snapshot(newerLocal), Snapshot(newerRemote)).Tasks.Single(), newerRemote), "newer remote record replaces all local fields");
        newerLocal.UpdatedAt = newerRemote.UpdatedAt;
        Check(TaskMerge.Merge(Snapshot(newerLocal), Snapshot(newerRemote)).Tasks.Single().Title == "Remote", "Google wins timestamp ties");
        var archive = Record(id, "Completed", 4, completed: true);
        Check(TaskMerge.Merge(Snapshot(newerLocal), Snapshot(archive)).Tasks.Single().CompletedAt.HasValue, "remote completion supersedes older active task");
        Check(TaskMerge.Merge(Snapshot(archive), Snapshot(newerRemote)).Tasks.Single().CompletedAt.HasValue, "local completion is not resurrected by older Google task");
        Check(!TaskMerge.Merge(Snapshot(archive), Snapshot(Record(id, "Newer active", 5))).Tasks.Single().CompletedAt.HasValue, "newer active version replaces older archive");
        ExpectFailure<InvalidDataException>(() => Snapshot(newerLocal, newerLocal).Validate(), "duplicate IDs");
        ExpectFailure<InvalidDataException>(() => new SyncSnapshot { Version = 2 }.Validate(), "unsupported version");
        ExpectFailure<JsonException>(() => JsonSerializer.Deserialize<SyncSnapshot>("{}"), "missing required snapshot fields");
        var invalid = Record(id, "Invalid");
        invalid.Due = "tomorrow";
        ExpectFailure<InvalidDataException>(() => Snapshot(invalid).Validate(), "invalid due date");
    }

    sealed class FakeDrive(SyncSnapshot remote) : IDriveTransport
    {
        public SyncSnapshot Remote = remote;
        public int Reads, Writes, Conflicts;
        public bool FailRead, FailWrite;
        public Action? OnRead;
        public TaskCompletionSource? Hold;
        public async Task<RemoteSnapshot> ReadAsync(CancellationToken cancellationToken)
        {
            Reads++;
            OnRead?.Invoke();
            if (Hold != null) await Hold.Task.WaitAsync(cancellationToken);
            if (FailRead) throw new IOException("Offline");
            return new RemoteSnapshot(Remote, "fake", "\"version\"");
        }
        public Task<bool> WriteAsync(RemoteSnapshot original, SyncSnapshot merged, CancellationToken cancellationToken)
        {
            Writes++;
            if (FailWrite) throw new IOException("Upload failed");
            if (Conflicts-- > 0) return Task.FromResult(false);
            Remote = merged;
            return Task.FromResult(true);
        }
    }

    static void CheckSyncEngine()
    {
        var local = Snapshot(Record(Guid.NewGuid(), "Local"));
        var drive = new FakeDrive(Snapshot(Record(Guid.NewGuid(), "Cloud"))) { Conflicts = 1 };
        var merged = SyncEngine.MergeAndUploadAsync(local, drive, CancellationToken.None).GetAwaiter().GetResult();
        Check(drive.Reads == 2 && drive.Writes == 2 && merged.Tasks.Count == 2, "conditional write conflict refetches and retries");
        drive = new FakeDrive(Snapshot()) { Conflicts = 10 };
        ExpectFailure<IOException>(() => SyncEngine.MergeAndUploadAsync(local, drive, CancellationToken.None).GetAwaiter().GetResult(), "bounded conflict retries");
        Check(drive.Writes == 3, "conflict retries are bounded");
        drive = new FakeDrive(Snapshot()) { FailRead = true };
        ExpectFailure<IOException>(() => SyncEngine.MergeAndUploadAsync(local, drive, CancellationToken.None).GetAwaiter().GetResult(), "offline download");
        Check(drive.Writes == 0, "failed download never uploads");
        drive = new FakeDrive(Snapshot()) { FailWrite = true };
        ExpectFailure<IOException>(() => SyncEngine.MergeAndUploadAsync(local, drive, CancellationToken.None).GetAwaiter().GetResult(), "failed upload");
        drive = new FakeDrive(new SyncSnapshot { Version = 2 });
        ExpectFailure<InvalidDataException>(() => SyncEngine.MergeAndUploadAsync(local, drive, CancellationToken.None).GetAwaiter().GetResult(), "bad remote snapshot");
        Check(drive.Writes == 0, "invalid Google data never uploads");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        ExpectFailure<OperationCanceledException>(() => SyncEngine.MergeAndUploadAsync(local, drive, cancellation.Token).GetAwaiter().GetResult(), "cancelled sync");
    }

    static T Invoke<T>(object instance, string method, params object?[] args) =>
        (T)instance.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(instance, args)!;

    static void FinishOnDispatcher(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!task.IsCompleted && DateTime.UtcNow < deadline) PumpDispatcher(TimeSpan.FromMilliseconds(10));
        Check(task.IsCompleted, "asynchronous regression check finishes");
        task.GetAwaiter().GetResult();
    }

    static void CheckSyncWindows() => WithCleanStorage(() =>
    {
        var task = new TaskItem { Title = "Editing" };
        Storage.SaveTasks(new[] { task });
        var main = new MainWindow(enableGoogle: false);
        try
        {
            main.Show();
            var tasks = (ObservableCollection<TaskItem>)typeof(MainWindow).GetField("_tasks", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
            task = tasks.Single();
            Invoke<object?>(main, "OpenDetails", task);
            var notes = (Dictionary<Guid, DetailsWindow>)typeof(MainWindow).GetField("_detailsWindows", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
            var note = notes[task.Id];
            Find<TextBox>(note, "Editor").Text = "First";
            Find<TextBox>(note, "Editor").Text = "Pending text";
            task.EditTitle = "Committed title";
            task.IsEditingTitle = true;
            Invoke<object?>(main, "PrepareLocalForSync");
            var drive = new FakeDrive(Snapshot()) { Hold = new TaskCompletionSource() };
            drive.OnRead = () => Check(Storage.LoadDetails(task.Id) == "Pending text" && Storage.LoadTasks().Single().Title == "Committed title", "pending text and title committed before Google read");
            var sync = Invoke<Task>(main, "SynchronizeAsync", drive);
            Check(!note.IsEnabled && note.IsSyncing && !Find<DockPanel>(main, "RootDock").IsEnabled, "editors disabled during sync");
            var overlap = Invoke<Task>(main, "SynchronizeAsync", drive);
            ExpectFailure<InvalidOperationException>(() => overlap.GetAwaiter().GetResult(), "overlapping sync");
            drive.Hold.SetResult();
            FinishOnDispatcher(sync);
            drive.OnRead = null;
            Check(note.IsEnabled && !note.IsSyncing, "editors restored after sync");
            drive.Hold = null;
            var incoming = Record(task.Id, "Incoming title");
            incoming.UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(1);
            incoming.Details = "Incoming details";
            drive.Remote = Snapshot(incoming);
            FinishOnDispatcher(Invoke<Task>(main, "SynchronizeAsync", drive));
            Check(note.Title == "Incoming title" && Find<TextBox>(note, "Editor").Text == "Incoming details", "incoming task refreshes open note");
            Check(Storage.LoadTasks().Single().UpdatedAt == incoming.UpdatedAt, "UI refresh preserves remote timestamp");
            Find<TextBox>(note, "Editor").Text = "Edit after sync";
            Invoke<object?>(main, "PrepareLocalForSync");
            Check(Storage.LoadDetails(task.Id) == "Edit after sync", "existing details session saves to refreshed task instance");
            var before = File.ReadAllText(Storage.TasksPath);
            drive.FailWrite = true;
            var failure = Invoke<Task>(main, "SynchronizeAsync", drive);
            ExpectFailure<IOException>(() => FinishOnDispatcher(failure), "upload failure");
            Check(File.ReadAllText(Storage.TasksPath) == before && note.IsEnabled, "failed sync preserves local data and restores controls");
            drive.FailWrite = false;
            incoming.CompletedAt = DateTime.UtcNow;
            incoming.Details = "";
            incoming.UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(2);
            FinishOnDispatcher(Invoke<Task>(main, "SynchronizeAsync", drive));
            Check(notes.Count == 0 && tasks.Count == 0 && !File.Exists(Storage.DetailsPath(task.Id)), "remote completion closes note and removes details");
            var defaultDialog = new SyncFailureWindow("Offline");
            Check(defaultDialog.Choice == SyncFailureChoice.Cancel, "closing failure dialog defaults to keeping app open");
            defaultDialog.Close();
        }
        finally { main.Close(); PumpDispatcher(TimeSpan.FromMilliseconds(20)); }
    });

    static void CheckProtectedTokens()
    {
        var directory = Path.Combine(Path.GetTempPath(), "StickyTodo-token-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ProtectedTokenStore(directory);
            store.StoreAsync("test", new TokenResponse { AccessToken = "secret-access", RefreshToken = "secret-refresh" }).GetAwaiter().GetResult();
            Check(!Encoding.UTF8.GetString(File.ReadAllBytes(Directory.GetFiles(directory).Single())).Contains("secret-refresh"), "OAuth tokens are encrypted on disk");
            Check(store.GetAsync<TokenResponse>("test").Result.RefreshToken == "secret-refresh", "DPAPI tokens round-trip");
            store.ClearAsync().GetAwaiter().GetResult();
            Check(store.GetAsync<TokenResponse>("test").Result == null, "sign-out removes cached tokens");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    sealed class TestHttpFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public ConfigurableHttpClient CreateHttpClient(CreateHttpClientArgs args) => new(new ConfigurableMessageHandler(handler));
    }

    sealed class DriveHttpHandler : HttpMessageHandler
    {
        public bool MissingFolder, MissingFile, Conflict, Denied, InvalidJson, DuplicateFolder;
        public int FoldersCreated, FilesCreated;
        public string? ConditionalHeader;
        public SyncSnapshot Snapshot = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var query = Uri.UnescapeDataString(uri.Query);
            if (Denied) return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            if (request.Method == HttpMethod.Get && query.Contains("q="))
            {
                bool folder = query.Contains("application/vnd.google-apps.folder");
                bool missing = folder ? MissingFolder : MissingFile;
                return JsonResponse(missing ? "{\"files\":[]}" : folder && DuplicateFolder
                    ? "{\"files\":[{\"id\":\"folder\"},{\"id\":\"second\"}]}"
                    : "{\"files\":[{\"id\":\"" + (folder ? "folder" : "file") + "\"}]}");
            }
            if (request.Method == HttpMethod.Post)
            {
                if (uri.AbsolutePath.Contains("upload"))
                {
                    var session = JsonResponse("{}");
                    session.Headers.Location = new Uri("https://www.googleapis.com/upload/drive/v3/files?upload_id=test-session");
                    return session;
                }
                MissingFolder = false;
                FoldersCreated++;
                return JsonResponse("{\"id\":\"folder\"}");
            }
            if (request.Method == HttpMethod.Put && query.Contains("upload_id="))
            {
                MissingFile = false;
                FilesCreated++;
                return JsonResponse("{\"id\":\"file\"}");
            }
            if (request.Method == HttpMethod.Get && query.Contains("alt=media"))
                return JsonResponse(InvalidJson ? "{}" : JsonSerializer.Serialize(Snapshot, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            if (request.Method == HttpMethod.Get)
            {
                Check(uri.AbsolutePath.Contains("/drive/v2/"), "conditional metadata uses Drive v2, which exposes ETags");
                return JsonResponse("{\"id\":\"file\",\"etag\":\"\\\"etag-123\\\"\"}");
            }
            if (request.Method == HttpMethod.Put)
            {
                Check(uri.AbsolutePath.Contains("/drive/v2/"), "conditional update uses the same Drive API version as its ETag");
                ConditionalHeader = request.Headers.IfMatch.ToString();
                if (Conflict) return new HttpResponseMessage(HttpStatusCode.PreconditionFailed);
                Snapshot = JsonSerializer.Deserialize<SyncSnapshot>(await request.Content!.ReadAsStringAsync(cancellationToken), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!;
                return JsonResponse("{\"id\":\"file\"}");
            }
            throw new InvalidOperationException("Unexpected Google request: " + request.Method + " " + uri);
        }
        static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }

    static void CheckDriveTransport()
    {
        var handler = new DriveHttpHandler { MissingFolder = true, MissingFile = true };
        using var service = new DriveService(new BaseClientService.Initializer
        {
            HttpClientFactory = new TestHttpFactory(handler), ApplicationName = "StickyTodo-tests"
        });
        var transport = new GoogleDriveTransport(service);
        var remote = transport.ReadAsync(CancellationToken.None).GetAwaiter().GetResult();
        Check(handler.FoldersCreated == 1 && handler.FilesCreated == 1, "Drive transport creates missing folder and snapshot");
        var local = Snapshot(Record(Guid.NewGuid(), "Upload"));
        Check(transport.WriteAsync(remote, local, CancellationToken.None).Result && handler.ConditionalHeader == "\"etag-123\"", "Drive upload sends If-Match");
        handler.Conflict = true;
        Check(!transport.WriteAsync(remote, local, CancellationToken.None).Result, "Drive 412 becomes retryable conflict");
        handler.Denied = true;
        ExpectFailure<IOException>(() => transport.WriteAsync(remote, local, CancellationToken.None).GetAwaiter().GetResult(), "expired/denied Google access");
        handler.Denied = false;
        handler.InvalidJson = true;
        ExpectFailure<JsonException>(() => transport.ReadAsync(CancellationToken.None).GetAwaiter().GetResult(), "malformed Drive snapshot");
        handler.InvalidJson = false;
        handler.DuplicateFolder = true;
        ExpectFailure<IOException>(() => transport.ReadAsync(CancellationToken.None).GetAwaiter().GetResult(), "ambiguous Drive folders");
    }
}
