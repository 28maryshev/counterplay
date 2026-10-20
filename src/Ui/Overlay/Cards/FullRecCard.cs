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

public sealed class FullRecCard
{
    public int          ChampionId { get; init; }   // для hover/lock в клиенте
    public string       Rank       { get; init; } = "";
    public string       Name       { get; init; } = "";
    public string       Score      { get; init; } = "";
    public string       ScoreColor { get; init; } = "#C89B3C";
    public string       WinRate    { get; init; } = "";
    public ImageSource? Icon       { get; init; }
    public string       ReasonText { get; init; } = "";
    public IEnumerable<ReasonSeg> ReasonSegs { get; init; } = [];
    // Показатели справа (порядок и цвета — MetricRow.Defs).
    public List<MetricRow> Metrics { get; init; } = [];
    public string       ArchGlyph  { get; init; } = "";
    public string       ArchColor  { get; init; } = "#888888";
    public string       ArchTip    { get; init; } = "";
    public Visibility   ArchVisibility => ArchGlyph.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    // Полоски показателей можно выключить в настройках — карточка становится
    // компактнее, остаются имя, счёт и доводы.
    public Visibility   MetricsVisibility { get; init; } = Visibility.Visible;
    public bool         ArchStrong { get; init; }   // поддерживает стиль команды
    public bool         ArchDim    { get; init; }   // выбивается из стиля
    public double       ArchOpacity   => ArchDim ? 0.75 : 1.0;
    public string       ArchRing      => ArchStrong ? "#F5D77A" : "#00000000";
    public double       ArchRingWidth => ArchStrong ? 2.0 : 0.0;
    public List<string> SynDashes  { get; init; } = []; // цвета связок с союзниками

    // Иконки предметов, которыми враг контрит состав (см. ItemValue.CounterItems).
    // С названием: предметов в таблице два десятка, и по значку узнаётся далеко
    // не каждый — название всплывает при наведении.
    public List<CounterItemVm> CounterItems { get; init; } = [];
    public Visibility CounterItemsVisibility =>
        CounterItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    // Мой уже выбранный чемпион — выделяем карточку и показываем бейдж.
    public bool         IsMyPick   { get; init; }
    public string       MineLabel  { get; init; } = "";
    public Visibility   MineVisibility => IsMyPick ? Visibility.Visible : Visibility.Collapsed;

    // Наведён кликом через интерфейс — вся рамка светится золотом.
    // Толщина рамки у ВСЕХ карточек одинаковая (1.5): разная толщина сдвигала
    // содержимое выбранной карточки на 1px — ряды «плыли» относительно соседних.
    public bool         IsSelected { get; init; }
    // Пик из пула — плашка БЕЗ рамки: обведённой показывается выбранная
    // карточка, и вторая обводка рядом читалась как второй выбор.
    //
    // Цвет говорит, ИЗ КАКОГО пула пик: личный — синий, дуо — бирюзовый, тем же
    // тоном, что пометка дуо в полосе ролей (#3FD9C8). Одно время бирюзовым
    // красили оба, и разницы было не видно: человек с личным пулом получал
    // метку дуо. Бирюзовой заливки чуть больше (0x2E против 0x22) — она светлее
    // и на тёмном фоне слабее себя показывает.
    public string       CardBg     => FromDuoPool ? "#2E3FD9C8"
                                    : FromPool    ? "#22315C8A"
                                    : IsSelected  ? "#2AC89B3C"
                                    : IsMyPick    ? "#1E36D6E7" : "#1EC89B3C";
    public string       CardBorder => IsSelected ? "#F0C24B" : IsMyPick ? "#36D6E7" : "#00000000";

    // Кнопка «не советовать» в углу. Нет её у моего пика и в ARAM: там
    // советы не из подбора по роли, и скрывать нечего.
    public bool         CanDislike { get; init; }
    public string       DislikeTip => Loc.T("dislike.tip", Dislikes.HideGames);

    // Чемпиона нет на аккаунте — красная рамка + надпись «нет чемпиона».
    public bool         NotOwned      { get; init; }
    public string       NotOwnedLabel { get; init; } = "";
    public Visibility   NotOwnedVisibility => NotOwned ? Visibility.Visible : Visibility.Collapsed;

    // Пик из активного пула («ТВОЙ ПУЛ ПРОТИВ ВРАГОВ») — подпись слота.
    public bool         FromPool   { get; init; }
    /// Пул именно ДУО — от этого зависит цвет плашки (бирюза вместо синего).
    public bool         FromDuoPool { get; init; }
    public string       PoolLabel  { get; init; } = "";
    public Visibility   PoolLabelVisibility => FromPool ? Visibility.Visible : Visibility.Collapsed;
    // Дуо: иконка чемпиона друга (кого назвать другу пикнуть) + его имя.
    public ImageSource? DuoIcon    { get; init; }
    public string       DuoName    { get; init; } = "";
    public Visibility   DuoVisibility => DuoIcon != null ? Visibility.Visible : Visibility.Collapsed;
}
