using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;

namespace EdgeRetails.Desktop;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.P ||
            (Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
        {
            return;
        }

        e.Handled = true;
        OpenWindowsPrintDialog();
    }

    private void PrintCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        => OpenWindowsPrintDialog();

    private static void OpenWindowsPrintDialog()
    {
        var dialog = new PrintDialog();
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var queueName = dialog.PrintQueue?.FullName ?? "Selected Windows printer";
        var document = CreatePrintVerificationDocument(queueName);
        dialog.PrintDocument(
            ((IDocumentPaginatorSource)document).DocumentPaginator,
            "Edge Retails Print Verification");
    }

    private static FlowDocument CreatePrintVerificationDocument(string printerName)
    {
        var document = new FlowDocument
        {
            PagePadding = new Thickness(48),
            ColumnGap = 0,
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
            FontSize = 12
        };

        document.Blocks.Add(new Paragraph(new Run("Edge Retails Print Verification"))
        {
            FontSize = 20,
            FontWeight = FontWeights.Bold
        });
        document.Blocks.Add(new Paragraph(new Run(
            "This page verifies the Windows print dialog and spool submission path. " +
            "It is not evidence of physical 58 mm or 80 mm thermal-printer output.")));
        document.Blocks.Add(new Paragraph(new Run($"Selected printer: {printerName}")));
        document.Blocks.Add(new Paragraph(new Run($"Generated: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}")));
        document.Blocks.Add(new Paragraph(new Run(
            "For software verification, Microsoft Print to PDF may be selected when no physical printer is connected.")));

        return document;
    }
}
