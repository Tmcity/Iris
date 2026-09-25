using System;
using System.IO;
using Iris.Core.Errors;
using Iris.Infrastructure.Storage;
using Shouldly;
using Xunit;

namespace Iris.Infrastructure.Tests.Storage;

public sealed class AppPathsTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _exeDir;
    private readonly string _appDataDir;

    public AppPathsTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "IrisTest_" + Guid.NewGuid().ToString("N"));
        _exeDir = Path.Combine(_tempRoot, "exe");
        _appDataDir = Path.Combine(_tempRoot, "appdata");
        Directory.CreateDirectory(_exeDir);
        Directory.CreateDirectory(_appDataDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup failure in tests
        }
    }

    [Fact]
    public void NonPortable_UsesRoamingAppData()
    {
        var paths = new AppPaths(_exeDir, _appDataDir);

        paths.IsPortable.ShouldBeFalse();
        paths.DataDirectory.ShouldBe(Path.Combine(_appDataDir, "Iris"));
        paths.SettingsFile.ShouldBe(Path.Combine(paths.DataDirectory, "settings.json"));
        paths.SecretsFile.ShouldBe(Path.Combine(paths.DataDirectory, "secrets.dat"));
        paths.DatabaseFile.ShouldBe(Path.Combine(paths.DataDirectory, "history.db"));
        paths.LogDirectory.ShouldBe(Path.Combine(paths.DataDirectory, "logs"));
    }

    [Fact]
    public void Portable_WhenMarkerExists_UsesExeDataFolder()
    {
        File.WriteAllText(Path.Combine(_exeDir, "portable.txt"), string.Empty);
        var paths = new AppPaths(_exeDir, _appDataDir);

        paths.IsPortable.ShouldBeTrue();
        paths.DataDirectory.ShouldBe(Path.Combine(_exeDir, "data"));
        paths.SettingsFile.ShouldBe(Path.Combine(paths.DataDirectory, "settings.json"));
        paths.SecretsFile.ShouldBe(Path.Combine(paths.DataDirectory, "secrets.dat"));
        paths.DatabaseFile.ShouldBe(Path.Combine(paths.DataDirectory, "history.db"));
        paths.LogDirectory.ShouldBe(Path.Combine(paths.DataDirectory, "logs"));
    }

    [Fact]
    public void PortableMarker_IsCaseInsensitive()
    {
        File.WriteAllText(Path.Combine(_exeDir, "PORTABLE.TXT"), string.Empty);
        var paths = new AppPaths(_exeDir, _appDataDir);

        paths.IsPortable.ShouldBeTrue();
        paths.DataDirectory.ShouldBe(Path.Combine(_exeDir, "data"));
    }

    [Fact]
    public void Constructor_CreatesDataAndLogDirectories()
    {
        var paths = new AppPaths(_exeDir, _appDataDir);

        Directory.Exists(paths.DataDirectory).ShouldBeTrue();
        Directory.Exists(paths.LogDirectory).ShouldBeTrue();
    }

    [Fact]
    public void Constructor_ExistingDirectories_DoesNotThrow()
    {
        var paths1 = new AppPaths(_exeDir, _appDataDir);
        var paths2 = new AppPaths(_exeDir, _appDataDir);

        Directory.Exists(paths2.DataDirectory).ShouldBeTrue();
        Directory.Exists(paths2.LogDirectory).ShouldBeTrue();
    }

    [Fact]
    public void Constructor_WhenDirectoryCannotBeCreated_ThrowsStorageFailure()
    {
        var targetIrisPath = Path.Combine(_appDataDir, "Iris");
        File.WriteAllText(targetIrisPath, "conflict");

        var ex = Should.Throw<IrisException>(() => new AppPaths(_exeDir, _appDataDir));
        ex.Code.ShouldBe(ErrorCode.StorageFailure);
        ex.Detail.ShouldNotBeNull();
        ex.Detail.ShouldContain(targetIrisPath);
    }

    [Fact]
    public void EmptyAppData_NonPortable_ThrowsStorageFailure()
    {
        var ex1 = Should.Throw<IrisException>(() => new AppPaths(_exeDir, string.Empty));
        ex1.Code.ShouldBe(ErrorCode.StorageFailure);

        var ex2 = Should.Throw<IrisException>(() => new AppPaths(_exeDir, "   "));
        ex2.Code.ShouldBe(ErrorCode.StorageFailure);
    }
}
