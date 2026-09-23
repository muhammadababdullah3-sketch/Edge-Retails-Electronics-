using System.Windows;
using System.Windows.Documents;

namespace EdgeRetails.Desktop.Production.Printing;

public partial class ProductionPrintPreviewWindow : Window
{
    public ProductionPrintPreviewWindow(FlowDocument document, string documentNumber)
    {
        InitializeComponent();
        Title = $"Print Preview - {documentNumber}";
        PreviewViewer.Document = document;
    }

    private void Print_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
