namespace EdgeRetails.Central.Domain.Outbox;

public enum OutboxStatus
{
    PENDING = 0,
    DISPATCHED = 1,
    FAILED = 2,
    DEAD_LETTER = 3
}
