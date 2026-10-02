using System.Windows;

namespace EdgeRetails.Recovery;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length >= 2 && string.Equals(e.Args[0], "--provision", StringComparison.Ordinal))
        {
            try
            {
                RecoveryGovernanceAuthority.ProvisionPublicTrustAndRestartServer(e.Args[1]);
                MessageBox.Show("Recovery governance public trust was provisioned. The Shop Server restarted and verified readiness.",
                    "Recovery authority ready", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch
            {
                MessageBox.Show("Provisioning or the Shop Server restart did not complete. Recovery remains disabled; verify the Server and governance configuration before retrying.",
                    "Recovery authority not ready", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
                return;
            }
            Shutdown(0);
            return;
        }

        MainWindow = new MainWindow();
        MainWindow.Show();
    }
}
