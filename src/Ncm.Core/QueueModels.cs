namespace Ncm.Core;

public sealed record NcmQueueScanError(string Path, string Message);

public sealed record NcmQueueScanResult(
    IReadOnlyList<string> InputPaths,
    IReadOnlyList<NcmQueueScanError> Errors);

public enum NcmQueueItemStatus
{
    Queued,
    Running,
    Completed,
    Failed,
    Cancelled
}

public sealed record NcmQueueItemResult(
    string InputPath,
    NcmQueueItemStatus Status,
    string? OutputPath = null,
    string? ErrorMessage = null);

public sealed record NcmQueueProgress(
    int Total,
    int Queued,
    int Running,
    int Completed,
    int Failed,
    int Cancelled,
    NcmQueueItemResult? LastItem);

public sealed record NcmQueueResult(IReadOnlyList<NcmQueueItemResult> Items)
{
    public int Total => Items.Count;
    public int Completed => Items.Count(item => item.Status == NcmQueueItemStatus.Completed);
    public int Failed => Items.Count(item => item.Status == NcmQueueItemStatus.Failed);
    public int Cancelled => Items.Count(item => item.Status == NcmQueueItemStatus.Cancelled);
}
