namespace EdgeRetails.Desktop.Services;

public interface IFirstRunSetupState
{
    bool IsSetupRequired { get; }

    void MarkSetupCompleted();
}

public sealed class DemoFirstRunSetupState(bool isSetupRequired) : IFirstRunSetupState
{
    public bool IsSetupRequired { get; private set; } = isSetupRequired;

    public void MarkSetupCompleted()
    {
        IsSetupRequired = false;
    }
}
