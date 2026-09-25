using System;
using System.Text.Json.Nodes;
using Iris.Core.Settings;

namespace Iris.Infrastructure.Tests.TestSupport;

internal sealed class TestMigration(int fromVersion, Func<JsonObject, JsonObject>? migrateFunc = null) : ISettingsMigration
{
    public int FromVersion { get; } = fromVersion;

    public JsonObject Migrate(JsonObject root)
    {
        if (migrateFunc != null)
        {
            return migrateFunc(root);
        }

        root["schemaVersion"] = FromVersion + 1;
        return root;
    }
}
