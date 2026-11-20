using System.Windows;

namespace Numbering.Revit;

public partial class NumberingWindow : Window
{
    internal NumberingWindow(NumberingViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
