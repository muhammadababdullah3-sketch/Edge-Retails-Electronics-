using System.Windows;
using System.Windows.Controls;
using EdgeRetails.Desktop.ViewModels;

namespace EdgeRetails.Desktop.Views;

public partial class ProductManagementView : UserControl
{
    public ProductManagementView()
    {
        InitializeComponent();
    }

    private void OnTaxonomyTreeSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is ProductManagementViewModel vm && e.NewValue is TaxonomyTreeNodeViewModel node)
        {
            vm.SelectedTaxonomyNode = node;
        }
    }
}
