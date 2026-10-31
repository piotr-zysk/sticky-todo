using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Google.Apis.Util.Store;

namespace StickyTodo;

public sealed class GoogleConnection
{
    static readonly string DefaultDirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StickyTodo", "Google");
    readonly string _directoryPath, _credentialsPath, _statePath;
    static readonly string[] Scopes = { DriveService.Scope.DriveFile };
    readonly ProtectedTokenStore _tokens;
    ConnectionState _state;
    UserCredential? _credential;

    public event Action? Changed;
    public bool HasConfiguration => File.Exists(_credentialsPath);
    public string? ConnectionError { get; private set; }
    public bool IsSignedIn => _credential != null;
    public string Account => _state.Email ?? "Google account";
    public DateTimeOffset? LastSync => _state.AccountId != null && _state.LastSync.TryGetValue(_state.AccountId, out var value) ? value : null;

    public GoogleConnection(string? directoryPath = null)
    {
        _directoryPath = directoryPath ?? DefaultDirectoryPath;
        _credentialsPath = Path.Combine(_directoryPath, "client.json");
        _statePath = Path.Combine(_directoryPath, "connection.json");
        _tokens = new ProtectedTokenStore(Path.Combine(_directoryPath, "tokens"));
        _state = new();
        try
        {
            _state = File.Exists(_statePath)
                ? JsonSerializer.Deserialize<ConnectionState>(File.ReadAllText(_statePath)) ?? new() : new();
            _state.LastSync ??= new();
            if (_state.AccountId == null || !HasConfiguration) return;
            var token = _tokens.GetAsync<TokenResponse>("google").GetAwaiter().GetResult();
            if (token == null) return;
            var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
            {
                ClientSecrets = ReadSecrets(_credentialsPath), Scopes = Scopes, DataStore = _tokens
            });
            _credential = new UserCredential(flow, "google", token);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or CryptographicException or JsonException or Newtonsoft.Json.JsonException)
        {
            // A broken/obsolete connection must not prevent access to locally saved tasks.
            _credential = null;
            ConnectionError = "Google connection could not be restored. Import credentials or sign in again. " + ex.Message;
        }
    }

    static ClientSecrets ReadSecrets(string path)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        if (json.RootElement.ValueKind != JsonValueKind.Object ||
            !json.RootElement.TryGetProperty("installed", out var installed) || installed.ValueKind != JsonValueKind.Object ||
            !installed.TryGetProperty("client_id", out var id) || id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString()) ||
            !installed.TryGetProperty("client_secret", out var secret) || secret.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(secret.GetString()))
            throw new InvalidDataException("Choose the credentials JSON for a Google OAuth Desktop app.");
        return new ClientSecrets { ClientId = id.GetString(), ClientSecret = secret.GetString() };
    }

    public void ImportCredentials(string path)
    {
        ReadSecrets(path);
        if (IsSignedIn) throw new InvalidOperationException("Sign out before changing the Google client configuration.");
        Directory.CreateDirectory(_directoryPath);
        WriteAtomic(_credentialsPath, File.ReadAllText(path));
        _tokens.ClearAsync().GetAwaiter().GetResult();
        ConnectionError = null;
        Changed?.Invoke();
    }

    public async Task SignInAsync(CancellationToken cancellationToken)
    {
        if (!HasConfiguration) throw new InvalidOperationException("Import Google Desktop app credentials first.");
        if (ConnectionError != null) await _tokens.ClearAsync();
        var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(ReadSecrets(_credentialsPath), Scopes,
            "google", cancellationToken, _tokens);
        try
        {
            using var drive = CreateService(credential);
            var request = drive.About.Get();
            request.Fields = "user(emailAddress,permissionId)";
            var about = await request.ExecuteAsync(cancellationToken);
            if (string.IsNullOrEmpty(about.User?.PermissionId)) throw new IOException("Google did not return the connected account.");
            _state.AccountId = about.User.PermissionId;
            _state.Email = about.User.EmailAddress;
            SaveState();
            _credential = credential;
            ConnectionError = null;
            Changed?.Invoke();
        }
        catch
        {
            await _tokens.ClearAsync();
            throw;
        }
    }

    public async Task SignOutAsync()
    {
        await _tokens.ClearAsync();
        _state.AccountId = null;
        _state.Email = null;
        _credential = null;
        try { SaveState(); }
        finally { Changed?.Invoke(); }
    }

    public DriveService CreateService() => CreateService(_credential ?? throw new InvalidOperationException("Sign in to Google first."));
    static DriveService CreateService(UserCredential credential) => new(new BaseClientService.Initializer
    {
        HttpClientInitializer = credential, ApplicationName = "StickyTodo"
    });

    public void RecordSuccess()
    {
        if (_state.AccountId == null) throw new InvalidOperationException("No Google account is connected.");
        var previous = LastSync;
        _state.LastSync[_state.AccountId] = DateTimeOffset.UtcNow;
        try { SaveState(); }
        catch
        {
            if (previous.HasValue) _state.LastSync[_state.AccountId] = previous.Value;
            else _state.LastSync.Remove(_state.AccountId);
            throw;
        }
        Changed?.Invoke();
    }

    void SaveState()
    {
        Directory.CreateDirectory(_directoryPath);
        WriteAtomic(_statePath, JsonSerializer.Serialize(_state));
    }

    static void WriteAtomic(string path, string contents)
    {
        File.WriteAllText(path + ".tmp", contents, new UTF8Encoding(false));
        File.Move(path + ".tmp", path, true);
    }

    public sealed class ConnectionState
    {
        public string? AccountId { get; set; }
        public string? Email { get; set; }
        public Dictionary<string, DateTimeOffset> LastSync { get; set; } = new();
    }
}

// Google refresh/access tokens never live in settings.yaml or the portable tasks folder.
public sealed class ProtectedTokenStore(string directory) : IDataStore
{
    string FilePath<T>(string key) => Path.Combine(directory,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(typeof(T).FullName + ":" + key))) + ".bin");

    public Task StoreAsync<T>(string key, T value)
    {
        Directory.CreateDirectory(directory);
        var bytes = Encoding.UTF8.GetBytes(Google.Apis.Json.NewtonsoftJsonSerializer.Instance.Serialize(value));
        var path = FilePath<T>(key);
        File.WriteAllBytes(path + ".tmp", ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser));
        File.Move(path + ".tmp", path, true);
        return Task.CompletedTask;
    }

    public Task<T> GetAsync<T>(string key)
    {
        var path = FilePath<T>(key);
        if (!File.Exists(path)) return Task.FromResult(default(T)!);
        var bytes = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
        return Task.FromResult(Google.Apis.Json.NewtonsoftJsonSerializer.Instance.Deserialize<T>(Encoding.UTF8.GetString(bytes)));
    }

    public Task DeleteAsync<T>(string key)
    {
        File.Delete(FilePath<T>(key));
        return Task.CompletedTask;
    }

    public Task ClearAsync()
    {
        if (Directory.Exists(directory))
            foreach (var path in Directory.EnumerateFiles(directory, "*.bin")) File.Delete(path);
        return Task.CompletedTask;
    }
}
