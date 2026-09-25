using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Iris.Core.Errors;
using Iris.Core.IO;
using Shouldly;
using Xunit;

namespace Iris.Core.Tests.IO;

public sealed class AtomicFileTests : IDisposable
{
    private readonly string _testDir;

    public AtomicFileTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "IrisAtomicTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, recursive: true);
            }
        }
        catch
        {
            // Ignore test cleanup error
        }
    }

    [Fact]
    public async Task WriteAllTextAsync_ContentCorrect_NoTmpResidual()
    {
        var targetFile = Path.Combine(_testDir, "test.txt");
        var tmpFile = targetFile + ".tmp";
        const string content = "hello world \u4f60\u597d";

        await AtomicFile.WriteAllTextAsync(targetFile, content, null, TestContext.Current.CancellationToken);

        File.Exists(targetFile).ShouldBeTrue();
        File.ReadAllText(targetFile, Encoding.UTF8).ShouldBe(content);
        File.Exists(tmpFile).ShouldBeFalse();
    }

    [Fact]
    public async Task WriteAllTextAsync_WhenTargetExistsAndBackupProvided_BacksUpOldContent()
    {
        var targetFile = Path.Combine(_testDir, "test.txt");
        var backupFile = Path.Combine(_testDir, "test.txt.bak");
        const string oldContent = "old content";
        const string newContent = "new content";

        await File.WriteAllTextAsync(targetFile, oldContent, TestContext.Current.CancellationToken);

        await AtomicFile.WriteAllTextAsync(targetFile, newContent, backupFile, TestContext.Current.CancellationToken);

        File.ReadAllText(targetFile, Encoding.UTF8).ShouldBe(newContent);
        File.Exists(backupFile).ShouldBeTrue();
        File.ReadAllText(backupFile, Encoding.UTF8).ShouldBe(oldContent);
    }

    [Fact]
    public async Task WriteAllTextAsync_WhenTmpLocked_ThrowsStorageFailureAndTargetUnchanged()
    {
        var targetFile = Path.Combine(_testDir, "test.txt");
        var tmpFile = targetFile + ".tmp";
        const string originalContent = "original content";
        await File.WriteAllTextAsync(targetFile, originalContent, TestContext.Current.CancellationToken);

        // Lock .tmp file with FileShare.None
        using (new FileStream(tmpFile, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            var ex = await Should.ThrowAsync<IrisException>(async () =>
                await AtomicFile.WriteAllTextAsync(targetFile, "attempted content", null, TestContext.Current.CancellationToken));

            ex.Code.ShouldBe(ErrorCode.StorageFailure);
            ex.Detail.ShouldBe(targetFile);
        }

        File.ReadAllText(targetFile, Encoding.UTF8).ShouldBe(originalContent);
    }

    [Fact]
    public async Task WriteAllTextAsync_WhenTargetLockedIndefinitely_ThrowsStorageFailureAndCleansTmp()
    {
        var targetFile = Path.Combine(_testDir, "test.txt");
        var tmpFile = targetFile + ".tmp";
        const string originalContent = "original content";
        await File.WriteAllTextAsync(targetFile, originalContent, TestContext.Current.CancellationToken);

        // Lock target file with FileShare.None
        using (new FileStream(targetFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var ex = await Should.ThrowAsync<IrisException>(async () =>
                await AtomicFile.WriteAllTextAsync(targetFile, "attempted content", null, TestContext.Current.CancellationToken));

            ex.Code.ShouldBe(ErrorCode.StorageFailure);
            ex.Detail.ShouldBe(targetFile);
        }

        // tmp file should have been cleaned up
        File.Exists(tmpFile).ShouldBeFalse();
        File.ReadAllText(targetFile, Encoding.UTF8).ShouldBe(originalContent);
    }

    [Fact]
    public async Task WriteAllTextAsync_WhenTokenPreCancelled_ThrowsOperationCanceledExceptionAndNoFilesCreated()
    {
        var targetFile = Path.Combine(_testDir, "test.txt");
        var tmpFile = targetFile + ".tmp";
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await AtomicFile.WriteAllTextAsync(targetFile, "data", null, cts.Token));

        File.Exists(targetFile).ShouldBeFalse();
        File.Exists(tmpFile).ShouldBeFalse();
    }

    [Fact]
    public async Task WriteAllTextAsync_WrittenFileHasNoBom()
    {
        var targetFile = Path.Combine(_testDir, "nobom.txt");
        await AtomicFile.WriteAllTextAsync(targetFile, "abc", null, TestContext.Current.CancellationToken);

        var bytes = await File.ReadAllBytesAsync(targetFile, TestContext.Current.CancellationToken);
        bytes.Length.ShouldBe(3);
        // UTF-8 BOM is 0xEF, 0xBB, 0xBF
        if (bytes.Length >= 3)
        {
            var hasBom = bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            hasBom.ShouldBeFalse();
        }
    }

    [Fact]
    public async Task WriteAllTextAsync_TargetDirectoryDoesNotExist_ThrowsStorageFailure()
    {
        var nonExistentDir = Path.Combine(_testDir, "sub_does_not_exist");
        var targetFile = Path.Combine(nonExistentDir, "test.txt");

        var ex = await Should.ThrowAsync<IrisException>(async () =>
            await AtomicFile.WriteAllTextAsync(targetFile, "content", null, TestContext.Current.CancellationToken));

        ex.Code.ShouldBe(ErrorCode.StorageFailure);
    }

    [Fact]
    public async Task WriteAllBytesAsync_RawBytes_WrittenCorrectly()
    {
        var targetFile = Path.Combine(_testDir, "bytes.bin");
        byte[] data = [1, 2, 3, 4, 5];
        await AtomicFile.WriteAllBytesAsync(targetFile, data, null, TestContext.Current.CancellationToken);

        var read = await File.ReadAllBytesAsync(targetFile, TestContext.Current.CancellationToken);
        read.ShouldBe(data);
    }
}
