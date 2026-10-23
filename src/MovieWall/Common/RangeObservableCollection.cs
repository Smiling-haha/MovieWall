using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace MovieWall.Common;

/// <summary>
/// 支持批量添加的 ObservableCollection。
/// 扫描时一次进几十条，逐条通知会让虚拟化面板反复重新布局，卡顿肉眼可见；
/// 这里把一批合并成一次 Reset，布局只做一遍。
/// </summary>
public sealed class RangeObservableCollection<T> : ObservableCollection<T>
{
    private bool _suppress;

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (_suppress) return;
        base.OnCollectionChanged(e);
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (_suppress) return;
        base.OnPropertyChanged(e);
    }

    public void AddRange(IEnumerable<T> items)
    {
        var list = items as IList<T> ?? items.ToList();
        if (list.Count == 0) return;

        _suppress = true;
        try
        {
            foreach (var item in list) Items.Add(item);
        }
        finally
        {
            _suppress = false;
        }
        RaiseReset();
    }

    public void ReplaceAll(IEnumerable<T> items)
    {
        var list = items as IList<T> ?? items.ToList();

        _suppress = true;
        try
        {
            Items.Clear();
            foreach (var item in list) Items.Add(item);
        }
        finally
        {
            _suppress = false;
        }
        RaiseReset();
    }

    private void RaiseReset()
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
