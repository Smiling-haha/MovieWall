using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MovieWall.Models;
using MovieWall.ViewModels;

namespace MovieWall.Views;

public partial class LibraryView : UserControl
{
    public LibraryView()
    {
        InitializeComponent();
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private void OnCardClicked(object? sender, Movie movie) => Vm?.OpenDetail(movie);

    private async void OnRescanClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;

        // 扫描中再点就是停止。以前这里静默切换，用户看不出区别，
        // 现在每一步都给明确反馈，点了必然有东西发生。
        if (Vm.IsScanning)
        {
            Vm.CancelScan();
            Vm.ShowToast("已停止扫描");
            return;
        }

        Vm.ShowToast("正在扫描磁盘…");
        await Vm.ScanAllAsync();
        Vm.ShowToast($"扫描完成 · 共 {Vm.AllCount} 部");
    }

    private void OnAllFilterClick(object sender, RoutedEventArgs e) { if (Vm is not null) Vm.Filter = LibraryFilter.All; }
    private void OnLocalFilterClick(object sender, RoutedEventArgs e) { if (Vm is not null) Vm.Filter = LibraryFilter.Local; }
    private void OnExternalFilterClick(object sender, RoutedEventArgs e) { if (Vm is not null) Vm.Filter = LibraryFilter.External; }
    private void OnFavoriteFilterClick(object sender, RoutedEventArgs e) { if (Vm is not null) Vm.Filter = LibraryFilter.Favorite; }

    private void OnAllDiskClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        Vm.DiskFilter = null;
    }

    private void OnDiskChipClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        if (sender is Button { Tag: string root })
            Vm.DiskFilter = string.Equals(Vm.DiskFilter, root, StringComparison.OrdinalIgnoreCase) ? null : root;
    }

    private void OnSortClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        Vm.SortMode = Vm.SortMode switch
        {
            SortMode.Title => SortMode.Year,
            SortMode.Year => SortMode.Rating,
            SortMode.Rating => SortMode.Size,
            SortMode.Size => SortMode.Recent,
            _ => SortMode.Title
        };
    }

    /// <summary>顶栏搜索快捷键把焦点送进来。</summary>
    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    /// <summary>Esc 在片库页时清空搜索。</summary>
    public bool ClearSearchIfAny()
    {
        if (string.IsNullOrEmpty(SearchBox.Text)) return false;
        SearchBox.Clear();
        return true;
    }
}
