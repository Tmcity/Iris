using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Iris.Core.Settings;

namespace Iris.Infrastructure.Settings;

internal sealed class SettingsMigrationRunner
{
    private readonly Dictionary<int, ISettingsMigration> _migrations;

    public SettingsMigrationRunner(IEnumerable<ISettingsMigration> migrations)
    {
        ArgumentNullException.ThrowIfNull(migrations);
        _migrations = new Dictionary<int, ISettingsMigration>();

        foreach (var migration in migrations)
        {
            if (!_migrations.TryAdd(migration.FromVersion, migration))
            {
                throw new InvalidOperationException($"存在重复的迁移 FromVersion: {migration.FromVersion}");
            }
        }
    }

    public JsonObject Run(JsonObject root, int fromVersion, int toVersion)
    {
        ArgumentNullException.ThrowIfNull(root);

        if (fromVersion == toVersion)
        {
            return root;
        }

        if (fromVersion > toVersion)
        {
            throw new ArgumentException($"fromVersion ({fromVersion}) 不能大于 toVersion ({toVersion})");
        }

        var current = root;
        for (var v = fromVersion; v < toVersion; v++)
        {
            if (!_migrations.TryGetValue(v, out var migration))
            {
                throw new InvalidOperationException($"缺少 schemaVersion {v} 的迁移");
            }

            current = migration.Migrate(current) ?? throw new InvalidOperationException($"schemaVersion {v} 的迁移返回了 null");
        }

        return current;
    }
}
