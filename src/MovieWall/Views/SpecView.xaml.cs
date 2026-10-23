using System.Windows;
using System.Windows.Controls;
using MovieWall.ViewModels;

namespace MovieWall.Views;

public partial class SpecView : UserControl
{
    public SpecView()
    {
        InitializeComponent();
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private void OnBackClick(object sender, RoutedEventArgs e) => Vm?.BackToLibrary();

    private void OnCloseClick(object sender, RoutedEventArgs e) => Vm?.BackToDetail();

    private void OnRevealClick(object sender, RoutedEventArgs e)
    {
        if (Vm?.CurrentMovie is { } m) Vm.RevealInExplorer(m);
    }

    private async void OnReproceClick(object sender, RoutedEventArgs e)
    {
        if (Vm?.CurrentMovie is { } m) await Vm.ReproceAsync(m);
    }
}
