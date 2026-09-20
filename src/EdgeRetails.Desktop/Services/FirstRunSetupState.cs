namespace EdgeRetails.Desktop.Services;

public interface IFirstRunSetupState
{
    bool IsSetupRequired { get; }

    void MarkSetupCompleted();
}

public sealed class DemoFirstRunSetupState : IFirstRunSetupState
{
    public DemoFirstRunSetupState(bool isSetupRequired)
    {
        IsSetupRequired = isSetupRequired;
    }

    public bool IsSetupRequired { get; private set; }

    public void MarkSetupCompleted()
    {
        IsSetupRequired = false;
    }
}
