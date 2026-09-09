using Capture.Core.Models;

namespace Capture.Core.Store;

public interface IOpenBatchStore
{
    Task<CaptureBatch> CreateScopedBatchAsync(Guid captureProfileId, string inputChannel, CancellationToken cancellationToken = default);
    Task<CaptureBatch?> GetOpenBatchAsync(Guid captureProfileId, string inputChannel, CancellationToken cancellationToken = default);
    Task SetBatchStateAsync(Guid batchId, BatchState state, CancellationToken cancellationToken = default);
}

public sealed class OpenBatchService(IOpenBatchStore store)
{
    public Task<CaptureBatch?> ResolveAsync(Guid captureProfileId, string inputChannel, CancellationToken cancellationToken = default) =>
        store.GetOpenBatchAsync(captureProfileId, inputChannel, cancellationToken);

    public Task CloseAsync(Guid batchId, CancellationToken cancellationToken = default) =>
        store.SetBatchStateAsync(batchId, BatchState.Closed, cancellationToken);
}
