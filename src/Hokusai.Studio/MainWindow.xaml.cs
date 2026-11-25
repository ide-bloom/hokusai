using System.Windows;

namespace Hokusai.Studio;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (DataContext is MainViewModel vm && vm.OpenWorkspace.CanExecute(null))
            {
                vm.OpenWorkspace.Execute(null);
            }
        };
    }
}
