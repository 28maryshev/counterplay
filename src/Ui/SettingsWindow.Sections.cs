using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;
using ComboBox = System.Windows.Controls.ComboBox;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;

namespace Counterplay;

// Окно настроек — что в каком разделе. Разделы повторяют экраны программы, а
// внутри — подразделы по смыслу (владелец 19.11: «раздели пункты на подпункты:
// те, что касаются внешнего вида, настроек алгоритмов и т. д.»). Сам подбор
// отделён от того, как он показан: «Алгоритм подбора» — первым в драфте.
sealed partial class SettingsWindow
{
    /// Синхронизация — выше всего прочего и сразу под ней язык. На НОВОМ
    /// компьютере человек приходит именно сюда: пулов у него ещё нет, он за
    /// ними и пришёл. Держать кнопку только в окне пулов означало, что
    /// включить синхронизацию негде.
    private void AddSyncAndLanguage(StackPanel body)
    {
        body.Children.Add(Section(Loc.T("sync.title")));
        body.Children.Add(new TextBlock
        {
            Text = Loc.T("sync.settingsHint"),
            Foreground = new SolidColorBrush(Mute),
            FontSize = 11, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6)
        });
        var syncRow = SyncBadge.Row(() => PoolSettingsWindow.AskSyncPassword(this));
        syncRow.HorizontalAlignment = HorizontalAlignment.Left;
        syncRow.Margin = new Thickness(0, 0, 0, 4);
        body.Children.Add(syncRow);

        // Язык — в обход «Применить»: смена языка перерисовывает само окно
        // настроек, ждать кнопки тут не от чего.
        body.Children.Add(Section(Loc.T("settings.language")));
        var langs = new ComboBox
        {
            Margin = new Thickness(0, 2, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            MinWidth = 220, Padding = new Thickness(8, 4, 8, 4),
            FontFamily = Font("UiFont"), FontSize = 13, Cursor = Cursors.Hand
        };
        foreach (var l in Loc.Languages)
            langs.Items.Add(new ComboBoxItem { Content = LangRow(l), Tag = l.Code });
        langs.SelectedIndex = Math.Max(0, Loc.Languages.ToList().FindIndex(l => l.Code == Loc.Current));
        langs.SelectionChanged += (_, _) =>
        {
            if (langs.SelectedItem is ComboBoxItem { Tag: string code } && code != Loc.Current)
            {
                Loc.SetLanguage(code);
                _body.Content = Build();   // перестраиваем содержимое на новом языке
            }
        };
        body.Children.Add(langs);
    }

    private void AddProfile(StackPanel body, AppSettings s)
    {
        body.Children.Add(Section(Loc.T("settings.ready")));

        body.Children.Add(Group(Loc.T("settings.groupRank")));
        body.Children.Add(Previewed(Row(Loc.T("settings.readyRank"),    Loc.T("settings.readyRankHint"),
            s.ReadyRank,    v => { s.ReadyRank = v;    MarkDirty(); }), "ReadyRank"));
        body.Children.Add(Previewed(Row(Loc.T("settings.readyCompact"), Loc.T("settings.readyCompactHint"),
            s.ReadyCompact, v => { s.ReadyCompact = v; MarkDirty(); }), "ReadyCompact"));
        body.Children.Add(Previewed(Row(Loc.T("settings.readyLast5"),   Loc.T("settings.readyLast5Hint"),
            s.ReadyLast5,   v => { s.ReadyLast5 = v;   MarkDirty(); }), "ReadyLast5"));

        body.Children.Add(Group(Loc.T("settings.groupStats")));
        body.Children.Add(Previewed(Row(Loc.T("settings.readyWinrate"), Loc.T("settings.readyWinrateHint"),
            s.ReadyWinrate, v => { s.ReadyWinrate = v; MarkDirty(); }), "ReadyWinrate"));
        body.Children.Add(PreviewedChoice("ChartMode", Loc.T("settings.chart"), Loc.T("settings.chartHint"),
            [("winrate", Loc.T("settings.chartWinrate")),
             ("rating",  Loc.T("settings.chartRating")),
             ("off",     Loc.T("settings.chartOff"))],
            s.ChartMode, v => { s.ChartMode = v; MarkDirty(); }));
        body.Children.Add(PreviewedChoice("ChartDays", Loc.T("settings.chartDays"), Loc.T("settings.chartDaysHint"),
            [("30", Loc.T("settings.days30")), ("90", Loc.T("settings.days90"))],
            s.ChartDays.ToString(), v => { s.ChartDays = int.Parse(v); MarkDirty(); }));
        // Все четыре очереди профиля и их подписи оттуда же: до 19.11 здесь
        // были только соло и флекс, английскими словами, а профиль умел и
        // нормалы с ARAM (OverlayWindow.Session — те же ключи).
        body.Children.Add(Choice(Loc.T("settings.queue"), Loc.T("settings.queueHint"),
            [("last", Loc.T("settings.queueLast")),
             .. new[] { "solo", "flex", "normal", "aram" }.Select(q => (q, Loc.T("session.queue." + q)))],
            s.DefaultQueue, v => { s.DefaultQueue = v; MarkDirty(); }));

        body.Children.Add(Group(Loc.T("settings.groupPool")));
        body.Children.Add(Previewed(Row(Loc.T("settings.readyPool"),   Loc.T("settings.readyPoolHint"),
            s.ReadyPool,   v => { s.ReadyPool = v;   MarkDirty(); }), "ReadyPool"));
        body.Children.Add(Previewed(Row(Loc.T("settings.readyChamps"), Loc.T("settings.readyChampsHint"),
            s.ReadyChamps, v => { s.ReadyChamps = v; MarkDirty(); }), "ReadyChamps"));

        body.Children.Add(Group(Loc.T("settings.groupInfo")));
        body.Children.Add(Previewed(Row(Loc.T("settings.readyPhase"),  Loc.T("settings.readyPhaseHint"),
            s.ReadyPhase,  v => { s.ReadyPhase = v;  MarkDirty(); }), "ReadyPhase"));
        body.Children.Add(Previewed(Row(Loc.T("settings.readyBeta"),   Loc.T("settings.readyBetaHint"),
            s.ReadyBeta,   v => { s.ReadyBeta = v;   MarkDirty(); }), "ReadyBeta"));
    }

    private void AddDraft(StackPanel body, AppSettings s)
    {
        body.Children.Add(Section(Loc.T("settings.draft")));

        // Что и кого советовать — сам подбор. Остальные подразделы — как он
        // показан: от них оценки не меняются.
        body.Children.Add(Group(Loc.T("settings.groupAlgo")));
        body.Children.Add(Previewed(Row(Loc.T("settings.draftEnabled"), Loc.T("settings.draftEnabledHint"),
            s.DraftEnabled, v => { s.DraftEnabled = v; MarkDirty(); }), "DraftEnabled"));
        body.Children.Add(Row(Loc.T("settings.draftPersonal"), Loc.T("settings.draftPersonalHint"),
            s.DraftPersonal, v => { s.DraftPersonal = v; MarkDirty(); }));
        body.Children.Add(PreviewedChoice("DraftCount", Loc.T("settings.draftCount"), Loc.T("settings.draftCountHint"),
            [("6", "6"), ("8", "8"), ("10", "10")],
            s.DraftCount.ToString(), v => { s.DraftCount = int.Parse(v); MarkDirty(); }));
        body.Children.Add(Previewed(Row(Loc.T("settings.draftUnowned"), Loc.T("settings.draftUnownedHint"),
            s.DraftUnowned, v => { s.DraftUnowned = v; MarkDirty(); }), "DraftUnowned"));
        // Скрытые кнопкой 👎: в драфте окно открывает кнопка над тир-листом, а
        // вне драфта вернуть чемпиона было негде. Открывается сразу, мимо
        // «Применить», — это не настройка, а список.
        body.Children.Add(Row(Loc.T("dislike.windowTitle"), Loc.T("dislike.btnTip"),
            SmallButton(Loc.T("settings.open"), OpenHidden)));

        body.Children.Add(Group(Loc.T("settings.groupCard")));
        body.Children.Add(Previewed(Row(Loc.T("settings.draftReasons"),  Loc.T("settings.draftReasonsHint"),
            s.DraftReasons,  v => { s.DraftReasons = v;  MarkDirty(); }), "DraftReasons"));
        body.Children.Add(Previewed(Row(Loc.T("settings.draftMetrics"),  Loc.T("settings.draftMetricsHint"),
            s.DraftMetrics,  v => { s.DraftMetrics = v;  MarkDirty(); }), "DraftMetrics"));
        body.Children.Add(Row(Loc.T("settings.draftItems"),    Loc.T("settings.draftItemsHint"),
            s.DraftItems,    v => { s.DraftItems = v;    MarkDirty(); }));

        body.Children.Add(Group(Loc.T("settings.groupTeams")));
        body.Children.Add(Previewed(Row(Loc.T("settings.draftCombos"),   Loc.T("settings.draftCombosHint"),
            s.DraftCombos,   v => { s.DraftCombos = v;   MarkDirty(); }), "DraftCombos"));
        body.Children.Add(Previewed(Row(Loc.T("settings.draftArch"),     Loc.T("settings.draftArchHint"),
            s.DraftArch,     v => { s.DraftArch = v;     MarkDirty(); }), "DraftArch"));
        body.Children.Add(Previewed(Row(Loc.T("settings.draftSideIcons"), Loc.T("settings.draftSideIconsHint"),
            s.DraftSideIcons, v => { s.DraftSideIcons = v; MarkDirty(); }), "DraftSideIcons"));
        body.Children.Add(Previewed(Row(Loc.T("settings.draftDamage"),   Loc.T("settings.draftDamageHint"),
            s.DraftDamage,   v => { s.DraftDamage = v;   MarkDirty(); }), "DraftDamage"));
        body.Children.Add(Previewed(Row(Loc.T("settings.draftMirror"),  Loc.T("settings.draftMirrorHint"),
            s.DraftMirror,  v => { s.DraftMirror = v;  MarkDirty(); }), "DraftMirror"));

        body.Children.Add(Group(Loc.T("settings.groupHover")));
        body.Children.Add(Row(Loc.T("settings.draftHover"),    Loc.T("settings.draftHoverHint"),
            s.DraftHover,    v => { s.DraftHover = v;    MarkDirty(); }));
        body.Children.Add(Row(Loc.T("settings.draftChampTip"), Loc.T("settings.draftChampTipHint"),
            s.DraftChampTip, v => { s.DraftChampTip = v; MarkDirty(); }));

        body.Children.Add(Group(Loc.T("settings.groupBars")));
        body.Children.Add(Previewed(Row(Loc.T("settings.draftRolePool"), Loc.T("settings.draftRolePoolHint"),
            s.DraftRolePool, v => { s.DraftRolePool = v; MarkDirty(); }), "DraftRolePool"));
        body.Children.Add(Row(Loc.T("settings.draftRunes"),    Loc.T("settings.draftRunesHint"),
            s.DraftRunes,    v => { s.DraftRunes = v;    MarkDirty(); }));

        // Фразы в чат: тумблер полосы и тот же редактор, что открывает карандаш
        // на ней (PhraseEditor), — здесь правки уходят по «Применить».
        body.Children.Add(Group(Loc.T("settings.groupPhrases")));
        body.Children.Add(Row(Loc.T("settings.draftPhrases"), Loc.T("settings.draftPhrasesHint"),
            s.DraftPhrases, v => { s.DraftPhrases = v; MarkDirty(); }));
        body.Children.Add(PhraseEditor.Build(s, MarkDirty));
    }

    private void AddPlacement(StackPanel body, AppSettings s)
    {
        body.Children.Add(Section(Loc.T("settings.placement")));
        body.Children.Add(ChoiceList(Loc.T("settings.placementHint"),
            [("right",    Loc.T("settings.placeRight"),    Loc.T("settings.placeRightHint")),
             ("cover",    Loc.T("settings.placeCover"),    Loc.T("settings.placeCoverHint")),
             ("allies",   Loc.T("settings.placeAllies"),   Loc.T("settings.placeAlliesHint")),
             ("center",   Loc.T("settings.placeCenter"),   Loc.T("settings.placeCenterHint")),
             ("remember", Loc.T("settings.placeRemember"), Loc.T("settings.placeRememberHint"))],
            s.DraftPlacement, v => { s.DraftPlacement = v; MarkDirty(); }));
    }

    private void AddBans(StackPanel body, AppSettings s)
    {
        body.Children.Add(Section(Loc.T("settings.bans")));
        body.Children.Add(Previewed(Row(Loc.T("settings.bansTier"), Loc.T("settings.bansTierHint"),
            s.BansTierList, v => { s.BansTierList = v; MarkDirty(); }), "BansTierList"));
        // С чего тир-лист начинает драфт; в самом драфте — кнопка над ним.
        body.Children.Add(PreviewedChoice("BansTierMode", Loc.T("settings.bansTierMode"), Loc.T("settings.bansTierModeHint"),
            [("picks", Loc.T("tier.modePicks")), ("bans", Loc.T("tier.modeBans"))],
            s.BansTierMode, v => { s.BansTierMode = v; MarkDirty(); }));
    }

    private void AddGeneral(StackPanel body, AppSettings s)
    {
        body.Children.Add(Section(Loc.T("settings.general")));

        body.Children.Add(Group(Loc.T("settings.groupLook")));
        body.Children.Add(Choice(Loc.T("settings.opacity"), Loc.T("settings.opacityHint"),
            [("1", "100%"), ("0.85", "85%"), ("0.7", "70%")],
            s.Opacity.ToString(System.Globalization.CultureInfo.InvariantCulture),
            v => { s.Opacity = double.Parse(v, System.Globalization.CultureInfo.InvariantCulture);
                   MarkDirty(); }));
        body.Children.Add(PreviewedChoice("FontScale", Loc.T("settings.scale"), Loc.T("settings.scaleHint"),
            [("0.9", Loc.T("settings.scaleS")), ("1", Loc.T("settings.scaleM")), ("1.15", Loc.T("settings.scaleL"))],
            s.FontScale.ToString(System.Globalization.CultureInfo.InvariantCulture),
            v => { s.FontScale = double.Parse(v, System.Globalization.CultureInfo.InvariantCulture);
                   MarkDirty(); }));
        body.Children.Add(Choice(Loc.T("settings.clientSize"), Loc.T("settings.clientSizeHint"),
            [("auto", Loc.T("settings.sizeAuto")), ("1024", "1024×576"), ("1280", "1280×720"),
             ("1600", "1600×900"), ("1920", "1920×1080")],
            s.ClientSize, v => { s.ClientSize = v; MarkDirty(); }));

        body.Children.Add(Group(Loc.T("settings.groupBehavior")));
        body.Children.Add(Row(Loc.T("settings.onTop"),   Loc.T("settings.onTopHint"),
            s.AlwaysOnTop,    v => { s.AlwaysOnTop = v;    MarkDirty(); }));
        body.Children.Add(Row(Loc.T("settings.inGame"),  Loc.T("settings.inGameHint"),
            s.KeepDuringGame, v => { s.KeepDuringGame = v; MarkDirty(); }));
        // Автозапуск жил только в меню трея — в настройках его не находили. Не
        // поле AppSettings, а ключ реестра (Autostart) и settings.json, поэтому
        // мимо «Применить» и мимо «По умолчанию», как язык. Песочница реестр не
        // трогает: её настройки живут только в памяти.
        if (Autostart.Supported && !Sandbox.Active)
            body.Children.Add(Row(Loc.T("tray.autostart"), Loc.T("settings.autostartHint"),
                Autostart.IsEnabled, v =>
                {
                    var on = Autostart.Set(v);
                    Settings.Set("autostart", on);
                    if (on != v) _body.Content = Build();   // реестр не поддался — показать как есть
                }));
    }

    /// «Применить» и «По умолчанию» с пояснением под ними.
    private void AddButtons(StackPanel body)
    {
        var reset = ActionButton(Loc.T("settings.reset"), "#8AA0B2", "#35485A");
        reset.Click += (_, _) => { _draft = AppSettings.Defaults(); MarkDirty(); _body.Content = Build(); };

        _apply = ActionButton(Loc.T("settings.apply"), "#0E141D", "#36D6E7");
        _apply.Background = new SolidColorBrush(Color.FromRgb(0x36, 0xD6, 0xE7));
        _apply.IsEnabled = _dirty;
        _apply.Opacity = _dirty ? 1.0 : 0.45;
        _apply.Click += (_, _) =>
        {
            // Отдаём КОПИЮ, а сам _draft не подменяем: обработчики тумблеров
            // держат ссылку на тот объект, с которым их построили. Раньше после
            // «Применить» поле указывало на новый объект, а переключатели правили
            // старый — второе изменение просто некуда было применить.
            AppSettings.Apply(_draft.Clone());
            _dirty = false;
            _apply.IsEnabled = false;
            _apply.Opacity = 0.45;
        };

        var bottom = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            Margin = new Thickness(0, 20, 0, 0)
        };
        bottom.Children.Add(_apply);
        bottom.Children.Add(reset);
        body.Children.Add(bottom);
        body.Children.Add(new TextBlock
        {
            Text = Loc.T("settings.applyHint"),
            FontFamily = Font("UiFont"), FontSize = 11,
            Foreground = new SolidColorBrush(Mute),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0)
        });
    }

    /// <summary>
    /// Подраздел внутри раздела: мелкая приглушённая надпись капителью и черта
    /// до правого края — тише заголовка раздела (золотой, 15), заметнее строк.
    /// </summary>
    private static UIElement Group(string text)
    {
        var grid = new Grid { Margin = new Thickness(0, 14, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new TextBlock
        {
            // Заглавные — по правилам языка интерфейса: у турецкого «i» → «İ».
            Text = text.ToUpper(System.Globalization.CultureInfo.GetCultureInfo(Loc.Current)), FontFamily = Font("UiFont"),
            FontSize = 10.5, FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x6E, 0x8A, 0xA0)),
            VerticalAlignment = VerticalAlignment.Center,
        });
        var line = new Border
        {
            Height = 1, Margin = new Thickness(10, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromArgb(0x30, 0x8A, 0xA0, 0xB2)),
        };
        Grid.SetColumn(line, 1);
        grid.Children.Add(line);
        return grid;
    }
}
