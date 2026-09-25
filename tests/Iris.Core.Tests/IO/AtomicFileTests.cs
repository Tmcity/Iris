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

    [Fact]
    public async Task CopyAsync_Success_CopiesContent()
    {
        var src = Path.Combine(_testDir, "src.txt");
        var dest = Path.Combine(_testDir, "dest.txt");
        await File.WriteAllTextAsync(src, "test-data", TestContext.Current.CancellationToken);

        await AtomicFile.CopyAsync(src, dest, overwrite: true, TestContext.Current.CancellationToken);

        File.Exists(dest).ShouldBeTrue();
        File.ReadAllText(dest, Encoding.UTF8).ShouldBe("test-data");
        File.Exists(src).ShouldBeTrue();
    }

    [Fact]
    public async Task CopyAsync_TargetLocked_ThrowsStorageFailureWithArrowDetail()
    {
        var src = Path.Combine(_testDir, "src.txt");
        var dest = Path.Combine(_testDir, "dest.txt");
        await File.WriteAllTextAsync(src, "source-data", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(dest, "dest-data", TestContext.Current.CancellationToken);

        using (new FileStream(dest, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var ex = await Should.ThrowAsync<IrisException>(async () =>
                await AtomicFile.CopyAsync(src, dest, overwrite: true, TestContext.Current.CancellationToken));

            ex.Code.ShouldBe(ErrorCode.StorageFailure);
            ex.Detail.ShouldBe($"{src} → {dest}");
        }
    }

    [Fact]
    public async Task CopyAsync_WhenTokenPreCancelled_ThrowsOperationCanceledException()
    {
        var src = Path.Combine(_testDir, "src.txt");
        var dest = Path.Combine(_testDir, "dest.txt");
        await File.WriteAllTextAsync(src, "source-data", TestContext.Current.CancellationToken);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await AtomicFile.CopyAsync(src, dest, overwrite: true, cts.Token));

        File.Exists(dest).ShouldBeFalse();
    }

    [Fact]
    public async Task MoveAsync_Success_MovesFile()
    {
        var src = Path.Combine(_testDir, "src_move.txt");
        var dest = Path.Combine(_testDir, "dest_move.txt");
        await File.WriteAllTextAsync(src, "move-data", TestContext.Current.CancellationToken);

        await AtomicFile.MoveAsync(src, dest, overwrite: true, TestContext.Current.CancellationToken);

        File.Exists(dest).ShouldBeTrue();
        File.ReadAllText(dest, Encoding.UTF8).ShouldBe("move-data");
        File.Exists(src).ShouldBeFalse();
    }

    [Fact]
    public async Task MoveAsync_TargetLocked_ThrowsStorageFailureWithArrowDetail()
    {
        var src = Path.Combine(_testDir, "src_move.txt");
        var dest = Path.Combine(_testDir, "dest_move.txt");
        await File.WriteAllTextAsync(src, "source-data", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(dest, "dest-data", TestContext.Current.CancellationToken);

        using (new FileStream(dest, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var ex = await Should.ThrowAsync<IrisException>(async () =>
                await AtomicFile.MoveAsync(src, dest, overwrite: true, TestContext.Current.CancellationToken));

            ex.Code.ShouldBe(ErrorCode.StorageFailure);
            ex.Detail.ShouldBe($"{src} → {dest}");
        }

        File.Exists(src).ShouldBeTrue();
    }

    [Fact]
    public async Task MoveAsync_WhenTokenPreCancelled_ThrowsOperationCanceledException()
    {
        var src = Path.Combine(_testDir, "src_move.txt");
        var dest = Path.Combine(_testDir, "dest_move.txt");
        await File.WriteAllTextAsync(src, "source-data", TestContext.Current.CancellationToken);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await AtomicFile.MoveAsync(src, dest, overwrite: true, cts.Token));

        File.Exists(src).ShouldBeTrue();
        File.Exists(dest).ShouldBeFalse();
    }

    [Fact]
    public void WriteAllBytes_ContentCorrect_NoTmpResidual()
    {
        var targetFile = Path.Combine(_testDir, "sync_test.txt");
        var tmpFile = targetFile + ".tmp";
        var content = Encoding.UTF8.GetBytes("hello sync world 你好");

        AtomicFile.WriteAllBytes(targetFile, content, null);

        File.Exists(targetFile).ShouldBeTrue();
        File.ReadAllBytes(targetFile).ShouldBe(content);
        File.Exists(tmpFile).ShouldBeFalse();
    }

    [Fact]
    public void WriteAllBytes_WhenTargetExistsAndBackupProvided_BacksUpOldContent()
    {
        var targetFile = Path.Combine(_testDir, "sync_test_backup.txt");
        var backupFile = Path.Combine(_testDir, "sync_test_backup.txt.bak");
        var oldContent = Encoding.UTF8.GetBytes("old sync content");
        var newContent = Encoding.UTF8.GetBytes("new sync content");

        File.WriteAllBytes(targetFile, oldContent);

        AtomicFile.WriteAllBytes(targetFile, newContent, backupFile);

        File.ReadAllBytes(targetFile).ShouldBe(newContent);
        File.Exists(backupFile).ShouldBeTrue();
        File.ReadAllBytes(backupFile).ShouldBe(oldContent);
    }

    [Fact]
    public void WriteAllBytes_WhenTmpLocked_ThrowsStorageFailureAndTargetUnchanged()
    {
        var targetFile = Path.Combine(_testDir, "sync_tmp_locked.txt");
        var tmpFile = targetFile + ".tmp";
        var originalContent = Encoding.UTF8.GetBytes("orig sync content");
        File.WriteAllBytes(targetFile, originalContent);

        using (new FileStream(tmpFile, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            var ex = Should.Throw<IrisException>(() =>
                AtomicFile.WriteAllBytes(targetFile, Encoding.UTF8.GetBytes("new attempt"), null));

            ex.Code.ShouldBe(ErrorCode.StorageFailure);
            ex.Detail.ShouldBe(targetFile);
        }

        File.ReadAllBytes(targetFile).ShouldBe(originalContent);
    }

    [Fact]
    public void WriteAllBytes_WhenTargetLockedIndefinitely_ThrowsStorageFailureAndCleansTmp()
    {
        var targetFile = Path.Combine(_testDir, "sync_target_locked.txt");
        var tmpFile = targetFile + ".tmp";
        var originalContent = Encoding.UTF8.GetBytes("orig sync content");
        File.WriteAllBytes(targetFile, originalContent);

        using (new FileStream(targetFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var ex = Should.Throw<IrisException>(() =>
                AtomicFile.WriteAllBytes(targetFile, Encoding.UTF8.GetBytes("new attempt"), null));

            ex.Code.ShouldBe(ErrorCode.StorageFailure);
            ex.Detail.ShouldBe(targetFile);
        }

        File.Exists(tmpFile).ShouldBeFalse();
        File.ReadAllBytes(targetFile).ShouldBe(originalContent);
    }

    [Fact]
    public void Move_ContentMoved_SourceDeleted()
    {
        var src = Path.Combine(_testDir, "sync_move_src.txt");
        var dest = Path.Combine(_testDir, "sync_move_dest.txt");
        File.WriteAllText(src, "sync move data");

        AtomicFile.Move(src, dest, overwrite: true);

        File.Exists(dest).ShouldBeTrue();
        File.ReadAllText(dest).ShouldBe("sync move data");
        File.Exists(src).ShouldBeFalse();
    }

    [Fact]
    public void Move_TargetLocked_ThrowsStorageFailureWithArrowDetail()
    {
        var src = Path.Combine(_testDir, "sync_move_src_locked.txt");
        var dest = Path.Combine(_testDir, "sync_move_dest_locked.txt");
        File.WriteAllText(src, "source data");
        File.WriteAllText(dest, "dest data");

        using (new FileStream(dest, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var ex = Should.Throw<IrisException>(() =>
                AtomicFile.Move(src, dest, overwrite: true));

            ex.Code.ShouldBe(ErrorCode.StorageFailure);
            ex.Detail.ShouldBe($"{src} → {dest}");
        }

        File.Exists(src).ShouldBeTrue();
    }

    [Fact]
    public async Task SyncAndAsync_ProduceIdenticalBytes_ConsistencyTest()
    {
        var syncFile = Path.Combine(_testDir, "consistency_sync.txt");
        var asyncFile = Path.Combine(_testDir, "consistency_async.txt");
        var testData = Encoding.UTF8.GetBytes("Consistency test payload with \u4e2d\u6587 and emojis \U0001F680");

        AtomicFile.WriteAllBytes(syncFile, testData, null);
        await AtomicFile.WriteAllBytesAsync(asyncFile, testData, null, TestContext.Current.CancellationToken);

        var syncBytes = File.ReadAllBytes(syncFile);
        var asyncBytes = File.ReadAllBytes(asyncFile);

        syncBytes.ShouldBe(asyncBytes);
    }

    [Fact]
    public void ReadAllBytes_NormalRead_Succeeds()
    {
        var file = Path.Combine(_testDir, "read_all_bytes_sync.txt");
        var expected = Encoding.UTF8.GetBytes("Sync read data \u4e2d\u6587");
        File.WriteAllBytes(file, expected);

        var actual = AtomicFile.ReadAllBytes(file);
        actual.ShouldBe(expected);
    }

    [Fact]
    public void ReadAllBytes_FileLockedWithFileShareNone_RetriesAndThrowsStorageFailure()
    {
        var file = Path.Combine(_testDir, "read_all_bytes_sync_locked.txt");
        File.WriteAllBytes(file, [1, 2, 3]);

        using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var ex = Should.Throw<IrisException>(() => AtomicFile.ReadAllBytes(file));
            ex.Code.ShouldBe(ErrorCode.StorageFailure);
            ex.Detail.ShouldBe(file);
        }
    }

    [Fact]
    public void ReadAllBytes_FileDoesNotExist_ThrowsStorageFailure()
    {
        var file = Path.Combine(_testDir, "non_existent_sync.txt");
        var ex = Should.Throw<IrisException>(() => AtomicFile.ReadAllBytes(file));
        ex.Code.ShouldBe(ErrorCode.StorageFailure);
        ex.Detail.ShouldBe(file);
    }

    [Fact]
    public async Task ReadAllBytesAsync_NormalRead_Succeeds()
    {
        var file = Path.Combine(_testDir, "read_all_bytes_async.txt");
        var expected = Encoding.UTF8.GetBytes("Async read data \u4e2d\u6587");
        await File.WriteAllBytesAsync(file, expected, TestContext.Current.CancellationToken);

        var actual = await AtomicFile.ReadAllBytesAsync(file, TestContext.Current.CancellationToken);
        actual.ShouldBe(expected);
    }

    [Fact]
    public async Task ReadAllBytesAsync_FileLockedWithFileShareNone_RetriesAndThrowsStorageFailure()
    {
        var file = Path.Combine(_testDir, "read_all_bytes_async_locked.txt");
        await File.WriteAllBytesAsync(file, [1, 2, 3], TestContext.Current.CancellationToken);

        using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var ex = await Should.ThrowAsync<IrisException>(() =>
                AtomicFile.ReadAllBytesAsync(file, TestContext.Current.CancellationToken));
            ex.Code.ShouldBe(ErrorCode.StorageFailure);
            ex.Detail.ShouldBe(file);
        }
    }

    [Fact]
    public async Task ReadAllBytesAsync_FileDoesNotExist_ThrowsStorageFailure()
    {
        var file = Path.Combine(_testDir, "non_existent_async.txt");
        var ex = await Should.ThrowAsync<IrisException>(() =>
            AtomicFile.ReadAllBytesAsync(file, TestContext.Current.CancellationToken));
        ex.Code.ShouldBe(ErrorCode.StorageFailure);
        ex.Detail.ShouldBe(file);
    }
}
