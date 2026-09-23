namespace EdgeRetails.Worker.Jobs;

public interface IWorkerJob
{
    string Name { get; }
    bool IsExclusive { get; }
    TimeSpan Interval { get; }
    Task ExecuteAsync(CancellationToken cancellationToken);
}
