namespace EdgeRetails.Desktop.Services;

public interface ILiveClock : IDisposable
{
    event EventHandler<DateTimeOffset>? Tick;

    DateTimeOffset Now { get; }
}
