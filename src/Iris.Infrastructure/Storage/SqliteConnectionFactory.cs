using System;
using System.IO;
using Iris.Core.Errors;
using Iris.Core.Storage;
using Microsoft.Data.Sqlite;

namespace Iris.Infrastructure.Storage;

internal interface ISqliteConnectionFactory
{
    bool DatabaseExists { get; }
    SqliteConnection CreateConnection();
    SqliteConnection CreateReadOnlyConnection();
}

internal sealed class SqliteConnectionFactory : ISqliteConnectionFactory
{
    private readonly IAppPaths _appPaths;
    private readonly string _connectionString;
    private readonly string _readOnlyConnectionString;

    internal Action<int>? OnConnectionOpened { get; set; }
    internal Action<SqliteConnection>? OnConnectionCreated { get; set; }

    public bool DatabaseExists => File.Exists(_appPaths.DatabaseFile);

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

        var readOnlyBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = _appPaths.DatabaseFile,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 3,
        };
        _readOnlyConnectionString = readOnlyBuilder.ConnectionString;
    }

    public SqliteConnection CreateConnection()
    {
        SqliteConnection? connection = null;
        try
        {
            var dir = Path.GetDirectoryName(_appPaths.DatabaseFile);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            connection = new SqliteConnection(_connectionString);
            OnConnectionCreated?.Invoke(connection);
            connection.Open();

            OnConnectionOpened?.Invoke(Environment.CurrentManagedThreadId);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 3000;";
            cmd.ExecuteNonQuery();

            cmd.CommandText = "PRAGMA foreign_keys;";
            var fk = Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            if (fk != 1)
            {
                throw new IrisException(ErrorCode.StorageFailure, "Failed to enable foreign_keys pragma on SQLite connection.");
            }

            cmd.CommandText = "PRAGMA busy_timeout;";
            var timeout = Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            if (timeout < 3000)
            {
                throw new IrisException(ErrorCode.StorageFailure, "Failed to set busy_timeout pragma on SQLite connection.");
            }

            var result = connection;
            connection = null; // 所有权转移成功
            return result;
        }
        catch (IrisException)
        {
            throw;
        }
        catch (Exception)
        {
            // 不回显任何内部异常消息或敏感值
            throw new IrisException(ErrorCode.StorageFailure, "Failed to open SQLite database connection.");
        }
        finally
        {
            connection?.Dispose();
        }
    }

    public SqliteConnection CreateReadOnlyConnection()
    {
        SqliteConnection? connection = null;
        try
        {
            if (!DatabaseExists)
            {
                throw new IrisException(ErrorCode.StorageFailure, "Database file does not exist.");
            }

            connection = new SqliteConnection(_readOnlyConnectionString);
            OnConnectionCreated?.Invoke(connection);
            connection.Open();

            OnConnectionOpened?.Invoke(Environment.CurrentManagedThreadId);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA busy_timeout = 3000;";
            cmd.ExecuteNonQuery();

            var result = connection;
            connection = null; // 所有权转移成功
            return result;
        }
        catch (IrisException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new IrisException(ErrorCode.StorageFailure, "Failed to open read-only SQLite database connection.");
        }
        finally
        {
            connection?.Dispose();
        }
    }
}
