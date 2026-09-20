namespace EdgeRetails.Domain.Common;

public abstract class Entity
{
    protected Entity()
    {
        Id = Guid.CreateVersion7();
    }

    public Guid Id { get; set; }
}
