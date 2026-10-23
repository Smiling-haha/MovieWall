using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MovieWall.Common;

/// <summary>
/// 极简 INotifyPropertyChanged 基类。整个项目不引入 MVVM 框架，保持零第三方 UI 依赖。
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}
