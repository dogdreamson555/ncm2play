using System.Collections.Concurrent;
using System.Diagnostics;
using Ncm.Core;

namespace Ncm.Core.Tests;

public sealed class QueueTests
{
    [Fact]
    public async Task ScanRecursesFiltersExtensionAndDeduplicatesNormalizedPaths()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var nestedDirectory = Directory.CreateDirectory(Path.Combine(temporaryDirectory.FullName, "nested", "deeper")).FullName;
        var first = Path.Combine(nestedDirectory, "first.NCM");
        var second = Path.Combine(temporaryDirectory.FullName, "second.ncm");
        var ignored = Path.Combine(temporaryDirectory.FullName, "ignored.txt");
        File.WriteAllText(first, "sample");
        File.WriteAllText(second, "sample");
        File.WriteAllText(ignored, "sample");

        var result = await NcmQueueScanner.ScanAsync(
        [
            temporaryDirectory.FullName,
            first,
            Path.Combine(nestedDirectory, ".", "first.NCM"),
            ignored
        ]);

        Assert.Equal(2, result.InputPaths.Count);
        Assert.Contains(Path.GetFullPath(first), result.InputPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(Path.GetFullPath(second), result.InputPaths, StringComparer.OrdinalIgnoreCase);
        Assert.All(result.InputPaths, path => Assert.True(Path.IsPathFullyQualified(path)));
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task ScanAsyncHonorsCancellationBetweenInputPaths()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var inputPath = Path.Combine(temporaryDirectory.FullName, "first.ncm");
        File.WriteAllText(inputPath, "sample");
        using var cancellation = new CancellationTokenSource();

        IEnumerable<string> Paths()
        {
            yield return temporaryDirectory.FullName;
            cancellation.Cancel();
            yield return inputPath;
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await NcmQueueScanner.ScanAsync(Paths(), cancellation.Token)
                .WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ScanDoesNotTraverseNestedOrRootDirectoryJunctions()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var root = Directory.CreateDirectory(Path.Combine(temporaryDirectory.FullName, "root")).FullName;
        var nested = Directory.CreateDirectory(Path.Combine(root, "nested")).FullName;
        var external = Directory.CreateDirectory(Path.Combine(temporaryDirectory.FullName, "external")).FullName;
        var normalFile = Path.Combine(root, "normal.ncm");
        var linkedFile = Path.Combine(external, "linked.ncm");
        File.WriteAllText(normalFile, "sample");
        File.WriteAllText(linkedFile, "sample");

        var nestedLink = Path.Combine(nested, "outside");
        var rootLink = Path.Combine(temporaryDirectory.FullName, "root-link");
        CreateDirectoryLink(nestedLink, external);
        CreateDirectoryLink(rootLink, external);

        try
        {
            var nestedResult = await NcmQueueScanner.ScanAsync([root]);
            Assert.Contains(Path.GetFullPath(normalFile), nestedResult.InputPaths, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain(nestedResult.InputPaths, path =>
                string.Equals(path, Path.GetFullPath(linkedFile), StringComparison.OrdinalIgnoreCase));
            Assert.Contains(nestedResult.Errors, error =>
                string.Equals(error.Path, nestedLink, StringComparison.OrdinalIgnoreCase));

            var rootResult = await NcmQueueScanner.ScanAsync([rootLink]);
            Assert.Empty(rootResult.InputPaths);
            Assert.Contains(rootResult.Errors, error =>
                string.Equals(error.Path, Path.GetFullPath(rootLink), StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            DeleteDirectoryLink(rootLink);
            DeleteDirectoryLink(nestedLink);
        }
    }

    [Fact]
    public async Task RunAsyncHonorsConcurrencyLimitAndReportsProgress()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var inputPaths = Enumerable.Range(0, 9)
            .Select(index => Path.Combine(temporaryDirectory.FullName, $"{index}.ncm"))
            .ToArray();
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var progressSnapshots = new ConcurrentQueue<NcmQueueProgress>();
        var progress = new InlineProgress<NcmQueueProgress>(snapshot => progressSnapshots.Enqueue(snapshot));
        var active = 0;
        var maximumActive = 0;

        var runTask = new NcmQueue(maxConcurrency: 3).RunAsync(
            inputPaths,
            async (path, cancellationToken) =>
            {
                var current = Interlocked.Increment(ref active);
                UpdateMaximum(ref maximumActive, current);
                if (current == 3)
                {
                    allStarted.TrySetResult();
                }

                try
                {
                    await release.Task.WaitAsync(cancellationToken);
                    return Path.ChangeExtension(path, ".mp3");
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            },
            progress);

        await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        release.TrySetResult();
        var result = await runTask;
        var snapshots = progressSnapshots.ToArray();

        Assert.Equal(3, maximumActive);
        Assert.Equal(inputPaths.Length, result.Completed);
        Assert.Equal(inputPaths.Length, result.Total);
        Assert.Contains(snapshots, snapshot => snapshot.Total == inputPaths.Length && snapshot.Running == 3);
        Assert.Equal(0, snapshots[^1].Queued);
        Assert.Equal(inputPaths.Length, snapshots[^1].Completed);
    }

    [Fact]
    public async Task RunAsyncSerializesConcurrentProgressReportsAndKeepsCountsMonotonic()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var inputPaths = Enumerable.Range(0, 4)
            .Select(index => Path.Combine(temporaryDirectory.FullName, $"{index}.ncm"))
            .ToArray();
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allProcessed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseItems = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCompletionReport = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseFirstReport = new ManualResetEventSlim();
        var progressSnapshots = new ConcurrentQueue<NcmQueueProgress>();
        var started = 0;
        var processed = 0;
        var activeReports = 0;
        var maximumActiveReports = 0;
        var reportTimedOut = 0;
        var progress = new InlineProgress<NcmQueueProgress>(snapshot =>
        {
            var current = Interlocked.Increment(ref activeReports);
            UpdateMaximum(ref maximumActiveReports, current);
            progressSnapshots.Enqueue(snapshot);

            try
            {
                if (snapshot.Completed > 0 && firstCompletionReport.TrySetResult())
                {
                    if (!releaseFirstReport.Wait(TimeSpan.FromSeconds(5)))
                    {
                        Interlocked.Exchange(ref reportTimedOut, 1);
                    }
                }
            }
            finally
            {
                Interlocked.Decrement(ref activeReports);
            }
        });

        var runTask = new NcmQueue(maxConcurrency: inputPaths.Length).RunAsync(
            inputPaths,
            async (path, cancellationToken) =>
            {
                if (Interlocked.Increment(ref started) == inputPaths.Length)
                {
                    allStarted.TrySetResult();
                }

                await releaseItems.Task.WaitAsync(cancellationToken);
                if (Interlocked.Increment(ref processed) == inputPaths.Length)
                {
                    allProcessed.TrySetResult();
                }

                return Path.ChangeExtension(path, ".mp3");
            },
            progress);

        await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        releaseItems.TrySetResult();
        await allProcessed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await firstCompletionReport.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        releaseFirstReport.Set();

        var result = await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        var snapshots = progressSnapshots.ToArray();

        Assert.Equal(inputPaths.Length, result.Completed);
        Assert.Equal(1, maximumActiveReports);
        Assert.Equal(0, reportTimedOut);
        Assert.NotEmpty(snapshots);
        for (var index = 0; index < snapshots.Length; index++)
        {
            var snapshot = snapshots[index];
            Assert.Equal(snapshot.Total, snapshot.Queued + snapshot.Running + snapshot.Completed + snapshot.Failed + snapshot.Cancelled);
            if (index == 0)
            {
                continue;
            }

            var previous = snapshots[index - 1];
            Assert.True(snapshot.Queued <= previous.Queued);
            Assert.True(snapshot.Completed >= previous.Completed);
            Assert.True(snapshot.Failed >= previous.Failed);
            Assert.True(snapshot.Cancelled >= previous.Cancelled);
        }
    }

    [Fact]
    public async Task RunAsyncContinuesAfterAnItemFails()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var inputPaths = new[] { "first.ncm", "broken.ncm", "last.ncm" }
            .Select(name => Path.Combine(temporaryDirectory.FullName, name))
            .ToArray();
        var processed = new List<string>();
        var gate = new object();

        var result = await new NcmQueue(maxConcurrency: 1).RunAsync(
            inputPaths,
            (path, _) =>
            {
                lock (gate)
                {
                    processed.Add(path);
                }

                return Path.GetFileName(path).Equals("broken.ncm", StringComparison.OrdinalIgnoreCase)
                    ? Task.FromException<string>(new IOException("simulated failure"))
                    : Task.FromResult(Path.ChangeExtension(path, ".mp3"));
            });

        Assert.Equal(inputPaths, processed);
        Assert.Equal(2, result.Completed);
        Assert.Equal(1, result.Failed);
        Assert.Equal("simulated failure", Assert.Single(result.Items, item => item.Status == NcmQueueItemStatus.Failed).ErrorMessage);
        Assert.Equal(NcmQueueItemStatus.Completed, result.Items[2].Status);
    }

    [Fact]
    public async Task RunAsyncCancelsActiveAndUnstartedItemsButKeepsCompletedItems()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var inputPaths = Enumerable.Range(0, 4)
            .Select(index => Path.Combine(temporaryDirectory.FullName, $"{index}.ncm"))
            .ToArray();
        var activeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var progressSnapshots = new ConcurrentQueue<NcmQueueProgress>();
        var progress = new InlineProgress<NcmQueueProgress>(snapshot => progressSnapshots.Enqueue(snapshot));
        using var cancellation = new CancellationTokenSource();

        var runTask = new NcmQueue(maxConcurrency: 2).RunAsync(
            inputPaths,
            async (path, cancellationToken) =>
            {
                if (path == inputPaths[0])
                {
                    return Path.ChangeExtension(path, ".mp3");
                }

                activeStarted.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return Path.ChangeExtension(path, ".mp3");
            },
            progress,
            cancellation.Token);

        await activeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var result = await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        var snapshots = progressSnapshots.ToArray();

        Assert.Equal(NcmQueueItemStatus.Completed, result.Items[0].Status);
        Assert.Equal(Path.ChangeExtension(inputPaths[0], ".mp3"), result.Items[0].OutputPath);
        Assert.All(result.Items.Skip(1), item => Assert.Equal(NcmQueueItemStatus.Cancelled, item.Status));
        Assert.Equal(3, result.Cancelled);
        Assert.Equal(0, snapshots[^1].Queued);
        Assert.Equal(3, snapshots[^1].Cancelled);
    }

    private static void UpdateMaximum(ref int maximum, int value)
    {
        while (true)
        {
            var current = Volatile.Read(ref maximum);
            if (current >= value || Interlocked.CompareExchange(ref maximum, value, current) == current)
            {
                return;
            }
        }
    }

    private static void CreateDirectoryLink(string linkPath, string targetPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return;
        }

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{linkPath}\" \"{targetPath}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        }) ?? throw new InvalidOperationException("无法启动 cmd.exe 创建目录联接点。");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"无法创建目录联接点：{output}{error}");
    }

    private static void DeleteDirectoryLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.Directory) != 0)
        {
            Directory.Delete(path, recursive: false);
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string FullName { get; } = Directory.CreateTempSubdirectory().FullName;

        public void Dispose()
        {
            if (Directory.Exists(FullName))
            {
                Directory.Delete(FullName, recursive: true);
            }
        }
    }
}
