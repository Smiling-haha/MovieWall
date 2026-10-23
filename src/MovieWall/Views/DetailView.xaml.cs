using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MovieWall.ViewModels;

namespace MovieWall.Views;

public partial class DetailView : UserControl
{
    public DetailView()
    {
        InitializeComponent();

        PosterHost.SizeChanged += (_, _) =>
        {
            var w = PosterHost.ActualWidth;
            var h = PosterHost.ActualHeight;
            if (w > 0 && h > 0)
                PosterHost.Clip = new RectangleGeometry(new Rect(0, 0, w, h), 4, 4);
        };
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private void OnBackClick(object sender, RoutedEventArgs e) => Vm?.BackToLibrary();

    private void OnPlayClick(object sender, RoutedEventArgs e)
    {
        if (Vm?.CurrentMovie is { } m) Vm.Play(m);
    }

    private async void OnFavoriteClick(object sender, RoutedEventArgs e)
    {
        if (Vm?.CurrentMovie is { } m) await Vm.ToggleFavoriteAsync(m);
    }

    private void OnSpecClick(object sender, RoutedEventArgs e) => Vm?.OpenSpec();

    private void OnRevealClick(object sender, RoutedEventArgs e)
    {
        if (Vm?.CurrentMovie is { } m) Vm.RevealInExplorer(m);
    }

    private async void OnRescrapeClick(object sender, RoutedEventArgs e)
    {
        if (Vm?.CurrentMovie is { } m) await Vm.RescrapeAsync(m);
    }
}
