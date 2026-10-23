using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace MovieWall.Converters;

/// <summary>字符之间插入细空格，用来模拟设计稿里的宽字距小标签。</summary>
public sealed class LetterSpaceConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var s = value as string ?? "";
        if (s.Length == 0) return s;

        var n = 1;
        if (parameter is string p && int.TryParse(p, out var parsed)) n = Math.Clamp(parsed, 0, 4);
        if (n == 0) return s;

        var gap = new string('\u2009', n); // thin space
        return string.Join(gap, s.ToCharArray());
    }

    public object ConvertBack(object value, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>null 或空串 → Collapsed。</summary>
public sealed class EmptyToCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var empty = value is null
                    || (value is string s && string.IsNullOrWhiteSpace(s))
                    || (value is int i && i <= 0)
                    || (value is double d && d <= 0);
        return empty ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>有值 → Collapsed（反向版）。</summary>
public sealed class NotEmptyToCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var empty = value is null
                    || (value is string s && string.IsNullOrWhiteSpace(s))
                    || (value is int i && i <= 0);
        return empty ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>bool 取反。</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : true;

    public object ConvertBack(object value, Type t, object p, CultureInfo c)
        => value is bool b ? !b : true;
}
