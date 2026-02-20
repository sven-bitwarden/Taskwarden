using Taskwarden.Models;

namespace Taskwarden.Services;

public interface IWorkItemAggregator
{
    Task<IReadOnlyList<WorkItem>> AggregateAsync(CancellationToken cancellationToken = default);
}
