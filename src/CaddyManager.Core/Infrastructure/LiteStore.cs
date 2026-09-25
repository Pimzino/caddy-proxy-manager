using CaddyManager.Core.Models;
using LiteDB;

namespace CaddyManager.Core.Infrastructure;

public sealed class LiteStore : IStore, IDisposable
{
    private readonly LiteDatabase _db;
    private readonly object _settingsLock = new();

    public event Action<Type>? SettingsChanged;
    public ILiteDatabase Database => _db;

    public LiteStore(AppPaths paths)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(paths.DbFile)!);
        var mapper = new BsonMapper { EnumAsInteger = false };
        _db = new LiteDatabase(new ConnectionString { Filename = paths.DbFile, Connection = ConnectionType.Direct }, mapper);
        _db.UtcDate = true;
        EnsureIndexes();
    }

    private void EnsureIndexes()
    {
        Col<SiteHost>().EnsureIndex(h => h.Kind);
        Col<User>().EnsureIndex(u => u.Email, unique: true);
        Col<AuditEntry>().EnsureIndex(a => a.CreatedAt);
        Col<EventEntry>().EnsureIndex(e => e.CreatedAt);
        Col<ConfigRevision>().EnsureIndex(r => r.CreatedAt);
    }

    public ILiteCollection<T> Col<T>() where T : Entity => _db.GetCollection<T>(typeof(T).Name);

    private sealed class SettingsDoc
    {
        public string Id { get; set; } = "";
        public string Json { get; set; } = "";
    }

    public T GetSettings<T>() where T : class, ISettingsDocument, new()
    {
        lock (_settingsLock)
        {
            var doc = _db.GetCollection<SettingsDoc>("settings").FindById(typeof(T).Name);
            if (doc is null) return new T();
            return System.Text.Json.JsonSerializer.Deserialize<T>(doc.Json, JsonDefaults.Storage) ?? new T();
        }
    }

    public void SaveSettings<T>(T settings) where T : class, ISettingsDocument, new()
    {
        lock (_settingsLock)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(settings, JsonDefaults.Storage);
            _db.GetCollection<SettingsDoc>("settings").Upsert(new SettingsDoc { Id = typeof(T).Name, Json = json });
        }
        SettingsChanged?.Invoke(typeof(T));
    }

    public void Dispose() => _db.Dispose();
}
