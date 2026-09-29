using System;
using System.IO;
using Iris.Core.Errors;
using Iris.Core.Storage;
using Microsoft.Data.Sqlite;

namespace Iris.Infrastructure.Storage;

internal interface ISqliteConnectionFactory
{
    SqliteConnection CreateConnection();
}

internal sealed class SqliteConnectionFactory : ISqliteConnectionFactory
{
    private readonly IAppPaths _appPaths;
    private readonly string _connectionString;

    public SqliteConnectionFactory(IAppPaths appPaths)
    {
        _appPaths = appPaths ?? throw new ArgumentNullException(nameof(appPaths));

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _appPaths.DatabaseFile,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 3,
        };
        _connectionString = builder.ConnectionString;
    }

    public SqliteConnection CreateConnection()
    {
        try
        {
            var dir = Path.GetDirectoryName(_appPaths.DatabaseFile);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 3000;";
            cmd.ExecuteNonQuery();

            cmd.CommandText = "PRAGMA foreign_keys;";
            var fk = Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            if (fk != 1)
            {
                connection.Dispose();
                throw new IrisException(ErrorCode.StorageFailure, "Failed to enable foreign_keys pragma on SQLite connection.");
            }

            cmd.CommandText = "PRAGMA busy_timeout;";
            var timeout = Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            if (timeout < 3000)
            {
                connection.Dispose();
                throw new IrisException(ErrorCode.StorageFailure, "Failed to set busy_timeout pragma on SQLite connection.");
            }

            return connection;
        }
        catch (IrisException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new IrisException(ErrorCode.StorageFailure, "Failed to open SQLite database connection.", innerException: ex);
        }
    }
}
