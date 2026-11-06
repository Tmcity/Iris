using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

if (Environment.UserName != "WDAGUtilityAccount") throw new InvalidOperationException("Owned T-040 Sandbox only.");
string root = @"C:\IrisT040Output";
if (!Directory.Exists(root)) throw new InvalidOperationException("Missing owned output mapping.");
string output = Path.Combine(root, "feature-data-" + DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffffffZ", CultureInfo.InvariantCulture));
Directory.CreateDirectory(output);
string dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Iris");
var hashes = new List<object>();
foreach (string name in new[] { "settings.json", "secrets.dat" })
{
    string file = Path.Combine(dataRoot, name);
    if (!File.Exists(file)) { hashes.Add(new { Name = name, Present = false }); continue; }
    await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    byte[] raw = new byte[input.Length];
    await input.ReadExactlyAsync(raw).ConfigureAwait(false);
    hashes.Add(new { Name = name, Present = true, Bytes = raw.Length, Sha256 = Convert.ToHexStringLower(SHA256.HashData(raw)) });
    if (name == "settings.json") await File.WriteAllBytesAsync(Path.Combine(output, name), raw).ConfigureAwait(false);
}
string database = Path.Combine(dataRoot, "history.db");
var history = new List<Dictionary<string, object?>>();
long? total = null, schema = null;
string? failure = null;
if (File.Exists(database))
{
    try
    {
        var connectionString = new SqliteConnectionStringBuilder {
            DataSource = database, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Private, Pooling = false, DefaultTimeout = 3
        };
        await using var connection = new SqliteConnection(connectionString.ToString());
        await connection.OpenAsync().ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction; command.CommandText = "PRAGMA user_version;";
            schema = (long)(await command.ExecuteScalarAsync().ConfigureAwait(false) ?? 0L);
            command.CommandText = "SELECT COUNT(*) FROM translation_history;";
            total = (long)(await command.ExecuteScalarAsync().ConfigureAwait(false) ?? 0L);
            command.CommandText = "SELECT h.id,h.created_at,h.origin,h.source_text,h.source_lang,h.target_lang,h.is_favorite,r.sort_order,r.provider_id,r.provider_name,r.model,r.result_text,r.is_truncated FROM translation_history h LEFT JOIN translation_result r ON r.history_id=h.id WHERE h.id IN (SELECT id FROM translation_history ORDER BY created_at DESC,id DESC LIMIT 2000) ORDER BY h.created_at DESC,h.id DESC,r.sort_order;";
            await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                var row = new Dictionary<string, object?>(StringComparer.Ordinal);
                for (int i = 0; i < reader.FieldCount; i++) row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                history.Add(row);
            }
        }
    }
    catch (SqliteException error) { failure = error.SqliteErrorCode.ToString(CultureInfo.InvariantCulture); }
}
string logRoot = Path.Combine(dataRoot, "logs");
if (Directory.Exists(logRoot))
    foreach (string file in Directory.EnumerateFiles(logRoot, "iris-*.log"))
    {
        await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        await using var copy = File.Create(Path.Combine(output, Path.GetFileName(file)));
        await input.CopyToAsync(copy).ConfigureAwait(false);
    }
await File.WriteAllTextAsync(Path.Combine(output, "state.json"), JsonSerializer.Serialize(new {
    Utc = DateTimeOffset.UtcNow, ProductOperationPerformed = false, SqliteReadOnly = true, SecretsDecrypted = false,
    Hashes = hashes, HistoryPresent = File.Exists(database), HistorySchemaVersion = schema, HistoryTotal = total,
    MaximumHistoryEntriesCaptured = 2000, History = history, SqliteFailureCode = failure
})).ConfigureAwait(false);
return failure is null ? 0 : 1;
