namespace Ncm.Core;

public sealed class NcmQueue
{
    private readonly int _maxConcurrency;

    public NcmQueue(int maxConcurrency = 2)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrency, 1);
        _maxConcurrency = maxConcurrency;
    }

    public async Task<NcmQueueResult> RunAsync(
        IReadOnlyList<string> inputPaths,
        Func<string, CancellationToken, Task<string>> processItemAsync,
        IProgress<NcmQueueProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputPaths);
        ArgumentNullException.ThrowIfNull(processItemAsync);

        var items = CreateItems(inputPaths);
        var gate = new object();
        var reportGate = new object();
        var running = 0;
        var completed = 0;
        var failed = items.Count(item => item.Status == NcmQueueItemStatus.Failed);
        var cancelled = 0;
        var queued = items.Count(item => item.Status == NcmQueueItemStatus.Queued);
        var nextIndex = -1;

        void Report(NcmQueueItemResult? lastItem)
        {
            if (progress is null)
            {
                return;
            }

            lock (reportGate)
            {
                NcmQueueProgress snapshot;
                lock (gate)
                {
                    snapshot = new NcmQueueProgress(
                        items.Count,
                        queued,
                        running,
                        completed,
                        failed,
                        cancelled,
                        lastItem);
                }

                try
                {
                    progress.Report(snapshot);
                }
                catch (Exception)
                {
                }
            }
        }

        Report(null);

        async Task WorkerAsync()
        {
            while (true)
            {
                int index;
                NcmQueueItemResult started;
                lock (gate)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    index = Interlocked.Increment(ref nextIndex);
                    if (index >= items.Count)
                    {
                        return;
                    }

                    if (items[index].Status != NcmQueueItemStatus.Queued)
                    {
                        continue;
                    }

                    queued--;
                    running++;
                    started = items[index] = items[index] with { Status = NcmQueueItemStatus.Running };
                }

                Report(started);

                NcmQueueItemResult finished;
                try
                {
                    var outputPath = await processItemAsync(started.InputPath, cancellationToken).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(outputPath))
                    {
                        throw new InvalidOperationException("处理回调未返回输出路径。");
                    }

                    finished = started with
                    {
                        Status = NcmQueueItemStatus.Completed,
                        OutputPath = Path.GetFullPath(outputPath),
                        ErrorMessage = null
                    };
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    finished = started with { Status = NcmQueueItemStatus.Cancelled };
                }
                catch (Exception exception)
                {
                    finished = cancellationToken.IsCancellationRequested
                        ? started with { Status = NcmQueueItemStatus.Cancelled }
                        : started with { Status = NcmQueueItemStatus.Failed, ErrorMessage = exception.Message };
                }

                lock (gate)
                {
                    running--;
                    switch (finished.Status)
                    {
                        case NcmQueueItemStatus.Completed:
                            completed++;
                            break;
                        case NcmQueueItemStatus.Failed:
                            failed++;
                            break;
                        case NcmQueueItemStatus.Cancelled:
                            cancelled++;
                            break;
                    }

                    items[index] = finished;
                }

                Report(finished);
            }
        }

        var workerCount = Math.Min(_maxConcurrency, Math.Max(1, items.Count));
        var workers = Enumerable.Range(0, workerCount).Select(_ => WorkerAsync()).ToArray();
        await Task.WhenAll(workers).ConfigureAwait(false);

        List<NcmQueueItemResult> cancelledItems = [];
        lock (gate)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                for (var index = 0; index < items.Count; index++)
                {
                    if (items[index].Status == NcmQueueItemStatus.Queued)
                    {
                        var item = items[index] = items[index] with { Status = NcmQueueItemStatus.Cancelled };
                        queued--;
                        cancelled++;
                        cancelledItems.Add(item);
                    }
                }
            }
        }

        foreach (var item in cancelledItems)
        {
            Report(item);
        }

        Report(null);
        lock (gate)
        {
            return new NcmQueueResult(items.ToArray());
        }
    }

    private static List<NcmQueueItemResult> CreateItems(IReadOnlyList<string> inputPaths)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<NcmQueueItemResult>(inputPaths.Count);
        foreach (var inputPath in inputPaths)
        {
            if (string.IsNullOrWhiteSpace(inputPath))
            {
                items.Add(new NcmQueueItemResult(inputPath ?? string.Empty, NcmQueueItemStatus.Failed, ErrorMessage: "输入路径为空。"));
                continue;
            }

            try
            {
                var fullPath = Path.GetFullPath(inputPath);
                if (seen.Add(fullPath))
                {
                    items.Add(new NcmQueueItemResult(fullPath, NcmQueueItemStatus.Queued));
                }
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                items.Add(new NcmQueueItemResult(inputPath, NcmQueueItemStatus.Failed, ErrorMessage: exception.Message));
            }
        }

        return items;
    }
}
