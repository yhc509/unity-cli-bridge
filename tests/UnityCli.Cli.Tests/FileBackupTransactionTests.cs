using UnityCli.Protocol;

namespace UnityCli.Cli.Tests;

public sealed class FileBackupTransactionTests
{
    [Fact]
    public void RunWithBackup_WhenActionSucceeds_KeepsMutationAndDeletesBackups()
    {
        using var temp = new TempDirectory();
        string path = Path.Combine(temp.Path, "Thing.asset");
        string metaPath = path + ".meta";
        File.WriteAllText(path, "before");
        File.WriteAllText(metaPath, "guid: before");

        FileBackupTransactionOptions options = CreateOptions(temp, "success");

        string result = FileBackupTransaction.RunWithBackup(
            path,
            "asset-test",
            () =>
            {
                File.WriteAllText(path, "after");
                File.WriteAllText(metaPath, "guid: after");
                return "ok";
            },
            options);

        Assert.Equal("ok", result);
        Assert.Equal("after", File.ReadAllText(path));
        Assert.Equal("guid: after", File.ReadAllText(metaPath));
        Assert.False(File.Exists(BuildBackupPath(path, options, "success")));
        Assert.False(File.Exists(BuildBackupPath(metaPath, options, "success")));
    }

    [Fact]
    public void RunWithBackup_WhenActionFails_RestoresBodyAndMeta()
    {
        using var temp = new TempDirectory();
        string path = Path.Combine(temp.Path, "Thing.asset");
        string metaPath = path + ".meta";
        File.WriteAllText(path, "before");
        File.WriteAllText(metaPath, "guid: before");

        FileBackupTransactionOptions options = CreateOptions(temp, "middlefail");

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            FileBackupTransaction.RunWithBackup<object?>(
                path,
                "asset-test",
                () =>
                {
                    File.WriteAllText(path, "partial");
                    File.WriteAllText(metaPath, "guid: partial");
                    throw new InvalidOperationException("boom");
                },
                options));

        Assert.Equal("boom", exception.Message);
        Assert.Equal("before", File.ReadAllText(path));
        Assert.Equal("guid: before", File.ReadAllText(metaPath));
        Assert.False(File.Exists(BuildBackupPath(path, options, "middlefail")));
        Assert.False(File.Exists(BuildBackupPath(metaPath, options, "middlefail")));
    }

    [Fact]
    public void RunWithBackup_WhenOriginalMetaIsMissing_RemovesPartialMetaOnFailure()
    {
        using var temp = new TempDirectory();
        string path = Path.Combine(temp.Path, "Thing.asset");
        string metaPath = path + ".meta";
        File.WriteAllText(path, "before");

        FileBackupTransactionOptions options = CreateOptions(temp, "missingmeta");

        Assert.Throws<InvalidOperationException>(() =>
            FileBackupTransaction.RunWithBackup<object?>(
                path,
                "asset-test",
                () =>
                {
                    File.WriteAllText(path, "partial");
                    File.WriteAllText(metaPath, "guid: partial");
                    throw new InvalidOperationException("boom");
                },
                options));

        Assert.Equal("before", File.ReadAllText(path));
        Assert.False(File.Exists(metaPath));
    }

    [Fact]
    public void RunWithMovedBackup_WhenActionFails_RestoresExistingTargetAndDeletesBackups()
    {
        using var temp = new TempDirectory();
        string path = Path.Combine(temp.Path, "Thing.asset");
        string metaPath = path + ".meta";
        File.WriteAllText(path, "before");
        File.WriteAllText(metaPath, "guid: before");

        FileBackupTransactionOptions options = CreateOptions(temp, "movedfail");

        string backupPath = BuildBackupPath(path, options, "movedfail");

        Assert.Throws<InvalidOperationException>(() =>
            FileBackupTransaction.RunWithMovedBackup<object?>(
                path,
                "asset-test",
                () =>
                {
                    Assert.False(File.Exists(path));
                    Assert.True(File.Exists(backupPath));
                    File.WriteAllText(path, "partial");
                    File.WriteAllText(metaPath, "guid: partial");
                    throw new InvalidOperationException("boom");
                },
                options));

        Assert.Equal("before", File.ReadAllText(path));
        Assert.Equal("guid: before", File.ReadAllText(metaPath));
        Assert.False(File.Exists(backupPath));
    }

    [Fact]
    public void RunWithBackup_WhenBackupCreationFails_ThrowsBackupFailed()
    {
        using var temp = new TempDirectory();
        string path = Path.Combine(temp.Path, "Thing.asset");
        File.WriteAllText(path, "before");

        string backupRootFile = Path.Combine(temp.Path, "BackupRootFile");
        File.WriteAllText(backupRootFile, "not a directory");
        var options = new FileBackupTransactionOptions
        {
            BackupIdFactory = () => "backupfail",
            BackupRoot = backupRootFile,
        };

        FileBackupTransactionException exception = Assert.Throws<FileBackupTransactionException>(() =>
            FileBackupTransaction.RunWithBackup(
                path,
                "asset-test",
                () => "unreachable",
                options));

        Assert.Equal(ProtocolConstants.ErrorBackupFailed, exception.ErrorCode);
        Assert.Contains("asset-test backup failed", exception.Message);
    }

    [Fact]
    public void RunWithBackup_WhenRestoreFails_ThrowsBackupRestoreFailedWithBackupLocation()
    {
        using var temp = new TempDirectory();
        string path = Path.Combine(temp.Path, "Thing.asset");
        File.WriteAllText(path, "before");

        FileBackupTransactionOptions options = CreateOptions(temp, "restorefail");

        string backupPath = BuildBackupPath(path, options, "restorefail");

        FileBackupTransactionException exception = Assert.Throws<FileBackupTransactionException>(() =>
            FileBackupTransaction.RunWithBackup<object?>(
                path,
                "asset-test",
                () =>
                {
                    File.WriteAllText(path, "partial");
                    File.Delete(backupPath);
                    throw new InvalidOperationException("boom");
                },
                options));

        Assert.Equal(ProtocolConstants.ErrorBackupRestoreFailed, exception.ErrorCode);
        Assert.Contains(backupPath, exception.Message);
        Assert.Contains("Manual recovery", exception.Message);
    }

    [Fact]
    public void RunWithMovedBackup_WhenMetaBackupCreationFailsAfterBodyMove_RestoresBody()
    {
        using var temp = new TempDirectory();
        string longFileName = new string('a', 244) + ".asset";
        string path = Path.Combine(temp.Path, longFileName);
        string metaPath = path + ".meta";
        File.WriteAllText(path, "before");
        File.WriteAllText(metaPath, "guid: before");

        FileBackupTransactionOptions options = CreateOptions(temp, "x");
        string bodyBackupPath = BuildBackupPath(path, options, "x");

        FileBackupTransactionException exception = Assert.Throws<FileBackupTransactionException>(() =>
            FileBackupTransaction.RunWithMovedBackup(
                path,
                "asset-test",
                () => "unreachable",
                options));

        Assert.Equal(ProtocolConstants.ErrorBackupFailed, exception.ErrorCode);
        Assert.Equal("before", File.ReadAllText(path));
        Assert.Equal("guid: before", File.ReadAllText(metaPath));
        Assert.False(File.Exists(bodyBackupPath));
    }

    [Fact]
    public void RunWithBackup_WhenActionFails_RestoresLastWriteTimes()
    {
        using var temp = new TempDirectory();
        string path = Path.Combine(temp.Path, "Thing.asset");
        string metaPath = path + ".meta";
        File.WriteAllText(path, "before");
        File.WriteAllText(metaPath, "guid: before");
        DateTime bodyLastWriteTime = DateTime.UtcNow.AddDays(-5);
        DateTime metaLastWriteTime = DateTime.UtcNow.AddDays(-4);
        File.SetLastWriteTimeUtc(path, bodyLastWriteTime);
        File.SetLastWriteTimeUtc(metaPath, metaLastWriteTime);

        FileBackupTransactionOptions options = CreateOptions(temp, "mtime");

        Assert.Throws<InvalidOperationException>(() =>
            FileBackupTransaction.RunWithBackup<object?>(
                path,
                "asset-test",
                () =>
                {
                    File.WriteAllText(path, "partial");
                    File.WriteAllText(metaPath, "guid: partial");
                    throw new InvalidOperationException("boom");
                },
                options));

        AssertLastWriteTimeCloseTo(bodyLastWriteTime, File.GetLastWriteTimeUtc(path));
        AssertLastWriteTimeCloseTo(metaLastWriteTime, File.GetLastWriteTimeUtc(metaPath));
    }

    [Fact]
    public void RunWithBackup_WhenBackupRootIsProvided_WritesBackupOutsideAssetFolder()
    {
        using var temp = new TempDirectory();
        string projectRoot = Path.Combine(temp.Path, "Project");
        string assetsRoot = Path.Combine(projectRoot, "Assets");
        Directory.CreateDirectory(assetsRoot);
        string path = Path.Combine(assetsRoot, "Thing.asset");
        File.WriteAllText(path, "before");

        string backupRoot = Path.Combine(temp.Path, "ExternalBackups");
        var options = new FileBackupTransactionOptions
        {
            BackupIdFactory = () => "external",
            BackupRoot = backupRoot,
        };
        string backupPath = BuildBackupPath(path, options, "external");

        string result = FileBackupTransaction.RunWithBackup(
            path,
            "asset-test",
            () =>
            {
                Assert.True(File.Exists(backupPath));
                Assert.StartsWith(Path.GetFullPath(backupRoot), Path.GetFullPath(backupPath));
                File.WriteAllText(path, "after");
                return "ok";
            },
            options);

        Assert.Equal("ok", result);
        Assert.Equal("after", File.ReadAllText(path));
        Assert.False(File.Exists(backupPath));
    }

    [Fact]
    public void RunWithMovedBackup_WhenOriginalMissingAndActionFails_DeletesPartialCreate()
    {
        using var temp = new TempDirectory();
        string path = Path.Combine(temp.Path, "Created.prefab");
        string metaPath = path + ".meta";
        FileBackupTransactionOptions options = CreateOptions(temp, "createfail");

        Assert.Throws<InvalidOperationException>(() =>
            FileBackupTransaction.RunWithMovedBackup<object?>(
                path,
                "prefab-create",
                () =>
                {
                    File.WriteAllText(path, "partial");
                    File.WriteAllText(metaPath, "guid: partial");
                    throw new InvalidOperationException("boom");
                },
                options));

        Assert.False(File.Exists(path));
        Assert.False(File.Exists(metaPath));
    }

    [Fact]
    public void AtomicFileUtility_ReplaceTempFile_ReplacesExistingDestination()
    {
        using var temp = new TempDirectory();
        string destinationPath = Path.Combine(temp.Path, "last-run.json");
        string tempPath = destinationPath + ".tmp";
        File.WriteAllText(destinationPath, "old");
        File.WriteAllText(tempPath, "new");

        AtomicFileUtility.ReplaceTempFile(tempPath, destinationPath);

        Assert.Equal("new", File.ReadAllText(destinationPath));
        Assert.False(File.Exists(tempPath));
    }

    [Fact]
    public void AtomicFileUtility_WriteAllText_CreatesDestinationAndCleansTempFile()
    {
        using var temp = new TempDirectory();
        string destinationPath = Path.Combine(temp.Path, "Library", "last-run.json");

        AtomicFileUtility.WriteAllText(destinationPath, "{\"lastRunId\":\"abc\"}");

        Assert.Equal("{\"lastRunId\":\"abc\"}", File.ReadAllText(destinationPath));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(destinationPath)!, "*.tmp"));
    }

    [Fact]
    public void AtomicFileUtility_CleanupTempFiles_RemovesStaleTmpFilesOnly()
    {
        using var temp = new TempDirectory();
        string staleTempPath = Path.Combine(temp.Path, "run.json.tmp");
        string resultPath = Path.Combine(temp.Path, "run.json");
        File.WriteAllText(staleTempPath, "stale");
        File.WriteAllText(resultPath, "result");

        int deleted = AtomicFileUtility.CleanupTempFiles(temp.Path);

        Assert.Equal(1, deleted);
        Assert.False(File.Exists(staleTempPath));
        Assert.True(File.Exists(resultPath));
    }

    private static FileBackupTransactionOptions CreateOptions(TempDirectory temp, string backupId)
    {
        return new FileBackupTransactionOptions
        {
            BackupIdFactory = () => backupId,
            BackupRoot = Path.Combine(temp.Path, "BackupRoot"),
        };
    }

    private static string BuildBackupPath(string path, FileBackupTransactionOptions options, string backupId)
    {
        return FileBackupTransaction.BuildBackupPath(path, backupId, options.BackupRoot);
    }

    private static void AssertLastWriteTimeCloseTo(DateTime expected, DateTime actual)
    {
        TimeSpan difference = (actual - expected).Duration();
        Assert.True(
            difference <= TimeSpan.FromSeconds(2),
            "Expected last write time close to " + expected.ToString("O") + ", actual " + actual.ToString("O"));
    }
}
