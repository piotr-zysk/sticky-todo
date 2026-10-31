using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Google.Apis.Drive.v3;
using Google.Apis.Upload;
using DriveFile = Google.Apis.Drive.v3.Data.File;

namespace StickyTodo;

public sealed class GoogleDriveTransport(DriveService drive) : IDriveTransport
{
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    const string FileName = "sticky-todo.json";

    async Task<string?> FindAsync(string query, CancellationToken cancellationToken)
    {
        var request = drive.Files.List();
        request.Q = query + " and trashed = false";
        request.Fields = "files(id),nextPageToken";
        request.PageSize = 100;
        var files = await request.ExecuteAsync(cancellationToken);
        if (files.Files.Count > 1 || files.NextPageToken != null)
            throw new IOException("More than one StickyTodo sync folder or file was found. Keep a single sync folder and file in Google Drive, then retry.");
        return files.Files.SingleOrDefault()?.Id;
    }

    async Task<string> GetFileIdAsync(CancellationToken cancellationToken)
    {
        var folder = await FindAsync("name = 'StickyTodo' and mimeType = 'application/vnd.google-apps.folder' and 'root' in parents", cancellationToken);
        if (folder == null)
        {
            var created = await drive.Files.Create(new DriveFile
            {
                Name = "StickyTodo", MimeType = "application/vnd.google-apps.folder", Parents = new[] { "root" }
            }).ExecuteAsync(cancellationToken);
            folder = created.Id;
            // Detect competing first-time folder creation instead of silently splitting task sets.
            await FindAsync("name = 'StickyTodo' and mimeType = 'application/vnd.google-apps.folder' and 'root' in parents", cancellationToken);
        }
        var file = await FindAsync($"name = '{FileName}' and '{folder}' in parents", cancellationToken);
        if (file != null) return file;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new SyncSnapshot(), Json)));
        var upload = drive.Files.Create(new DriveFile { Name = FileName, Parents = new[] { folder } }, stream, "application/json");
        var progress = await upload.UploadAsync(cancellationToken);
        if (progress.Status != UploadStatus.Completed) throw progress.Exception ?? new IOException("Could not create the Google sync file.");
        await FindAsync($"name = '{FileName}' and '{folder}' in parents", cancellationToken);
        return upload.ResponseBody.Id;
    }

    public async Task<RemoteSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        var fileId = await GetFileIdAsync(cancellationToken);
        var url = "https://www.googleapis.com/drive/v3/files/" + Uri.EscapeDataString(fileId);
        // Drive v3 removed file ETags. Use v2 for both the metadata token and its conditional update.
        // A media-download ETag is a different value and must not be used for file updates.
        using var metadata = await drive.HttpClient.GetAsync(
            "https://www.googleapis.com/drive/v2/files/" + Uri.EscapeDataString(fileId) + "?fields=id,etag", cancellationToken);
        await EnsureSuccessAsync(metadata, cancellationToken);
        using var metadataJson = JsonDocument.Parse(await metadata.Content.ReadAsStringAsync(cancellationToken));
        var etag = metadataJson.RootElement.TryGetProperty("etag", out var value) ? value.GetString() : null;
        if (string.IsNullOrWhiteSpace(etag))
            throw new IOException("Google Drive did not return a file version; sync stopped to avoid overwriting concurrent changes.");
        using var media = await drive.HttpClient.GetAsync(url + "?alt=media", cancellationToken);
        await EnsureSuccessAsync(media, cancellationToken);
        var snapshot = JsonSerializer.Deserialize<SyncSnapshot>(await media.Content.ReadAsStringAsync(cancellationToken), Json)
            ?? throw new InvalidDataException("The Google sync file is empty.");
        snapshot.Validate();
        return new RemoteSnapshot(snapshot, fileId, etag);
    }

    public async Task<bool> WriteAsync(RemoteSnapshot original, SyncSnapshot merged, CancellationToken cancellationToken)
    {
        merged.Validate();
        using var request = new HttpRequestMessage(HttpMethod.Put,
            "https://www.googleapis.com/upload/drive/v2/files/" + Uri.EscapeDataString(original.FileId) + "?uploadType=media");
        request.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(original.ETag));
        request.Content = new StringContent(JsonSerializer.Serialize(merged, Json), Encoding.UTF8, "application/json");
        using var response = await drive.HttpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.PreconditionFailed) return false;
        await EnsureSuccessAsync(response, cancellationToken);
        return true;
    }

    static Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new IOException("Google denied access. Check Drive permissions or sign out and sign in again in Settings.");
        response.EnsureSuccessStatusCode();
        return Task.CompletedTask;
    }
}
