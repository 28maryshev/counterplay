using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Image = System.Windows.Controls.Image;
using FontFamily = System.Windows.Media.FontFamily;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace Counterplay;

/// <summary>Ячейка тир-листа (лучшие по WR на роль) под банами.</summary>
public sealed class TierCell : System.ComponentModel.INotifyPropertyChanged
{
    public int          ChampionId { get; init; }
    public ImageSource? Icon       { get; init; }
    public string       WrText     { get; init; } = "";
    public string       WrBrush    { get; init; } = "#8AA0B2";
    public string       Grade      { get; init; } = "";   // OP/S/A/B/C
    public string       GradeColor { get; init; } = "#C89B3C"; // цвет эмблемы и буквы
    public string       Tip        { get; init; } = "";
    // У WR-столбца грейдов нет: скрываем бейдж, рамку красим нейтрально.
    public bool         ShowGrade  { get; init; } = true;
    public Visibility   GradeVisibility => ShowGrade ? Visibility.Visible : Visibility.Collapsed;
    // Чемпиона нет на аккаунте — гасим и обводим красным: видно, что он есть в
    // роли, но взять его нельзя.
    public bool         NotOwned   { get; init; }
    public double       CellOpacity => NotOwned ? 0.45 : 1.0;

    /// <summary>
    /// Чемпион из МОЕЙ половины активного пула.
    ///
    /// Цвет бирюзовый нарочно: золото занято грейдами тир-листа, красный —
    /// «нет на аккаунте». Бирюза ни с чем не спорит и читается отдельным
    /// признаком — «это твоё по пулу», а не «это сильно».
    /// </summary>
    public bool         InPool     { get; init; }
    public string       FrameColor => NotOwned ? "#F0402F"
                                    : InPool   ? "#3FD9C8"
                                    : ShowGrade ? GradeColor : "#3A4B5F";
    /// Подложка — та же бирюза, еле заметной заливкой.
    public string       PoolTint   => InPool ? "#593FD9C8" : "#00000000";

    // Чемпион уже забанен в этом драфте — помечаем оверлеем (обновляется по ходу).
    private bool _banned;
    public bool Banned
    {
        get => _banned;
        set
        {
            if (_banned == value) return;
            _banned = value;
            PropertyChanged?.Invoke(this, new(nameof(Banned)));
            PropertyChanged?.Invoke(this, new(nameof(BannedVisibility)));
        }
    }
    public Visibility BannedVisibility => _banned ? Visibility.Visible : Visibility.Collapsed;
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}
