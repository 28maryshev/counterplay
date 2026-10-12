using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using ComboBox = System.Windows.Controls.ComboBox;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Image = System.Windows.Controls.Image;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace Counterplay;

// Окно пулов — винрейты пулов за период: одиночные и с напарником.
public sealed partial class PoolSettingsWindow
{
    // Лента «мой винрейт по чемпионам»: ранкед (соло+флекс) и нормалы отдельно,
    // ARAM не в счёт. Сортировка по числу игр — сверху те, на ком реально играют.
    private readonly Grid _wrStrip = new() { Margin = new Thickness(0, 0, 0, 8) };

    // Винрейты стоят ДВУМЯ КОЛОНКАМИ, ровно под пулами: слева личное, справа
    // связки. Одно под другим читалось хуже — глаз ищет винрейт под своей
    // половиной, а не в столбик через весь низ окна.
    private readonly DockPanel _wrSolo = new();

    private readonly DockPanel _wrDuo  = new();

    private void RefreshWinrates()
    {
        _wrSolo.Children.Clear();
        _wrDuo.Children.Clear();
        _wrPeriodSolo.Children.Clear();
        _wrPeriodDuo.Children.Clear();
        FillPeriodButtons(_wrPeriodSolo, duo: false);
        FillPeriodButtons(_wrPeriodDuo,  duo: true);

        // Каркас собираем ОДИН раз. Пересобирать его на каждом обновлении
        // нельзя: колонки остаются детьми прежней сетки, и WPF отвечает
        // «элемент уже вложен в другой» — ровно на этом падало переключение
        // периода.
        if (_wrStrip.Children.Count == 0)
        {
            var g = new Grid();
            // Те же доли и та же черта, что у пулов выше, — колонки совпадают.
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // Кнопки стоят В СВОЕЙ колонке, над её заголовком: период у
            // половин теперь разный, и общий ряд наверху врал бы про обе.
            var left = new DockPanel();
            DockPanel.SetDock(_wrPeriodSolo, Dock.Top);
            left.Children.Add(_wrPeriodSolo);
            left.Children.Add(_wrSolo);      // остаток высоты — ленте
            Grid.SetColumn(left, 0);
            g.Children.Add(left);

            var line = new Border
            {
                Width = 1, Background = new SolidColorBrush(Line),
                Margin = new Thickness(14, 0, 14, 0)
            };
            Grid.SetColumn(line, 1);
            g.Children.Add(line);

            var right = new DockPanel();
            DockPanel.SetDock(_wrPeriodDuo, Dock.Top);
            right.Children.Add(_wrPeriodDuo);
            right.Children.Add(_wrDuo);      // остаток высоты — ленте
            Grid.SetColumn(right, 2);
            g.Children.Add(right);

            _wrStrip.Children.Add(g);
        }

        RefreshSoloWinrates();
        RefreshDuoWinrates();
    }

    /// Выбранное окно в днях для своей половины; 0 — за всё время.
    private static int SoloDays =>
        AppSettings.Current.WinratesAllTime ? 0 : SessionTracker.RecentDays;

    private static int DuoDays =>
        AppSettings.Current.DuoWinratesAllTime ? 0 : SessionTracker.RecentDays;

    /// <summary>
    /// Переключатель периода — один на обе половины.
    ///
    /// Раньше слева считался месяц, справа всё время, и это только называлось
    /// в подписях. Теперь период общий и выбирается здесь, а подписи колонок
    /// стали короче: период стоит над ними и не повторяется дважды.
    /// </summary>
    private static StackPanel PeriodRow() => new()
    {
        Orientation = System.Windows.Controls.Orientation.Horizontal,
        Margin = new Thickness(2, 0, 0, 8)
    };

    private readonly StackPanel _wrPeriodSolo = PeriodRow();

    private readonly StackPanel _wrPeriodDuo  = PeriodRow();

    /// <param name="duo">true — кнопки правой половины (связки).</param>
    private void FillPeriodButtons(StackPanel row, bool duo)
    {
        bool Chosen() => duo ? AppSettings.Current.DuoWinratesAllTime
                             : AppSettings.Current.WinratesAllTime;

        Button Make(string text, bool allTime)
        {
            var on = Chosen() == allTime;
            var b = new Button
            {
                Content = text, FontSize = 11, FontWeight = FontWeights.Bold,
                Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 0),
                Cursor = System.Windows.Input.Cursors.Hand,
                Background = new SolidColorBrush(on ? Color.FromRgb(0x22, 0x36, 0x55)
                                                    : Color.FromArgb(0x00, 0, 0, 0)),
                Foreground = new SolidColorBrush(on ? Colors.White : Color.FromRgb(0x8A, 0xA0, 0xB2)),
                BorderBrush = new SolidColorBrush(on ? Color.FromRgb(0x5A, 0x8A, 0xC8)
                                                     : Color.FromRgb(0x30, 0x42, 0x54)),
                BorderThickness = new Thickness(1),
            };
            b.Click += (_, _) =>
            {
                if (Chosen() == allTime) return;
                if (duo) AppSettings.Current.DuoWinratesAllTime = allTime;
                else     AppSettings.Current.WinratesAllTime    = allTime;
                AppSettings.SaveQuiet();
                RefreshWinrates();   // каркас на месте, меняется только содержимое
            };
            return b;
        }

        row.Children.Add(Make(Loc.T("pool.periodRecent", SessionTracker.RecentDays), false));
        row.Children.Add(Make(Loc.T("pool.periodAll"), true));
    }

    /// Заголовок раздела винрейтов.
    private static void WrHeader(System.Windows.Controls.Panel into, string text)
    {
        var tb = new TextBlock
        {
            Text = text,
            Foreground = new SolidColorBrush(Color.FromRgb(0xC9, 0xD2, 0xDC)),
            FontWeight = FontWeights.Bold, FontSize = 12, Margin = new Thickness(2, 0, 0, 6),
            TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis
        };
        // Прибит сверху: половина — DockPanel, и остаток высоты должен уйти
        // ленте, а не разойтись поровну.
        DockPanel.SetDock(tb, Dock.Top);
        into.Children.Add(tb);
    }

    /// Приглушённая строка «пока пусто».
    private static void WrEmpty(System.Windows.Controls.Panel into, string text)
    {
        var tb = new TextBlock
        {
            Text = text, Foreground = new SolidColorBrush(Color.FromRgb(0x6A, 0x78, 0x86)),
            FontSize = 11, Margin = new Thickness(2, 0, 0, 10), TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Top
        };
        DockPanel.SetDock(tb, Dock.Top);
        into.Children.Add(tb);
    }

    // ── Часть первая: личный винрейт по чемпионам ───────────────────────────
    //
    // Отмечен звездой личный пул — показываем только его чемпионов: человек
    // отметил, чем играет, и мерить себя логично по этому же набору. Звезды
    // нет — показываем всех, как и раньше.
    private void RefreshSoloWinrates()
    {
        var days   = SoloDays;
        var ranked = SessionTracker.ChampStatsMap(days, SessionTracker.QueuesRanked);
        var normal = SessionTracker.ChampStatsMap(days, SessionTracker.QueuesNormal);

        var a = PoolStore.Current();
        var fav = a.Pools.FirstOrDefault(p => p.Id == a.FavPoolId);
        var only = fav is null ? null
            : new HashSet<int>(fav.ByRole.Values.SelectMany(l => l).Where(id => id != 0));

        var ids = ranked.Keys.Concat(normal.Keys).Distinct()
            .Where(id => only is null || only.Contains(id))
            .OrderByDescending(id => ranked.GetValueOrDefault(id).Games + normal.GetValueOrDefault(id).Games)
            .Take(30).ToList();

        WrHeader(_wrSolo, fav is null
            ? Loc.T("pool.myWinratesShort")
            : Loc.T("pool.myWinratesPoolShort", fav.Name.Length > 0 ? fav.Name : Loc.T("pool.pool")));

        if (ids.Count == 0)
        {
            // Пул отмечен, а игр по ЕГО чемпионам нет — это другое, чем «игр нет
            // вовсе». Прежний текст звал поиграть с запущенной программой, хотя
            // играть человек мог много, просто не этими.
            WrEmpty(_wrSolo, only is not null
                ? Loc.T("pool.myWinratesPoolEmpty")
                : Loc.T("pool.myWinratesEmpty"));
            return;
        }

        var row = new WrapPanel();
        foreach (var id in ids)
        {
            // Слот чемпиона: рамка охватывает и иконку, и винрейт под ней, а её
            // цвет — цвет этого винрейта. Так результат читается по слоту целиком.
            var r = ranked.GetValueOrDefault(id);
            var n = normal.GetValueOrDefault(id);
            var gAll  = r.Games + n.Games;
            var wrAll = gAll > 0 ? 100.0 * (r.Wins + n.Wins) / gAll : 50.0;
            var frame = WinrateColor.BrushForSample(wrAll, gAll);

            var inner = new StackPanel { Margin = new Thickness(4, 4, 4, 3) };
            if (IconCache.Get(id) is { } src)
                inner.Children.Add(new Border
                {
                    Width = 40, Height = 40, CornerRadius = new CornerRadius(4),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Background = new ImageBrush { ImageSource = src, Stretch = Stretch.UniformToFill }
                });
            if (PoolEditorWindow.WrChip(r, n) is { } chip)
            {
                chip.Margin = new Thickness(0, 3, 0, 0);
                inner.Children.Add(chip);
            }

            var slot = new Border
            {
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                BorderBrush = frame,
                // Подложка того же цвета, но почти прозрачная — слот слегка
                // «подсвечен» результатом, не перетягивая внимание с портрета.
                Background = WinrateColor.TintForSample(wrAll, gAll),
                Margin = new Thickness(0, 0, 8, 8),
                ToolTip = DataDragon.Name(id),
                Child = inner
            };
            row.Children.Add(slot);
        }
        _wrSolo.Children.Add(new ScrollViewer
        {
            // Потолка нет нарочно: высоту отмеряет подвижная полоса, а раньше
            // при жёстких 170 половина отданного места пустовала.
            Content = row,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        });
    }

    /// <summary>
    /// «Эта пара задумана в пуле?» — обе половины должны быть из него.
    ///
    /// В ручном режиме пул перечисляет сами связки, в авто — наборы по ролям, и
    /// парой считается любое сочетание «мой из моей половины + его из его».
    /// </summary>
    /// <summary>
    /// «Пара задумана в пуле?» — теперь и по РОЛЯМ, если они у записи есть.
    ///
    /// Пул собран по линиям: мои саппорты отдельно, его стрелки отдельно. Игра,
    /// где мы взяли тех же чемпионов, но встали иначе, к этому пулу отношения
    /// не имеет, а считалась в нём наравне.
    ///
    /// Роли есть не у всех записей: до того, как их начали хранить, история
    /// ничего о линиях не знала, и переразметить её нечем. Такие записи
    /// проверяются как раньше — по чемпионам. Иначе накопленный счёт исчез бы с
    /// экрана, а это выглядело бы как потеря данных.
    /// </summary>
    private static Func<SessionTracker.PairStat, bool> InPool(DuoPool? d)
    {
        if (d is null) return _ => false;
        if (d.Manual)
        {
            var set = d.ManualPairs.Select(p => (p.Mine, p.Friend)).ToHashSet();
            return p => set.Contains((p.MyChampionId, p.AllyChampionId));
        }
        var mine   = d.Mine.Values.SelectMany(l => l).Where(x => x != 0).ToHashSet();
        var friend = d.Friend.Values.SelectMany(l => l).Where(x => x != 0).ToHashSet();
        return p =>
        {
            if (!mine.Contains(p.MyChampionId) || !friend.Contains(p.AllyChampionId)) return false;
            if (!p.HasRoles) return true;   // роль не сохранена — судим как раньше
            return d.Mine.TryGetValue(p.MyRole, out var mineRole) && mineRole.Contains(p.MyChampionId)
                && d.Friend.TryGetValue(p.AllyRole, out var hisRole) && hisRole.Contains(p.AllyChampionId);
        };
    }

    /// Ниже этого числа игр процент не показываем — он ещё ничего не значит.
    /// То же число, по которому WinrateColor глушит цвет: признак один.
    private const int SmallSample = 5;

    /// Название очереди для подсказки. Ключи те же, что у трекера сессии.
    private static string QueueName(string q) =>
        q is "solo" or "flex" or "normal" ? Loc.T($"session.queue.{q}") : q;

    // ── Часть вторая: винрейт связок ────────────────────────────────────────
    //
    // Здесь не общая синергия из базы, а СВОЙ счёт: сколько игр сыграно вдвоём
    // именно на этой паре чемпионов и сколько выиграно. Копится только вперёд —
    // история клиента отдаёт около двадцати последних игр и глубже не пускает.
    private void RefreshDuoWinrates()
    {
        var a = PoolStore.Current();
        var fav = a.DuoPools.FirstOrDefault(d => d.Id == a.FavDuoId);

        // Только с напарником по пулу. Раньше при неопознанном напарнике сюда
        // сыпались связки со всеми подряд, да ещё с пометкой «пара из пула» —
        // она ставится по чемпионам, а человек при этом посторонний.
        //
        // Пустой список честнее чужого: лучше сказать, чего не хватает.
        var who = fav?.FriendPuuid ?? "";
        if (who.Length == 0)
        {
            WrHeader(_wrDuo, Loc.T("pool.duoWinratesShort"));
            WrEmpty(_wrDuo, fav is null ? Loc.T("pool.duoNoPool") : Loc.T("pool.duoNoMate"));
            return;
        }
        var pairs = SessionTracker.TopPairs(who, take: 200, days: DuoDays).ToList();

        // Только пары, задуманные В ПУЛЕ. Раньше сюда шли ВСЕ игры с этим
        // человеком, а пулевые лишь поднимались наверх — и счёт связки мешался
        // со случайными сочетаниями: напарник взял чемпиона не из пула, сыграл
        // не на своей роли, а игра всё равно ложилась в ту же ленту. Смотрят
        // сюда ради пула, значит и считать надо по нему.
        var inPool = InPool(fav);
        pairs = pairs
            .Where(inPool)
            .OrderByDescending(p => p.Games)
            .ThenByDescending(p => p.WinRate)
            .Take(24).ToList();

        // Заголовок должен называть ЧЕЛОВЕКА. FriendName — подпись плитки, она
        // склеена из названий половин («supports + top») и на месте имени
        // читается бессмыслицей: «связки с supports + top».
        //
        // Ник берём по порядку: из файла, которым обменялись; если его нет —
        // из самих связок, там он лежит рядом с играми; и только потом
        // отступаем к подписи плитки.
        var nick = DuoNaming.PartnerNick(fav, pairs);

        // Пары показаны СО ВСЕМИ, а подпись обещала бы одного человека — так
        // подписывать нельзя: пул собран руками, напарник ещё не опознан.
        var byPerson = who is { Length: > 0 };
        // Период в подписи стоит нарочно. Слева считается за 30 дней, здесь — за
        // всё время: у связки игр во много раз меньше, и окно в месяц оставило
        // бы от неё одну-две. Но половины стоят рядом, и разницу надо назвать, а
        // не оставлять догадываться.
        WrHeader(_wrDuo, byPerson && nick.Length > 0
            ? Loc.T("pool.duoWinratesWithShort", nick)
            : Loc.T("pool.duoWinratesShort"));

        if (pairs.Count == 0)
        {
            // Играли вместе, но не по пулу — это другое «пусто», и сказать надо
            // иначе: счёт есть, просто не тот, что здесь показывают.
            var anyAtAll = SessionTracker.TopPairs(who, take: 1, days: DuoDays).Any();
            WrEmpty(_wrDuo, anyAtAll ? Loc.T("pool.duoWinratesOffPool")
                                     : Loc.T("pool.duoWinratesEmpty"));
            return;
        }

        var row = new WrapPanel();
        foreach (var p in pairs)
        {
            var frame = WinrateColor.BrushForSample(p.WinRate, p.Games);
            var inner = new StackPanel { Margin = new Thickness(4, 4, 4, 3) };

            // Две иконки рядом: слева мой чемпион, справа его. Так связка
            // читается как одна карточка, а не как два отдельных винрейта.
            var faces = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            foreach (var (id, first) in new[] { (p.MyChampionId, true), (p.AllyChampionId, false) })
                if (IconCache.Get(id) is { } src)
                    faces.Children.Add(new Border
                    {
                        Width = 36, Height = 36, CornerRadius = new CornerRadius(4),
                        Margin = new Thickness(first ? 0 : 3, 0, 0, 0),
                        Background = new ImageBrush { ImageSource = src, Stretch = Stretch.UniformToFill }
                    });
            inner.Children.Add(faces);

            inner.Children.Add(new TextBlock
            {
                // «73% / 8-3» — как в левой половине («57% / 12»), только вместо
                // числа игр счёт побед и поражений: у связки важно не сколько
                // сыграно, а чем кончилось.
                //
                // На малой выборке процента НЕТ: «0% / 0-3» читается приговором,
                // хотя это три игры. Порог тот же, по которому уже глушится
                // цвет, — значит признак один и тот же, а не два разных.
                Text = p.Games >= SmallSample
                    ? $"{p.WinRate:F0}% / {p.Wins}-{p.Games - p.Wins}"
                    : $"{p.Wins}-{p.Games - p.Wins}",
                Foreground = frame, FontSize = 10, FontWeight = FontWeights.Bold,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 3, 0, 0)
            });

            row.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(8),
                // Толщина у всех одна: в ленте теперь только пары из пула, и
                // выделять стало нечего на фоне чего.
                BorderThickness = new Thickness(1),
                BorderBrush = frame,
                Background = WinrateColor.TintForSample(p.WinRate, p.Games),
                Margin = new Thickness(0, 0, 8, 8),
                ToolTip = $"{DataDragon.Name(p.MyChampionId)} + {DataDragon.Name(p.AllyChampionId)}"
                          + (p.AllyName.Length > 0 ? $"\n{p.AllyName}" : "")
                          + $"\n{p.WinRate:0}% · {p.Games} " + Loc.T("pool.games")
                          // Очередь: дуо-пул собирают под одну, и «5-2 во флексе»
                          // это совсем не то же, что «5-2 где придётся».
                          + (p.Queue.Length > 0 ? $" · {QueueName(p.Queue)}" : "")
                          + (p.Games < SmallSample ? "\n" + Loc.T("pool.fewGames") : ""),
                Child = inner
            });
        }
        _wrDuo.Children.Add(new ScrollViewer
        {
            Content = row,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        });
    }
}
