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

// Окно драфта — общее: состояние драфта, движок, конструктор, кнопки шапки и мелкие помощники.
// Остальное разложено по областям в соседних файлах OverlayWindow.*.cs.
public partial class OverlayWindow : Window
{
    private IReadOnlyList<Recommendation>? _lastRecs;

    private IReadOnlyList<BanRec>?         _lastBans;

    private ChampionTraits.Arch?           _allyStyle;    // выраженный стиль моей команды

    private Dictionary<string, string>     _comboColors = [];  // связка → цвет её полоски

    private Dictionary<string, int>        _comboIndex  = [];  // связка → её скобка в списке

    private List<ComboCard>                _myComboCards = [];  // связки команды без наведения

    private List<int>                      _allyIdsNow  = [];  // чемпионы союзников сейчас

    private DraftState?                    _lastDraft;    // с применёнными ручными ролями

    private DraftState?                    _lastRawDraft; // как пришёл из LCU

    private RecommendationEngine?          _engine;

    private HashSet<int>                    _ownedChamps = new(); // чемпионы аккаунта (пусто = данных нет)

    private int                             _duoOffPool;          // напарник взял мимо половины — чей пик уже записан в журнал

    // Список чемпионов аккаунта из LCU: которых нет — помечаем «нет чемпиона».
    public void SetOwnedChampions(IEnumerable<int> ids) => Dispatcher.InvokeAsync(() =>
    {
        _ownedChamps = ids.ToHashSet();
        RenderCurrentState();   // перерисовать, если рекомендации уже на экране
    });

    /// Ранняя привязка движка — чтобы окно настроек пула считало WR/дельту связок
    /// ещё на экране Ready, до старта драфта (там _engine иначе ещё не выставлен).
    public void SetEngine(RecommendationEngine engine) => UseEngine(engine);

    /// <summary>
    /// Поставить движок и выбросить всё, что из него посчитано.
    ///
    /// Тир-лист и пул роли считаются ОДИН раз и кэшируются — список статичен в
    /// пределах патча. Но движок за сеанс меняется: скачали свежую базу, человек
    /// перешёл в другой ранг (бакет свой у каждого). Раньше кэш при этом
    /// оставался от прежнего движка; а если он успел посчитаться, когда данных
    /// ещё не было, пустой список оставался до перезапуска — и тир-лист в банах
    /// просто не показывался.
    /// </summary>
    private void UseEngine(RecommendationEngine? engine)
    {
        if (engine is null || ReferenceEquals(engine, _engine)) return;
        _engine = engine;
        _tierCols = null;
        _banTierCols = null;
        _rolePoolCells = null;
        _rolePoolRole = "";
    }

    /// Песочница (dotnet run test): клиента LoL рядом нет, а если он и запущен —
    /// он к тесту не относится. В этом режиме окно живёт там, куда его поставил
    /// человек: не прыгает к окну клиента в начале драфта и не уходит в трей,
    /// когда клиент свёрнут или закрыт. Боевой путь этим флагом не затронут.
    public bool SandboxMode { get; set; }

    // Путь к логотипу рядом с приложением (если положен).
    private static string LogoPath =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "assets", "logo.ico");

    public OverlayWindow()
    {
        InitializeComponent();

        // Логотип окна (если есть assets\logo.ico) — иначе стандартный.
        try { if (File.Exists(LogoPath)) Icon = new BitmapImage(new Uri(LogoPath)); }
        catch { /* битый ico — пропускаем */ }

        // Стартуем в полном режиме с явными размерами
        SizeToContent = SizeToContent.Manual;
        Width  = Scaled(FullW);
        Height = Scaled(FullH);
        MinWidth  = Scaled(MinW);
        MinHeight = Scaled(MinH);

        Left = SystemParameters.PrimaryScreenWidth - FullW - 24;
        Top  = 60;

        // Запоминаем ручной ресайз, чтобы перерисовки его не сбрасывали.
        SizeChanged += OnWindowSizeChanged;

        // Слежение за окном клиента (свернуть/развернуть/переместить).
        StartWindowFollow();

        // Настройки применяем при запуске и на каждое изменение — окно настроек
        // остаётся открытым, эффект виден сразу.
        AppSettings.Changed += () => Dispatcher.InvokeAsync(ApplySettings);
        ApplySettings();

        // Чип языка: показываем текущий, меню — по клику.
        Loc.LanguageChanged += OnLanguageChanged;

        // Сообщение с сайта на плашке «BETA»: показываем сохранённое сразу и
        // спрашиваем сайт — так новость доходит без обновления программы.
        StartNoticeWatch();

        // Подсказка режима пула в лобби: следит за составом пати сама.
        InitPoolHint();

        // Журнал умеет спросить у окна, в каком состоянии всё было на момент
        // копирования, — иначе присланный хвост событий читается вслепую.
        Log.Snapshot = () => Dispatcher.CheckAccess()
            ? BuildSnapshot()
            : Dispatcher.Invoke(BuildSnapshot);
    }

    // Клик по ссылке в плашке беты — открываем в системном браузере.
    private void OpenLink(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch { /* нет браузера/URL битый — молча игнорируем */ }
        e.Handled = true;
    }

    // Каждое нажатие, дошедшее до окна, и то, на чём оно оказалось.
    //
    // Нужно для жалоб вида «нажимаю, а ничего»: без этой записи нельзя отличить
    // три совершенно разных случая — клик не дошёл до окна вовсе, дошёл, но мимо
    // кнопки, или дошёл до кнопки, а та отказалась работать. Preview идёт сверху
    // вниз, поэтому видит нажатие раньше всех, кто может его перехватить.
    private void OnAnyClick(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(this);
        var el = e.OriginalSource as FrameworkElement;
        var name = string.IsNullOrEmpty(el?.Name) ? e.OriginalSource?.GetType().Name ?? "?" : el!.Name;
        Log.Write($"клик ({p.X:0};{p.Y:0}) → {name}; пик {Vis(PickBar)}, бан {Vis(BanBar)}");
    }

    private static string Vis(UIElement el) => el.Visibility == Visibility.Visible ? "виден" : "скрыт";

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        _userMoved = true; // дальше окно стоит там, куда его поставил пользователь
        DragMove();
        Log.Write($"окно перетащили в ({Left:0};{Top:0})");
    }

    // Кнопка-пин: вернуть оверлей к правому краю окна клиента
    private void OnPin(object sender, RoutedEventArgs e)
    {
        _userMoved = false;
        AnchorToClient();
    }

    // Крестик сворачивает окно в трей (значок остаётся). Окно не вернётся само
    // при событиях LCU — только по двойному клику в трее. Полный выход — пункт
    // «Выход» в меню значка.
    /// Свернуть в трей: программа продолжает работать и сама поднимется в драфте.
    private void OnMinimize(object sender, RoutedEventArgs e) => HideToTray(userInitiated: true);

    /// Крестик закрывает программу по-настоящему — но спрашивает. Раньше он
    /// прятал окно в трей, и человек, желавший выйти, оставлял её работать.
    private void OnQuit(object sender, RoutedEventArgs e)
    {
        if (ConfirmWindow.Ask(Loc.T("quit.question"), Loc.T("quit.yes"), Loc.T("quit.no"), this))
            System.Windows.Application.Current.Shutdown();
    }

    private void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Hide();
    }

    // Ширина полоски показателя (трек ~226px). Дельта масштабируется так, что
    // сильные значения (≈+17пп синергии) почти заполняют полоску.
    private static double ToBar(double delta) => Math.Max(0, Math.Min(214, delta * 13));

    // Базовый WR после темпера упирается в ~+3пп, поэтому у него своя шкала:
    // +3 = полная полоса (значит +2.5 ≈ 83%), иначе строка выглядела бы пустой.
    private static double ToBaseBar(double delta) => Math.Max(0, Math.Min(214, delta * 71));

    private static string Signed(double v)    => (v >= 0 ? "+" : "") + v.ToString("F1");
}
