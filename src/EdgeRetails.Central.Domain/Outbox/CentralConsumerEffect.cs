namespace EdgeRetails.Central.Domain.Outbox;
// The isolated foundation consumer's durable effect IS this recorded notification.
// MessageId is both inbox identity and observable effect, atomically inserted.
public sealed class CentralConsumerEffect
{
    public Guid MessageId { get; set; }
    public string EventType { get; set; } = "";
    public string AggregateId { get; set; } = "";
    public string PayloadJson { get; set; } = "";
}
