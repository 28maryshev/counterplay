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

/// <summary>Редактор одного пула (обычного или дуо). Работает на КОПИИ — Назад/Сброс
/// не трогают исходные данные, пока не нажали Сохранить (с подтверждением).
///
/// Окно НЕ модальное: модальное запирает всё приложение, и настройки под ним
/// нельзя было даже отодвинуть, чтобы заглянуть в то, что оно закрыло. Отсюда
/// собственный флаг «сохранили» вместо DialogResult — он есть только у диалога.</summary>
/// <summary>
/// Окно сборки пула. Публичное РАДИ ПРОВЕРКИ: ник напарника с плашкой выбора
/// однажды уехал за край окна и стал недоступен, а увидеть это можно только
/// открыв окно и померив, где эта плашка оказалась.
/// </summary>
// Редактор пула — каркас: копия пула, имя, тело окна, экспорт, сброс и сохранение.
// Роли и чемпионы, дуо-половины и напарник — в соседних PoolEditorWindow.*.cs.
public sealed partial class PoolEditorWindow : Window
{
    /// Нажали «Сохранить» (для владельца — обновить кнопки/слот пула).
    public bool Saved { get; private set; }

    private readonly bool _duo;

    private readonly RecommendationEngine? _engine;

    private readonly ChampPool? _srcPool;

    private readonly DuoPool?   _srcDuo;

    private readonly Dictionary<string, int> _idByName;

    private readonly List<string> _names;

    // Рабочие копии.
    private string _name;

    /// Ник напарника — только для дуо-пула. Пусто, пока не вписали.
    private string _nick = "";

    /// <summary>
    /// Кто этот друг на самом деле. Ник — просто подпись, а следует программа
    /// за человеком по puuid: по нему она ищет его в команде и по нему считает
    /// винрейты связки. Раньше его можно было задать только обменным файлом
    /// или дать проставиться самому по первой пати — и если угадало неверно,
    /// исправить было нечем.
    /// </summary>
    private string _matePuuid = "";

    /// «Забыть» нажали НАРОЧНО. Пустой puuid сам по себе значит и «забыли», и
    /// «не успело подхватиться» — после того, как пулы однажды уже пропали,
    /// разводить такие случаи надо явно, а не по пустой строке.
    private bool _mateCleared;

    /// <summary>
    /// Секрет дуо — ключ к обмену наигранностью с напарником.
    ///
    /// Живёт полем, а не рождается при выгрузке: наружу уходит РАБОЧАЯ КОПИЯ
    /// пула, собранная из полей редактора, и секрет на ней не сохранился бы
    /// никуда — при каждой выгрузке он получался бы новый, а обмен молча не
    /// состоялся бы. Поэтому он тут, пишется в свой пул и уезжает в файле или
    /// в коде — в обоих случаях это один и тот же конверт.
    /// </summary>
    private string _shareSecret = "";

    private readonly Dictionary<string, List<int>> _mine   = NewRoles();

    private readonly Dictionary<string, List<int>> _friend = NewRoles();

    private bool _dirty;

    // Дуо: способ подбора. Manual — фиксированные связки (список пар), иначе автоподбор.
    // По умолчанию Manual (для нового пула); существующий грузит сохранённый выбор.
    private bool _manual = true;

    private readonly List<ManualDuoPair> _manualPairs = [];

    private readonly TextBox _nameBox = new() { FontSize = 15, FontWeight = FontWeights.Bold, MinWidth = 240 };

    /// Ник напарника. Уже, чем название: это одно слово, а не фраза.
    private readonly StackPanel _body = new();

    private static Dictionary<string, List<int>> NewRoles() =>
        PoolSettingsWindow.Roles.ToDictionary(r => r, _ => new List<int>());

    public PoolEditorWindow(object? existing, bool duo, RecommendationEngine? engine = null)
    {
        _duo = duo;
        _engine = engine;
        // GroupBy, а не ToDictionary: дубликат имени в Data Dragon (бывает при
        // выкатке патча) не должен ронять окно настроек — берём первого.
        _idByName = DataDragon.GetAllIconUrls().Keys
            .GroupBy(DataDragon.Name)
            .ToDictionary(g => g.Key, g => g.First());
        _names = _idByName.Keys.Where(n => !string.IsNullOrEmpty(n))
                          .OrderBy(n => n, StringComparer.CurrentCulture).ToList();

        if (existing is ChampPool p)
        { _srcPool = p; _name = p.Name; CopyInto(_mine, p.ByRole); }
        else if (existing is DuoPool d)
        { _srcDuo = d; _name = d.FriendName; _nick = d.FriendNick; _matePuuid = d.FriendPuuid;
          _shareSecret = d.ShareSecret;
          CopyInto(_mine, d.Mine); CopyInto(_friend, d.Friend);
          _manual = d.Manual;
          _manualPairs.AddRange(d.ManualPairs.Select(p => new ManualDuoPair {
              Mine = p.Mine, MineRole = p.MineRole, Friend = p.Friend, FriendRole = p.FriendRole })); }
        else
            _name = "";

        Title  = _duo ? Loc.T("pool.duo") : Loc.T("pool.pool");
        Width  = 620; Height = 560;
        MinWidth = 520; MinHeight = 380;
        Background = new SolidColorBrush(PoolSettingsWindow.Bg);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        PoolUi.Apply(this);

        BuildUi();
    }

    private static void CopyInto(Dictionary<string, List<int>> dst, Dictionary<string, List<int>> src)
    {
        foreach (var r in PoolSettingsWindow.Roles)
            dst[r] = src.TryGetValue(r, out var l) ? [.. l] : [];
    }

    private void BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(16) };

        // Верх: имя слева + Назад/Сброс/Сохранить справа.
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, _duo ? 8 : 14) };
        _nameBox.Text = _name;
        _nameBox.TextChanged += (_, _) => { _name = _nameBox.Text; _dirty = true; };

        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        // Экспорт — золотом и с отбивкой: это действие не по ходу формы, а в
        // сторону, и среди трёх одинаковых тёмных кнопок его не находили глазами.
        var export = PoolUi.GoldBtn(Loc.T("pool.export"));
        export.Content = PoolUi.IconLabel("⭱", Loc.T("pool.export"));
        export.Margin = new Thickness(0, 0, 16, 0);
        export.Click += (_, _) => Export();
        btns.Children.Add(export);
        var back = ActionBtn(Loc.T("pool.back"));
        back.Click += (_, _) => Back();
        var reset = ActionBtn(Loc.T("pool.reset"));
        reset.Click += (_, _) => Reset();
        var save = ActionBtn(Loc.T("pool.save"), primary: true);
        save.Click += (_, _) => Save();
        btns.Children.Add(back); btns.Children.Add(reset); btns.Children.Add(save);
        // Метка для проверки: по левому краю кнопок видно, сколько места
        // остаётся имени с ником, — именно туда ник и залезал.
        btns.Tag = "editor-actions";
        DockPanel.SetDock(btns, Dock.Right);
        top.Children.Add(btns);

        // Отступ справа: поле имени тянется на всю оставшуюся ширину и иначе
        // упирается прямо в «Экспорт».
        var nameWrap = new StackPanel { Margin = new Thickness(0, 0, 18, 0) };
        nameWrap.Children.Add(new TextBlock
        {
            Text = _duo ? Loc.T("pool.friendName") : Loc.T("pool.name"),
            Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0xA0, 0xB2)), FontSize = 10, FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 3)
        });
        nameWrap.Children.Add(_nameBox);

        top.Children.Add(nameWrap);

        // Напарник — ОТДЕЛЬНОЙ строкой под именем. В одной строке с кнопками он
        // не помещался и рисовался под ними: кнопки справа забирают около
        // трёхсот точек, имени нужно ещё двести сорок. Поле ввода ника отсюда
        // убрано совсем — вписывать его руками незачем, человек выбирается из
        // списка тех, с кем правда стояли в очереди.
        var header = new StackPanel();
        header.Children.Add(top);
        if (_duo) header.Children.Add(MateRow());

        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        root.Children.Add(new ScrollViewer { Content = _body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        var chrome = PoolUi.Chrome(this, Title, root, resizable: true);
        Content = chrome;
        WindowScale.Apply(this, chrome, 620, 560, 520, 380);
        RenderBody();
    }

    private void RenderBody()
    {
        _body.Children.Clear();
        if (_duo)
        {
            _body.Children.Add(ModeToggle());
            if (_manual)
            {
                // Ручные связки: список пар (мой + друга), каждая строка — одна связка.
                _body.Children.Add(SectionLabel(Loc.T("pool.duoManualHint")));
                _body.Children.Add(ManualHeader());
                foreach (var mp in _manualPairs.ToList()) _body.Children.Add(ManualPairRow(mp));
                _body.Children.Add(AddPairRow());   // пустая строка «+ +» — добавить связку
            }
            else
            {
                // Авто: наборы по ролям, лучшая пара подбирается движком.
                _body.Children.Add(SectionLabel(Loc.T("pool.mine")));
                foreach (var r in PoolSettingsWindow.Roles) _body.Children.Add(RoleRow(r, _mine));
                _body.Children.Add(SectionLabel(Loc.T("pool.friendPool")));
                foreach (var r in PoolSettingsWindow.Roles) _body.Children.Add(RoleRow(r, _friend));
            }
        }
        else
        {
            foreach (var r in PoolSettingsWindow.Roles) _body.Children.Add(RoleRow(r, _mine));
        }
    }

    // Выгрузить пул в файл — чтобы отдать напарнику. Выгружаем то, что сейчас
    // НА ЭКРАНЕ, вместе с несохранёнными правками: человек отдаёт то, что видит,
    // и объяснять ему разницу между показанным и записанным незачем.
    private void Export()
    {
        // Пустой пул отдавать нельзя: файл запишется, а загрузить его не выйдет —
        // на той стороне он отвергается как «ничего внутри». Раньше программа
        // молча сохраняла пустоту, и человек узнавал об этом только от друга.
        var hasChamps = _duo
            ? _mine.Values.Concat(_friend.Values).Any(l => l.Any(id => id != 0))
              || _manualPairs.Any(p => p.Mine != 0 || p.Friend != 0)
            : _mine.Values.Any(l => l.Any(id => id != 0));
        if (!hasChamps)
        {
            Confirm.Tell(this, Loc.T("pool.export"), Loc.T("pool.exportEmpty"));
            return;
        }

        var json = _duo
            ? PoolFile.Export(new DuoPool
            {
                FriendName = _name, Mine = Clone(_mine), Friend = Clone(_friend),
                Manual = _manual,
                ShareSecret = ShareSecretForExport(),
                // Пустые заготовки связок наружу не отдаём — как и при сохранении.
                ManualPairs = _manualPairs.Where(p => p.Mine != 0 || p.Friend != 0).ToList(),
            })
            : PoolFile.Export(new ChampPool { Name = _name, ByRole = Clone(_mine) });

        // Сначала код: строку можно просто кинуть в мессенджер, а файл нужен не
        // всегда — он теперь за кнопкой в том же окне.
        PoolShare.Show(this, json, PoolFile.SuggestName(_name));
    }

    // Окно выбора чемпионов — тоже не модальное (чтобы окна под ним двигались),
    // и потому открыто одно за раз, а результат читаем после закрытия.
    private ChampionPickerWindow? _picker;

    private void OpenPicker(ChampionPickerWindow win, Action<ChampionPickerWindow> done)
    {
        if (_picker is { IsVisible: true }) { _picker.Activate(); return; }
        _picker = win;
        win.Owner = this;
        win.Closed += (_, _) => { if (IsVisible) done(win); };
        win.Show();
    }

    // ── Действия (с подтверждением) ──────────────────────────────────────────
    private void Back()
    {
        if (_dirty && !Confirm.Ask(this, Loc.T("pool.back"), Loc.T("pool.confirmBack"))) return;
        Close();
    }

    private void Reset()
    {
        if (!Confirm.Ask(this, Loc.T("pool.reset"), Loc.T("pool.confirmReset"))) return;
        foreach (var r in PoolSettingsWindow.Roles) { _mine[r].Clear(); _friend[r].Clear(); }
        _manual = false; _manualPairs.Clear();
        _dirty = true;
        RenderBody();
    }

    /// <summary>
    /// Секрет для выгрузки: свой, если есть, иначе новый — и он тут же
    /// запоминается в своём пуле, не дожидаясь «Сохранить».
    ///
    /// Секрет обязан быть ОДИН на двоих, а единственный момент, когда он
    /// попадает ко второму, — это выгрузка. Отдали код, а пул не сохранили —
    /// и у напарника остался бы ключ, которого у нас уже нет.
    /// </summary>
    private string ShareSecretForExport()
    {
        if (_shareSecret.Length == 0) _shareSecret = DuoShare.NewSecret();
        if (_srcDuo is not null && _srcDuo.ShareSecret != _shareSecret)
        {
            _srcDuo.ShareSecret = _shareSecret;
            PoolStore.Persist();
        }
        return _shareSecret;
    }

    private void Save()
    {
        if (!Confirm.Ask(this, Loc.T("pool.save"), Loc.T("pool.confirmSave"))) return;
        var a = PoolStore.Current();
        if (_duo)
        {
            var d = _srcDuo ?? new DuoPool();
            d.FriendName   = _name;
            d.FriendNick   = _nick.Trim();
            // Человек, за которым следует пул. Стираем ТОЛЬКО по явному
            // «Забыть»: иначе пустое поле однажды молча снесёт хозяина, а
            // чем это кончается, уже видели.
            if (_matePuuid.Length > 0)   d.FriendPuuid = _matePuuid;
            else if (_mateCleared)       d.FriendPuuid = "";
            d.Mine         = Clone(_mine);
            d.Friend       = Clone(_friend);
            d.Manual      = _manual;
            // Сохраняем только заполненные связки (хотя бы один чемпион).
            d.ManualPairs = _manualPairs.Where(p => p.Mine != 0 || p.Friend != 0)
                                        .Select(p => new ManualDuoPair { Mine = p.Mine, MineRole = p.MineRole,
                                                                         Friend = p.Friend, FriendRole = p.FriendRole }).ToList();
            // Не только новый: окно больше не модальное, и пул могли удалить из
            // списка, пока он открыт здесь. Сохранение возвращает его на место.
            // Секрет не затираем пустым: он мог родиться при выгрузке, а
            // мог приехать в чужом файле — потерять его значит оборвать
            // обмен наигранностью молча.
            if (_shareSecret.Length > 0) d.ShareSecret = _shareSecret;
            if (!a.DuoPools.Contains(d)) a.DuoPools.Add(d);
        }
        else
        {
            var p = _srcPool ?? new ChampPool();
            p.Name  = _name;
            p.ByRole = Clone(_mine);
            if (!a.Pools.Contains(p)) a.Pools.Add(p);
        }
        PoolStore.Persist();
        Saved = true;
        Close();
    }

    private static Dictionary<string, List<int>> Clone(Dictionary<string, List<int>> src) =>
        src.ToDictionary(kv => kv.Key, kv => new List<int>(kv.Value));

    // Кнопки — единый тёмный стиль (PoolUi). Сохранить — с синим акцентом.
    private static Button ActionBtn(string text, bool primary = false)
    {
        var b = PoolUi.Btn(text);
        b.Margin = new Thickness(6, 0, 0, 0);
        b.FontWeight = primary ? FontWeights.Bold : FontWeights.Normal;
        if (primary)
        {
            b.Background = new SolidColorBrush(Color.FromArgb(0x33, 0x5A, 0x8A, 0xC8));
            b.BorderBrush = new SolidColorBrush(PoolSettingsWindow.Blue);
            b.Foreground = Brushes.White;
        }
        return b;
    }
}
