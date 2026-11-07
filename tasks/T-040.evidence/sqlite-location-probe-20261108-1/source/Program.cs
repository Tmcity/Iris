using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

if (Environment.UserName != "WDAGUtilityAccount") throw new InvalidOperationException("Owned Sandbox only.");
var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IrisT040Diagnostic", "sqlite-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
Directory.CreateDirectory(directory);
string path = Path.Combine(directory, "probe.db");
using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ConnectionString);
connection.Open();
using var command = connection.CreateCommand();
command.CommandText = "PRAGMA journal_mode=WAL;";
string? mode = Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture);
command.CommandText = "CREATE TABLE sample (id INTEGER PRIMARY KEY, value TEXT); INSERT INTO sample(value) VALUES ('owned synthetic check'); SELECT COUNT(*) FROM sample;";
long count = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
if (mode != "wal" || count != 1) throw new InvalidOperationException("Guest-local WAL verification failed.");
string output = Path.Combine(@"C:\IrisT040Output", "sqlite-local-verification.json");
await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { Utc = DateTimeOffset.UtcNow, LocalDirectory = directory, JournalMode = mode, Rows = count, ProductOperationPerformed = false, ProductDataChanged = false })).ConfigureAwait(false);
