using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Iris.Core.Settings;
using Iris.Infrastructure.Settings;
using Iris.Infrastructure.Tests.TestSupport;
using Shouldly;
using Xunit;

namespace Iris.Infrastructure.Tests.Settings;

public sealed class SettingsMigrationRunnerTests
{
    [Fact]
    public void Constructor_WhenDuplicateFromVersion_ThrowsInvalidOperationException()
    {
        var migrations = new ISettingsMigration[]
        {
            new TestMigration(0),
            new TestMigration(0),
        };

        Should.Throw<InvalidOperationException>(() => new SettingsMigrationRunner(migrations));
    }

    [Fact]
    public void Run_WhenFromEqualsTo_ReturnsOriginalObject()
    {
        var runner = new SettingsMigrationRunner(Array.Empty<ISettingsMigration>());
        var json = new JsonObject { ["key"] = "value" };

        var result = runner.Run(json, 1, 1);

        result.ShouldBeSameAs(json);
    }

    [Fact]
    public void Run_TwoStepChain_ExecutesInOrder()
    {
        var stepLog = new List<string>();

        var m0 = new TestMigration(0, root =>
        {
            stepLog.Add("0->1");
            root["v0to1"] = true;
            return root;
        });

        var m1 = new TestMigration(1, root =>
        {
            stepLog.Add("1->2");
            root["v1to2"] = true;
            return root;
        });

        var runner = new SettingsMigrationRunner([m1, m0]); // pass in reverse order to ensure runner orders them
        var root = new JsonObject();

        var result = runner.Run(root, 0, 2);

        stepLog.ShouldBe(["0->1", "1->2"]);
        result["v0to1"]?.GetValue<bool>().ShouldBeTrue();
        result["v1to2"]?.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public void Run_WhenMissingMigrationInChain_ThrowsInvalidOperationException()
    {
        var m0 = new TestMigration(0);
        // Missing version 1 migration
        var m2 = new TestMigration(2);

        var runner = new SettingsMigrationRunner([m0, m2]);
        var root = new JsonObject();

        var ex = Should.Throw<InvalidOperationException>(() => runner.Run(root, 0, 3));
        ex.Message.ShouldContain("缺少 schemaVersion 1 的迁移");
    }
}
