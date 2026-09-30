using System;
using System.IO;
using Iris.Core.Errors;
using Iris.Platform.Windows.SystemIntegration;
using Microsoft.Win32;
using Shouldly;
using Xunit;

namespace Iris.Platform.Windows.Tests.SystemIntegration;

public sealed class AutoStartServiceTests
{
    private sealed class FakeAutoStartRegistry : IAutoStartRegistry
    {
        public string? Value { get; set; }
        public RegistryValueKind? Kind { get; set; }
        public int GetCallCount { get; private set; }
        public int SetCallCount { get; private set; }
        public int DeleteCallCount { get; private set; }
        public Exception? ThrowOnGet { get; set; }
        public Exception? ThrowOnSet { get; set; }
        public Exception? ThrowOnDelete { get; set; }

        public (string? Value, RegistryValueKind? Kind) GetRunValue(string valueName)
        {
            GetCallCount++;
            if (ThrowOnGet != null) throw ThrowOnGet;
            return (Value, Kind);
        }

        public void SetRunValue(string valueName, string value)
        {
            SetCallCount++;
            if (ThrowOnSet != null) throw ThrowOnSet;
            Value = value;
            Kind = RegistryValueKind.String;
        }

        public void DeleteRunValue(string valueName)
        {
            DeleteCallCount++;
            if (ThrowOnDelete != null) throw ThrowOnDelete;
            Value = null;
            Kind = null;
        }
    }

    private const string ValidAppPath = @"C:\Program Files\Iris\Iris.App.exe";
    private static readonly string ExpectedValidCommand = $"\"{ValidAppPath}\"";

    [Fact]
    public void IsEnabled_WhenMatchingValueExists_ReturnsTrue()
    {
        var fakeRegistry = new FakeAutoStartRegistry
        {
            Value = ExpectedValidCommand.ToLowerInvariant(), // case-insensitive
            Kind = RegistryValueKind.String
        };
        var service = new AutoStartService(fakeRegistry, () => ValidAppPath);

        service.IsEnabled().ShouldBeTrue();
    }

    [Fact]
    public void IsEnabled_WhenValueMissing_ReturnsFalse()
    {
        var fakeRegistry = new FakeAutoStartRegistry
        {
            Value = null,
            Kind = null
        };
        var service = new AutoStartService(fakeRegistry, () => ValidAppPath);

        service.IsEnabled().ShouldBeFalse();
    }

    [Fact]
    public void IsEnabled_WhenValueDoesNotMatch_ReturnsFalse()
    {
        var fakeRegistry = new FakeAutoStartRegistry
        {
            Value = "\"C:\\Other\\App.exe\"",
            Kind = RegistryValueKind.String
        };
        var service = new AutoStartService(fakeRegistry, () => ValidAppPath);

        service.IsEnabled().ShouldBeFalse();
    }

    [Fact]
    public void IsEnabled_WhenValueKindIsNotString_ReturnsFalse()
    {
        var fakeRegistry = new FakeAutoStartRegistry
        {
            Value = ExpectedValidCommand,
            Kind = RegistryValueKind.DWord
        };
        var service = new AutoStartService(fakeRegistry, () => ValidAppPath);

        service.IsEnabled().ShouldBeFalse();
    }

    [Fact]
    public void IsEnabled_WhenAppHostIsDotnetOrInvalid_ReturnsFalse_WithoutThrowing()
    {
        var fakeRegistry = new FakeAutoStartRegistry
        {
            Value = ExpectedValidCommand,
            Kind = RegistryValueKind.String
        };
        var service = new AutoStartService(fakeRegistry, () => @"C:\Program Files\dotnet\dotnet.exe");

        service.IsEnabled().ShouldBeFalse();
    }

    [Fact]
    public void IsEnabled_WhenRegistryThrows_WrapsInStorageFailure_WithSafeDetail()
    {
        var fakeRegistry = new FakeAutoStartRegistry
        {
            ThrowOnGet = new UnauthorizedAccessException("Access denied")
        };
        var service = new AutoStartService(fakeRegistry, () => ValidAppPath);

        var ex = Should.Throw<IrisException>(() => service.IsEnabled());
        ex.Code.ShouldBe(ErrorCode.StorageFailure);
        ex.InnerException.ShouldBeNull();
        ex.Detail.ShouldNotBeNull();
        ex.Detail.ShouldContain("IsEnabled");
        ex.Detail.ShouldContain("UnauthorizedAccessException");
        ex.Detail.ShouldNotContain(ValidAppPath);
        ex.Detail.ShouldNotContain("Access denied");
    }

    [Fact]
    public void SetEnabled_True_WhenNotEnabled_WritesQuotedCommand()
    {
        var fakeRegistry = new FakeAutoStartRegistry();
        var service = new AutoStartService(fakeRegistry, () => ValidAppPath);

        service.SetEnabled(true);

        fakeRegistry.SetCallCount.ShouldBe(1);
        fakeRegistry.Value.ShouldBe(ExpectedValidCommand);
        fakeRegistry.Kind.ShouldBe(RegistryValueKind.String);
    }

    [Fact]
    public void SetEnabled_True_WhenAlreadyMatching_IsNoOp()
    {
        var fakeRegistry = new FakeAutoStartRegistry
        {
            Value = ExpectedValidCommand,
            Kind = RegistryValueKind.String
        };
        var service = new AutoStartService(fakeRegistry, () => ValidAppPath);

        service.SetEnabled(true);

        fakeRegistry.SetCallCount.ShouldBe(0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("relative/path/iris.exe")]
    [InlineData("C:iris.exe")]
    [InlineData(@"\iris.exe")]
    [InlineData("C:\\path\\has\"quote\\iris.exe")]
    [InlineData("C:\\path\\has\0null\\iris.exe")]
    [InlineData("C:\\path\\has\nnewline\\iris.exe")]
    [InlineData("C:\\dotnet\\dotnet.exe")]
    [InlineData("C:\\dotnet\\dotnet")]
    [InlineData("C:\\path\\iris.bat")]
    public void SetEnabled_True_WithInvalidPath_ThrowsStorageFailure(string? invalidPath)
    {
        var fakeRegistry = new FakeAutoStartRegistry();
        var service = new AutoStartService(fakeRegistry, () => invalidPath);

        var ex = Should.Throw<IrisException>(() => service.SetEnabled(true));
        ex.Code.ShouldBe(ErrorCode.StorageFailure);
        ex.InnerException.ShouldBeNull();
        fakeRegistry.SetCallCount.ShouldBe(0);
    }

    [Fact]
    public void SetEnabled_True_WhenCommandExceeds260Chars_ThrowsStorageFailure()
    {
        // 261 chars total: "C:\" + 253 'a's + ".exe" = 260 chars, with quotes = 262 chars > 260
        var longDir = new string('a', 248);
        var longPath = $@"C:\{longDir}\iris.exe";
        (longPath.Length + 2).ShouldBeGreaterThan(260);

        var fakeRegistry = new FakeAutoStartRegistry();
        var service = new AutoStartService(fakeRegistry, () => longPath);

        var ex = Should.Throw<IrisException>(() => service.SetEnabled(true));
        ex.Code.ShouldBe(ErrorCode.StorageFailure);
        ex.Detail.ShouldNotBeNull();
        ex.Detail!.ShouldContain("CommandLengthExceeded");
        ex.Detail!.ShouldNotContain(longPath);
    }

    [Fact]
    public void SetEnabled_False_WhenPresent_DeletesValue()
    {
        var fakeRegistry = new FakeAutoStartRegistry
        {
            Value = ExpectedValidCommand,
            Kind = RegistryValueKind.String
        };
        var service = new AutoStartService(fakeRegistry, () => ValidAppPath);

        service.SetEnabled(false);

        fakeRegistry.DeleteCallCount.ShouldBe(1);
        fakeRegistry.Value.ShouldBeNull();
    }

    [Fact]
    public void SetEnabled_False_WorksEvenIfAppHostPathIsInvalid()
    {
        var fakeRegistry = new FakeAutoStartRegistry
        {
            Value = ExpectedValidCommand,
            Kind = RegistryValueKind.String
        };
        // Invalid path provider
        var service = new AutoStartService(fakeRegistry, () => throw new InvalidOperationException("No path"));

        Should.NotThrow(() => service.SetEnabled(false));
        fakeRegistry.DeleteCallCount.ShouldBe(1);
        fakeRegistry.Value.ShouldBeNull();
    }

    [Fact]
    public void SetEnabled_False_WhenRegistryThrows_WrapsInStorageFailure_WithSafeDetail()
    {
        var fakeRegistry = new FakeAutoStartRegistry
        {
            ThrowOnDelete = new IOException("Disk error")
        };
        var service = new AutoStartService(fakeRegistry, () => ValidAppPath);

        var ex = Should.Throw<IrisException>(() => service.SetEnabled(false));
        ex.Code.ShouldBe(ErrorCode.StorageFailure);
        ex.InnerException.ShouldBeNull();
        ex.Detail.ShouldNotBeNull();
        ex.Detail!.ShouldContain("SetEnabled:Delete");
        ex.Detail!.ShouldContain("IOException");
        ex.Detail!.ShouldNotContain("Disk error");
    }

    [Fact]
    public void RealRegistry_IsolatedSubKey_FullLifecycleWorks()
    {
        var isolatedSubKey = $@"Software\IrisTests\AutoStart_{Guid.NewGuid():N}";
        try
        {
            var registry = new WindowsAutoStartRegistry(isolatedSubKey);
            var service = new AutoStartService(registry, () => ValidAppPath);

            // Initially false
            service.IsEnabled().ShouldBeFalse();

            // Set true
            service.SetEnabled(true);
            service.IsEnabled().ShouldBeTrue();

            // Idempotent set true
            service.SetEnabled(true);
            service.IsEnabled().ShouldBeTrue();

            // Set false
            service.SetEnabled(false);
            service.IsEnabled().ShouldBeFalse();

            // Idempotent set false
            service.SetEnabled(false);
            service.IsEnabled().ShouldBeFalse();
        }
        finally
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(isolatedSubKey, throwOnMissingSubKey: false);
            }
            catch
            {
                // Best effort cleanup in tests
            }
        }
    }
}
