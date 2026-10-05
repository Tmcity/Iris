using System.IO;
using System.Text.Json;
using Iris.Core.Settings;
using Iris.Core.Storage;
using Iris.Infrastructure.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Iris.App.Tests.Settings;

public sealed class SelectionSettingsPersistenceTests
{
    [Fact]
    public async Task RealJsonSave_ReopenedWindowLoadsPersistedSelection()
    {
        var paths = new TestPaths(Path.Combine(Path.GetTempPath(), "iris-t023b-settings-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(paths.DataDirectory);
        using (var services = CreateProvider(paths))
        {
            var settings = services.GetRequiredService<ISettingsService>(); await settings.LoadAsync(TestContext.Current.CancellationToken);
            await SelectionSettingsTestScope.Run(async f =>
            {
                f.Show(); f.Vm.Selection.ClipboardTimeoutText = "1100"; f.Vm.Selection.ReadOrderChoice = 2;
                f.Vm.Selection.ClipboardUnsafeProcessesText = " app.exe \nAPP.EXE\nhelper.bin";
                await f.Vm.ExecuteSaveAsync(); f.Vm.IsDirty.ShouldBeFalse(); settings.Current.Selection.ClipboardTimeoutMs.ShouldBe(1100);
            }, settings);
        }
        using var reloaded = CreateProvider(paths);
        var loaded = reloaded.GetRequiredService<ISettingsService>(); await loaded.LoadAsync(TestContext.Current.CancellationToken);
        await SelectionSettingsTestScope.Run(f =>
        {
            f.Show(); f.Vm.Selection.ClipboardTimeoutText.ShouldBe("1100"); f.Vm.Selection.ReadOrderChoice.ShouldBe(2);
            f.Vm.Selection.ClipboardUnsafeProcessesText.ShouldBe("app.exe\nhelper.bin"); f.Vm.IsDirty.ShouldBeFalse(); return Task.CompletedTask;
        }, loaded);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(paths.SettingsFile, TestContext.Current.CancellationToken));
        json.RootElement.GetProperty("selection").GetProperty("clipboardTimeoutMs").GetInt32().ShouldBe(1100);
    }
    private static ServiceProvider CreateProvider(TestPaths paths)
    {
        var services = new ServiceCollection(); services.AddLogging(); services.AddSingleton<IAppPaths>(paths); services.AddIrisInfrastructure();
        return services.BuildServiceProvider();
    }
    private sealed class TestPaths(string directory) : IAppPaths
    {
        public bool IsPortable => true;
        public string DataDirectory => directory;
        public string SettingsFile => Path.Combine(directory, "settings.json");
        public string SecretsFile => Path.Combine(directory, "unused-secrets.dat");
        public string DatabaseFile => Path.Combine(directory, "unused-history.db");
        public string LogDirectory => Path.Combine(directory, "unused-logs");
    }
}
