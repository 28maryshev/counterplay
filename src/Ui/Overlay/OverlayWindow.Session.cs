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

// Окно драфта — профиль игрока на экране ожидания: ранг, очереди, последние игры.
public partial class OverlayWindow
{
    // ── Трекер сессии на экране ожидания ─────────────────────────────────────
    private SessionTracker.SessionData? _session;

    private SessionTracker.QueueView?   _sessionView; // выбранная очередь

    private string _selectedQueue = "";

    private bool _queueUserSet;   // выбрал руками в этой сессии — авто не перебивает

    private bool _wrChartHooked;

    private static readonly Brush WinBrush  = new SolidColorBrush(Color.FromRgb(0x57, 0xC9, 0x8A));

    private static readonly Brush LossBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0x5B, 0x5B));

    private static readonly Brush MuteBrush = new SolidColorBrush(Color.FromRgb(0x9F, 0xB3, 0xC8));

    // Заполняет панель ранга/последних игр/винрейта. Вызывать из Program после RefreshAsync.
    private string _lastNick = "";

    // ── Когда показывать изменения после игры ─────────────────────────────
    //
    // Клиент отдаёт новые LP уже на экране после матча — и если показать их там,
    // человек ничего не увидит: он смотрит на свою статистику, а карточка
    // тихо перещёлкивается за окном клиента. Поэтому новые данные ЖДУТ: пока
    // идёт послематчевый экран, карточка остаётся прежней, а когда игрок
    // выходит в меню, она разом оживает — полоса едет, цифры считают, ранг
    // меняется со вспышкой.
    private static bool AfterGamePhase(string phase) =>
        phase is "EndOfGame" or "PreEndOfGame" or "WaitingForStats";

    private string _phaseRaw = "";

    private SessionTracker.SessionData? _heldSession;

    public void ShowSession(SessionTracker.SessionData? d) =>
        Dispatcher.InvokeAsync(() =>
        {
            // Идёт послематчевый экран — придержим до выхода в меню. Первый показ
            // (карточки ещё нет) придерживать нечего: человеку нужно видеть хоть
            // что-то.
            if (AfterGamePhase(_phaseRaw) && _lpShown is not null)
            {
                _heldSession = d;
                return;
            }
            // Сменился аккаунт — ручной выбор очереди с прошлого не переносим:
            // у нового аккаунта своя настройка (и своя статистика).
            if (d is not null && d.Nick != _lastNick)
            {
                _lastNick = d.Nick;
                _queueUserSet = false;
            }

            _session = d;
            // Очередь по умолчанию: из настроек, если там выбрана конкретная,
            // иначе автоопределённая (где больше игр). И то и другое отменяется
            // ручным выбором в этой сессии.
            if (!_queueUserSet && d is not null)
            {
                var pref = AppSettings.Current.DefaultQueue;
                _selectedQueue = pref is "solo" or "flex" or "normal" or "aram"
                    ? pref : d.SelectedQueue;
            }
            if (!_wrChartHooked)
            {
                WrChart.SizeChanged += (_, _) => DrawWrChart();
                // Полоса LP тянется по ширине панели — пересчитываем заполнение.
                RankProgressTrack.SizeChanged += (_, _) => ApplyRankProgress();
                _wrChartHooked = true;
            }
            RenderSessionView();
            // Журнал мог измениться: при первом запуске его добивает история
            // клиента уже после показа экрана, и без этого лента «мои чемпионы»
            // стояла пустой до первой смены фазы.
            UpdateMyChampsStrip();
        });

    // Рендер панели для выбранной очереди (Solo/Flex/Normal/ARAM).
    private void RenderSessionView()
    {
        if (_selectedQueue.Length == 0)
            _selectedQueue = SessionTracker.GetSelectedQueue();

        var d = _session;
        var v = d?.Queues.GetValueOrDefault(_selectedQueue);
        if (_emptyProfilePreview) v = null;   // тестовый предпросмотр «нет игр»
        _sessionView = v;

        NickText.Text  = d?.Nick ?? "";
        QueueText.Text = Loc.T("session.queue." + _selectedQueue) + " ▾";

        // По бокам ника — половинки ранговой эмблемы выбранной очереди (крылья).
        // Чем выше ранг — тем больше размах крыльев.
        var halves = v?.HasRank == true ? WingHalves(v.Tier) : null;
        WingLeft.Source  = halves?.Left;
        WingRight.Source = halves?.Right;
        WingLeft.Visibility  = halves != null ? Visibility.Visible : Visibility.Collapsed;
        WingRight.Visibility = halves != null ? Visibility.Visible : Visibility.Collapsed;
        if (halves != null && v != null)
            WingLeft.Height = WingRight.Height = WingHeight(v.Tier);

        if (v is null)
        {
            RankEmblem.Visibility = Visibility.Collapsed;
            RankText.Text = "—";
            RankText.Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xED, 0xF3));
            RankLpText.Text = "";
            _rankPct = 0;
            _lpWin = _lpLoss = 0;
            _lpDivisioned = false;
            _lpLastLost = false;
            _lpShown = null;
            _rankKey = "";
            ApplyRankProgress();
            QueueMapBox.Visibility = Visibility.Collapsed;
            CenterQueueName(false);
            _lpBarShown = true;                   // скелетон: полоса на своём месте
            ApplyLpBarVisibility();
            SessionHint.Text = Loc.T("session.needGames");
            SessionHint.Visibility = Visibility.Visible;
            ShowSkeletons(true);                  // плашки на месте будущих данных
            FillLast5(null);                      // 5 скелетон-ячеек
            SeasonWlText.Text = "";
            WinrateBig.Text = "";
            DrawWrChart();                        // скелетон вместо графика
            return;
        }

        ShowSkeletons(false);

        // Ранг есть только у Solo/Flex; для Normal/ARAM — имя очереди вместо тира.
        // На месте эмблемы там силуэт карты — пустое место выглядело недоделкой.
        // Без ранга бывает двух видов. Нормалы и ARAM — ранга там не бывает:
        // силуэт карты. Соло и флекс без отборочных — ранг будет: скелетон
        // эмблемы.
        var rankedQueue = _selectedQueue is "solo" or "flex";
        QueueMapBox.Visibility = !v.HasRank && !rankedQueue ? Visibility.Visible : Visibility.Collapsed;
        ShowEmblemSkeleton(!v.HasRank && rankedQueue);
        _lpBarShown = v.HasRank;
        ApplyLpBarVisibility();
        if (!v.HasRank && !rankedQueue)
        {
            var aram = _selectedQueue == "aram";
            QueueMap.Source = QueueMapSource(aram ? "abyss" : "rift");
        }
        if (v.HasRank)
        {
            var emblem = RankEmblemSource(v.Tier);
            var tierLoc = LocalizedTier(v.Tier);
            var rankLine = string.IsNullOrEmpty(v.Division) ? tierLoc : $"{tierLoc} {v.Division}";
            var rankBrush = TierBrush(v.Tier);

            // Что именно произошло с прошлого показа: очки внутри дивизиона,
            // переход в соседний или просто первый показ.
            var rankKey = v.Tier + "/" + v.Division;
            var known = _lpShown is { } was && _rankKey.Length > 0;
            var sameRank = rankKey == _rankKey;
            var moved = known && sameRank && _lpShown!.Value != v.Lp;
            // Переход: дивизион сменился. Вверх он или вниз, по очкам не понять
            // (при повышении они падают с 93 до 8), поэтому сравниваем ранги
            // целиком — в сквозной шкале, где дивизион это сотня очков.
            var crossed = known && !sameRank;
            var abs = RankAbs(v.Tier, v.Division, v.Lp);
            var up = crossed && abs > _rankAbs;
            // Новая лига — это смена тира, а не дивизиона: изумруд → алмаз, а не
            // изумруд II → изумруд I. Такое случается раз в десятки игр.
            var prevTier = _rankKey.Split('/')[0];
            var newLeague = crossed && !string.Equals(prevTier, v.Tier,
                                                      StringComparison.OrdinalIgnoreCase);
            _rankKey = rankKey;
            _rankAbs = abs;

            if (crossed)
            {
                // Подпись и эмблему меняем НЕ сейчас: полоса сначала должна
                // дойти до края с прежним рангом — иначе повышение выглядит так,
                // будто новый ранг был всегда.
                AnimateRankCross(up, newLeague, emblem, rankLine, rankBrush, v.Lp, v.ProgressPct);
            }
            else
            {
                RankEmblem.Source = emblem;
                RankEmblem.Visibility = emblem != null ? Visibility.Visible : Visibility.Collapsed;
                RankText.Text = rankLine;
                RankText.Foreground = rankBrush;

                if (moved)
                {
                    AnimateLp(_lpShown!.Value, v.Lp, _pctShown, v.ProgressPct);
                }
                else
                {
                    // Снимаем анимацию — и обязательно опускаем флаг. Иначе так:
                    // очки изменились, анимация пошла, а следом пришло обычное
                    // обновление с теми же очками и остановило её. Completed у
                    // снятой анимации не вызывается, флаг оставался поднятым — и
                    // штриховка с засечками замирали до перезапуска программы,
                    // хотя полоса продолжала двигаться.
                    BeginAnimation(LpProgressProperty, null);
                    _lpMoving = false;
                    RankLpText.Text = $"{v.Lp} LP";
                    _rankPct = v.ProgressPct;
                }
            }
            _lpShown = v.Lp;
            _pctShown = v.ProgressPct;
            // Шкала «сто очков — дивизион» есть только ниже Мастера.
            _lpDivisioned = v.Tier.ToUpperInvariant()
                is not ("MASTER" or "GRANDMASTER" or "CHALLENGER");
            _lpWin = AvgLp(v.Last5, win: true);
            _lpLoss = AvgLp(v.Last5, win: false);
            // Список идёт от свежей игры к старой — последняя игра первая.
            _lpLastLost = v.Last5.Count > 0 && !v.Last5[0].Win;
        }
        else
        {
            RankEmblem.Visibility = Visibility.Collapsed;
            RankText.Text = Loc.T("session.queue." + _selectedQueue);
            RankText.Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xED, 0xF3));
            RankLpText.Text = "";
            _rankPct = 0;
            _lpWin = _lpLoss = 0;
            _lpDivisioned = false;
            _lpLastLost = false;
            _lpShown = null;
            _rankKey = "";
        }
        CenterQueueName(!v.HasRank && !rankedQueue);
        ApplyRankProgress();

        // Свежая установка/очередь: журнал этой очереди пуст — статистика
        // появится после сыгранных при запущенной программе игр. В нормалах и
        // ARAM LP не бывает — и подсказка про него молчит.
        SessionHint.Text = Loc.T(_selectedQueue is "solo" or "flex" ? "session.needGames" : "session.needGamesWr");
        SessionHint.Visibility = v.WinrateHistory.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        FillLast5(v.Last5);

        // W/L (мелко, локализовано) + винрейт (крупно, цвет по правилам)
        var played = v.Wins + v.Losses;
        SeasonWlText.Text = Loc.T("session.wl", v.Wins, v.Losses);
        WinrateBig.Text = played > 0 ? $"{v.Winrate:0}%" : "—";
        WinrateBig.Foreground = played > 0
            ? WinrateBrush(v.Winrate)
            : new SolidColorBrush(Color.FromRgb(0xE6, 0xED, 0xF3));

        DrawWrChart();
    }

    // Клик по чипу очереди — выпадающий список Solo/Flex/Normal/ARAM.
    private void QueueChip_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var menu = new ContextMenu { Style = (Style)FindResource("RoleMenuStyle") };
        var itemStyle = (Style)FindResource("RoleMenuItemStyle");
        foreach (var key in SessionTracker.QueueKeys)
        {
            var item = new MenuItem
            {
                Header    = Loc.T("session.queue." + key),
                IsChecked = key == _selectedQueue,
                Style     = itemStyle,
            };
            var chosen = key;
            item.Click += (_, _) =>
            {
                _selectedQueue = chosen;
                _queueUserSet  = true;
                _ = Task.Run(() => SessionTracker.SetSelectedQueue(chosen)); // персист вне UI-потока
                RenderSessionView();
            };
            menu.Items.Add(item);
        }
        menu.PlacementTarget = QueueChip;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    // Цвет цифры винрейта по заданным порогам.
    // Единая шкала на всё приложение (см. WinrateColor): серый → белый →
    // оранжевый → красный, с плавным переливом между зонами.
    private static Brush WinrateBrush(double wr)
    {
        return WinrateColor.Brush(wr);
    }

    // Локализованное название ранга (ranks.* в i18n; нет перевода — как есть).
    private static string LocalizedTier(string tier)
    {
        var key = "ranks." + tier.ToLowerInvariant();
        var t = Loc.T(key);
        return t == key ? tier : t;
    }

    // Цвет названия ранга по тиру.
    private static Brush TierBrush(string tier) => (tier?.ToUpperInvariant() ?? "") switch
    {
        "IRON"        => new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A)),
        "BRONZE"      => new SolidColorBrush(Color.FromRgb(0xB0, 0x7A, 0x53)),
        "SILVER"      => new SolidColorBrush(Color.FromRgb(0xB6, 0xC2, 0xCC)),
        "GOLD"        => new SolidColorBrush(Color.FromRgb(0xE8, 0xC1, 0x5A)),
        "PLATINUM"    => new SolidColorBrush(Color.FromRgb(0x4F, 0xC7, 0xC7)),
        "EMERALD"     => new SolidColorBrush(Color.FromRgb(0x4F, 0xC7, 0x8A)),
        "DIAMOND"     => new SolidColorBrush(Color.FromRgb(0x6E, 0x9B, 0xE7)),
        "MASTER"      => new SolidColorBrush(Color.FromRgb(0xC0, 0x6E, 0xE0)),
        "GRANDMASTER" => new SolidColorBrush(Color.FromRgb(0xE0, 0x5B, 0x5B)),
        "CHALLENGER"  => new SolidColorBrush(Color.FromRgb(0x54, 0xC7, 0xF0)),
        _             => new SolidColorBrush(Color.FromRgb(0xE6, 0xED, 0xF3)),
    };

    // Эмблема ранга из встроенных ресурсов (assets/ranks/{tier}.png).
    private static readonly Dictionary<string, ImageSource> _emblemCache = new();

    /// <summary>
    /// Скелетон эмблемы для ранговой очереди без ранга: форма золотой эмблемы,
    /// залитая общим бликом скелетонов. Форма — маской по прозрачности картинки,
    /// так что силуэт точный, со всеми крыльями.
    /// </summary>
    private void ShowEmblemSkeleton(bool on)
    {
        if (on && RankEmblemSkel.OpacityMask is null && RankEmblemSource("gold") is { } shape)
        {
            RankEmblemSkel.Fill = Skeleton.Shimmer;
            RankEmblemSkel.OpacityMask = new ImageBrush(shape) { Stretch = Stretch.Uniform };
        }
        RankEmblemSkel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Название нормалов и ARAM — по центру значка карты над ним, а не от края:
    /// значок стоит по центру эмблемы лиги, и надпись от края смотрелась
    /// съехавшей. Ширина надписи зависит от языка; шире значка — от края, как
    /// название лиги.
    /// </summary>
    private void CenterQueueName(bool on)
    {
        double left = 0;
        if (on)
        {
            RankText.Margin = new Thickness(0);
            RankText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var center = QueueMapBox.Margin.Left + QueueMapBox.Width / 2;
            left = Math.Max(0, center - RankText.DesiredSize.Width / 2);
        }
        RankText.Margin = new Thickness(left, 0, 0, 0);
    }

    // Значок карты на сетке 100×100, как в клиенте при выборе режима. Линии —
    // уголки и стрелки толщиной 16, внешние углы чуть скруглены. Топ и бот —
    // уголки слева сверху и справа снизу; мид — две стрелки по диагонали,
    // наконечник — такой же уголок, древко уходит под него.
    private const string MapTop = "M0,4 A4,4 0 0 1 4,0 L44,0 L44,16 L16,16 L16,44 L0,44 Z";
    private const string MapBot = "M100,96 A4,4 0 0 1 96,100 L56,100 L56,84 L84,84 L84,56 L100,56 Z";
    private const string MapMid =
        "M56,0 L96,0 A4,4 0 0 1 100,4 L100,44 L84,44 L84,16 L56,16 Z " +
        "M83.05,7.05 L92.95,16.95 L58.95,50.95 L49.05,41.05 Z " +
        "M44,100 L4,100 A4,4 0 0 1 0,96 L0,56 L16,56 L16,84 L44,84 Z " +
        "M16.95,92.95 L7.05,83.05 L41.05,49.05 L50.95,58.95 Z";

    /// <summary>
    /// Значок карты очереди без ранга, как в клиенте: «rift» — Ущелье
    /// призывателей (три линии: уголки топа и бота и стрелки мида), «abyss» —
    /// Воющая бездна (одна линия — только стрелки). Вектор: чёткий при любом
    /// масштабе экрана.
    /// </summary>
    internal static ImageSource? QueueMapSource(string map)
    {
        var key = "map:" + map;
        if (_emblemCache.TryGetValue(key, out var cached)) return cached;
        var data = map == "abyss" ? MapMid : $"{MapTop} {MapBot} {MapMid}";
        // Ненулевое правило: древко и наконечник сливаются, а не вырезают друг друга.
        var shape = Geometry.Parse("F1 " + data);
        var brush = new SolidColorBrush(Color.FromRgb(0x10, 0xE4, 0xF9));
        var img = new DrawingImage(new GeometryDrawing(brush, null, shape));
        img.Freeze();
        _emblemCache[key] = img;
        return img;
    }

    private static ImageSource? RankEmblemSource(string tier)
    {
        var key = (tier ?? "").ToLowerInvariant();
        if (string.IsNullOrEmpty(key)) return null;
        if (_emblemCache.TryGetValue(key, out var cached)) return cached;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri($"pack://application:,,,/Counterplay;component/assets/ranks/{key}.png");
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            _emblemCache[key] = bmp;
            return bmp;
        }
        catch { return null; }
    }

    // Размах крыльев растёт с рангом: Железо — скромные, Претендент — максимальные.
    private static double WingHeight(string tier) => tier.ToUpperInvariant() switch
    {
        "IRON"        => 14,
        "BRONZE"      => 16,
        "SILVER"      => 18,
        "GOLD"        => 20,
        "PLATINUM"    => 22.5,
        "EMERALD"     => 25,
        "DIAMOND"     => 27.5,
        "MASTER"      => 30,
        "GRANDMASTER" => 32,
        "CHALLENGER"  => 34,
        _             => 20,
    };

    // Половинки эмблемы ранга (левая/правая) — «крылья» вокруг ника, с кэшем.
    private static readonly Dictionary<string, (ImageSource Left, ImageSource Right)> _wingCache = new();

    private static (ImageSource Left, ImageSource Right)? WingHalves(string tier)
    {
        var key = (tier ?? "").ToLowerInvariant();
        if (key.Length == 0) return null;
        if (_wingCache.TryGetValue(key, out var cached)) return cached;
        if (RankEmblemSource(tier!) is not BitmapSource bmp) return null;
        try
        {
            int half = bmp.PixelWidth / 2;
            var left  = new CroppedBitmap(bmp, new Int32Rect(0, 0, half, bmp.PixelHeight));
            var right = new CroppedBitmap(bmp, new Int32Rect(half, 0, bmp.PixelWidth - half, bmp.PixelHeight));
            left.Freeze(); right.Freeze();
            var pair = ((ImageSource)left, (ImageSource)right);
            _wingCache[key] = pair;
            return pair;
        }
        catch { return null; }
    }

    // Ячейка одной игры: иконка чемпиона (рамка по W/L) + LP за игру под ней.
    // Скелетон-режим ready-экрана: вместо цифр — плашки той же формы с бликом.
    // Так экран сразу выглядит собранным, а не «поломанным прочерками».
    private void ShowSkeletons(bool on)
    {
        var v = on ? Visibility.Visible : Visibility.Collapsed;
        // Эмблема — силуэтом эмблемы, тем же, что у ранговой очереди без ранга:
        // «только поставил программу» и «ещё не сыграл отборочные» выглядели по-
        // разному (квадратная плашка и силуэт), хотя ранга не видно в обоих.
        ShowEmblemSkeleton(on);
        RankSkel.Visibility     = v;
        RankLpSkel.Visibility   = v;
        WinrateSkel.Visibility  = v;
        RankSkel.Background     = WlSkelBar.Background =
        WrSkelBar.Background    = RankLpSkel.Background = Skeleton.Shimmer;

        // Настоящие подписи прячем, чтобы они не просвечивали сквозь плашки.
        RankText.Visibility     = on ? Visibility.Hidden : Visibility.Visible;
        RankLpText.Visibility   = on ? Visibility.Hidden : Visibility.Visible;
        SeasonWlText.Visibility = on ? Visibility.Hidden : Visibility.Visible;
        WinrateBig.Visibility   = on ? Visibility.Hidden : Visibility.Visible;
    }

    // Последние 5 игр — всегда пять равных колонок во всю ширину бара.
    // Недосыгранные слоты рисуем пустыми: панель не «прыгает» по высоте и
    // сразу показывает, где появится история.
    private void FillLast5(IReadOnlyList<SessionTracker.RecentGame>? games)
    {
        Last5Panel.Children.Clear();
        Last5Panel.ColumnDefinitions.Clear();
        for (int i = 0; i < 5; i++)
            Last5Panel.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < 5; i++)
        {
            var cell = games != null && i < games.Count
                ? BuildGameCell(games[i])
                : BuildEmptyGameCell();
            Grid.SetColumn(cell, i);
            Last5Panel.Children.Add(cell);
        }
    }

    // Скелетон слота игры: та же геометрия, что у настоящей ячейки, с бликом.
    private FrameworkElement BuildEmptyGameCell()
    {
        var col = new StackPanel
        {
            Margin = new Thickness(2, 0, 2, 0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center
        };
        col.Children.Add(Skeleton.Block(47, 47, 9));
        var lp = Skeleton.Block(22, 8);
        lp.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
        lp.Margin = new Thickness(0, 4, 0, 1);
        col.Children.Add(lp);
        return col;
    }

    private FrameworkElement BuildGameCell(SessionTracker.RecentGame g)
    {
        var col = new StackPanel
        {
            Margin = new Thickness(2, 0, 2, 0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center
        };
        var border = new Border
        {
            Width = 47, Height = 47, CornerRadius = new CornerRadius(9),
            BorderThickness = new Thickness(2),
            BorderBrush = g.Win ? WinBrush : LossBrush,
        };
        var img = IconCache.Get(g.ChampionId);
        if (img != null)
            // Рисунок скругляем собственным клипом — иначе его квадратные углы
            // вылезают за скруглённую рамку и она выглядит разорванной.
            border.Child = new Image
            {
                Source = img, Stretch = Stretch.UniformToFill,
                Width = 43, Height = 43,
                Clip = new RectangleGeometry(new Rect(0, 0, 43, 43), 7, 7),
            };
        col.Children.Add(border);

        var lp = new TextBlock
        {
            FontFamily = (FontFamily)FindResource("UiFont"),
            FontSize = 11, FontWeight = FontWeights.Bold,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new Thickness(0, 3, 0, 0)
        };
        if (g.LpDelta is int lpd)
        {
            // Ноль — это не начисление: так выглядит поражение на 0 LP, когда
            // запас перед вылетом из дивизиона съел потерю. «+0» зелёным читался
            // бы как «дали ноль очков за победу».
            lp.Text = lpd == 0 ? "0" : lpd > 0 ? $"+{lpd}" : lpd.ToString();
            lp.Foreground = lpd == 0 ? MuteBrush : lpd > 0 ? WinBrush : LossBrush;
        }
        else
        {
            // LP нет — показываем исход. В нормалах и ARAM очков не бывает
            // вовсе, и под каждым портретом стояла точка: пусто, хотя что
            // сказать есть. Буквы те, что просил владелец, на всех языках.
            lp.Text = g.Win ? "W" : "L";
            lp.Foreground = g.Win ? WinBrush : LossBrush;
        }
        col.Children.Add(lp);
        return col;
    }
}
