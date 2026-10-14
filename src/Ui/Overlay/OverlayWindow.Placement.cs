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

// Окно драфта — размер и место окна: полный и компактный вид, масштаб, место на время
// драфта, удержание на экране, изменение размера за край.
public partial class OverlayWindow
{
    // Компактный вид убран из шапки: окно всегда полное, а прячется в трей.
    private readonly bool _isFullMode = true;

    private const double FullW    = 1320;

    // Высота по умолчанию: чтобы в боковой колонке целиком помещалась первая
    // карточка TEAM COMBOS (заголовок + описание + how-to-play).
    private const double FullH    = 760;

    private const double CompactW = 320;

    private const double IdleW    = 340;   // компактное окно режима ожидания

    private const double MinW     = 1240;

    // Запомненное окно уже этого — не раскладка драфта: три колонки (две по
    // 282 и центр) в него не влезают. Так выглядит панель профиля (IdleW),
    // снятая концом драфта, — с ней следующий драфт открывался одной колонкой
    // своих, а центр и враги уезжали за край.
    private const double RememberMinW = 800;

    private static bool HasRememberedDraftSize(AppSettings s) =>
        s.DraftWidth >= RememberMinW && s.DraftHeight > 100;

    private const double MinH     = 200;

    // Запоминаем размер полного режима при переключении в компактный
    private double _savedFullW = FullW;

    private double _savedFullH = FullH;

    // Привязка к окну клиента LoL: ставим один раз при появлении, дальше
    // окно свободно перетаскивается. Как только пользователь сдвинул вручную —
    // больше не привязываем (до нажатия кнопки-пина).
    private bool _userMoved;

    // Ready-экран растёт по контенту (SizeToContent.Height). Прокрутки внутри
    // нет — окно может быть выше клиента LoL, это нормально; ограничиваем его
    // только рабочей областью экрана, чтобы низ не ушёл под панель задач.
    //
    // Ограничение касается ТОЛЬКО авто-высоты. В драфте высоту задаёт человек, и
    // потолок там мешал: окно упиралось в него при растягивании вниз.
    private void LimitHeightToClient()
    {
        if (SizeToContent != SizeToContent.Height)
        {
            MaxHeight = double.PositiveInfinity;
            return;
        }
        var wa = SystemParameters.WorkArea;
        var max = wa.Bottom - Math.Max(wa.Top, Top) - 8;
        MaxHeight = Math.Max(MinH, max);
    }

    // Разрешения из настроек клиента LoL → масштаб нашего окна. Опорное — 1920:
    // под него нарисован интерфейс. Ниже 0.8 не опускаемся: текст в карточках
    // становится нечитаемым раньше, чем окно перестаёт мешать.
    // Опорное — 1280×720: самое ходовое окно клиента, под него и рисовался
    // интерфейс. Дальше растём пропорционально: на 1920 всё должно быть заметно
    // крупнее, иначе рядом с большим клиентом оверлей выглядит игрушечным.
    // Базовый кегль обязательного текста Riot (в XAML стоит он же).
    private const double DisclaimerBase = 7.5;

    private static readonly (int Width, double Scale)[] ClientScales =
        [(1024, 0.84), (1280, 1.00), (1600, 1.18), (1920, 1.35)];

    /// Масштаб содержимого: выбор человека × подгонка под клиент, но НЕ больше
    /// того, что помещается в окно. Без верхней границы увеличенные карточки
    /// вылезали за край окна и обрезались — три колонки драфта требуют ширины
    /// FullW на единичном масштабе.
    private void ApplyScale()
    {
        var scale = AppSettings.Current.FontScale * ClientScale();
        var w = ActualWidth > 0 ? ActualWidth : Width;
        var draftView = _lastDraft is not null || FullView.Visibility == Visibility.Visible;

        // В драфте масштаб НЕ режем по ширине окна: окно само расширяется под
        // нужный масштаб и при необходимости уходит за край экрана. Резать здесь
        // значило бы делать шрифт мельче, чем в клиенте, ради того, чтобы всё
        // влезло, — а именно этого мы и не хотим.
        // Панель профиля — другое дело: она узкая и всегда целиком на экране.
        if (!draftView && w > 0) scale = Math.Min(scale, w / IdleW);

        RootGrid.LayoutTransform = Math.Abs(scale - 1.0) < 0.01
            ? System.Windows.Media.Transform.Identity
            : new ScaleTransform(scale, scale);

        // Обязательный текст Riot растёт вместе со всем остальным, и на крупном
        // клиенте занимал заметную часть панели. Он мелкий по смыслу: его надо
        // показать, а не читать. Растём вдвое медленнее общего масштаба — на
        // маленьком окне размер прежний, на большом текст не разъезжается.
        // Совсем зафиксировать нельзя: при ×1.5 он стал бы нечитаемым.
        Disclaimer.FontSize = DisclaimerBase * (1 + (Math.Max(1.0, scale) - 1) * 0.5) / Math.Max(1.0, scale);
        if (Math.Abs(scale - _loggedScale) > 0.005)
        {
            Log.Write($"масштаб {scale:0.00} (окно {w:0}px, вид {(draftView ? "драфт" : "панель")}, " +
                      $"клиент ×{ClientScale():0.00}, ручной ×{AppSettings.Current.FontScale:0.00})");
            _loggedScale = scale;
        }
    }

    private double _loggedScale = -1;

    // ── Снимок состояния для журнала ────────────────────────────────────────

    private (double X, double Y) Dpi()
    {
        var src = System.Windows.PresentationSource.FromVisual(this);
        return src?.CompositionTarget is { } ct
            ? (ct.TransformToDevice.M11, ct.TransformToDevice.M22) : (1, 1);
    }

    /// Насколько сильно вписывать окно в экран. В драфте окно широкое и по
    /// договорённости может уходить за край — держим только шапку; панель
    /// профиля узкая, ей уезжать незачем.
    private void KeepReachable()
    {
        if (_lastDraft is not null || FullView.Visibility == Visibility.Visible) KeepHeaderOnScreen();
        else ClampToScreen();
    }

    /// Окну можно уходить за правый край — но не целиком: на экране должна
    /// оставаться шапка, иначе окно нечем схватить и вернуть.
    private void KeepHeaderOnScreen()
    {
        var wa = SystemParameters.WorkArea;
        const double GrabW = 220;   // столько шапки оставляем видимой
        var (l0, t0) = (Left, Top);
        Left = Math.Max(wa.Left - Math.Max(0, (ActualWidth > 0 ? ActualWidth : Width) - GrabW),
                        Math.Min(Left, wa.Right - GrabW));
        Top  = Math.Max(wa.Top, Math.Min(Top, wa.Bottom - 40));
        if (Math.Abs(l0 - Left) > 1 || Math.Abs(t0 - Top) > 1)
            Log.Write($"подтянул шапку в экран: ({l0:0};{t0:0}) → ({Left:0};{Top:0})");
    }

    /// Полное вписывание в экран — для панели профиля: она узкая, ей уезжать
    /// за край незачем.
    private void ClampToScreen()
    {
        var wa = SystemParameters.WorkArea;
        var w = ActualWidth > 0 ? ActualWidth : Width;
        var h = ActualHeight > 0 ? ActualHeight : Height;
        if (double.IsNaN(w) || double.IsNaN(h) || w <= 0 || h <= 0) return;

        var (l0, t0, w0, h0) = (Left, Top, w, h);
        if (w > wa.Width)  { Width  = w = wa.Width;  }
        if (h > wa.Height) { Height = h = wa.Height; }
        Left = Math.Max(wa.Left, Math.Min(Left, wa.Right  - w));
        Top  = Math.Max(wa.Top,  Math.Min(Top,  wa.Bottom - h));
        if (Math.Abs(l0 - Left) > 1 || Math.Abs(t0 - Top) > 1 ||
            Math.Abs(w0 - w) > 1 || Math.Abs(h0 - h) > 1)
            Log.Write($"вписал в экран: было ({l0:0};{t0:0}) {w0:0}×{h0:0} → ({Left:0};{Top:0}) {w:0}×{h:0}");
    }

    /// Размер в пикселях с учётом подгонки под клиент: окно и содержимое должны
    /// расти и уменьшаться вместе, иначе появляются пустые поля по краям.
    private double Scaled(double px) => px * ClientScale();

    /// Масштаб под окно клиента. "auto" — по фактической ширине окна LoL, иначе
    /// по выбранному в настройках разрешению.
    private double ClientScale() => ClientScaleFor(this);

    /// <summary>
    /// То же для окон ВНЕ оверлея (настройки пулов, редактор, выбор чемпионов):
    /// человек выставил размер под клиент один раз, и он должен работать везде, а
    /// не только на драфтовой панели. <paramref name="forDpi"/> нужен только чтобы
    /// перевести пиксели Win32 в DIP; null — считаем, что DPI единичный.
    /// </summary>
    public static double ClientScaleFor(System.Windows.Media.Visual? forDpi)
    {
        var pick = AppSettings.Current.ClientSize;
        if (pick != "auto")
            return int.TryParse(pick, out var w)
                ? ClientScales.MinBy(x => Math.Abs(x.Width - w)).Scale : 1.0;

        if (!TryGetClientRect(out var r)) return 1.0;   // клиента не видно — как есть
        double dpiX = 1;
        if (forDpi is not null &&
            System.Windows.PresentationSource.FromVisual(forDpi)?.CompositionTarget is { } ct)
            dpiX = ct.TransformToDevice.M11;
        var width = (r.Right - r.Left) / dpiX;
        return ClientScales.MinBy(x => Math.Abs(x.Width - width)).Scale;
    }

    // ── Раскладка окна на время драфта ────────────────────────────────────
    // Клиент у всех стоит по-разному: если он посреди экрана, оверлей справа от
    // него наполовину уезжает за край монитора, и окно приходится таскать руками
    // каждый драфт. Поэтому на старте драфта ставим его по выбранному правилу.

    private bool _placementApplied;   // раскладка этого драфта уже применена

    /// Запомнить положение окна — вызывается перед уходом в трей в конце драфта.
    private void RememberDraftPlacement()
    {
        if (AppSettings.Current.DraftPlacement != "remember") return;
        // Запоминаем только «драфтовую» геометрию: если раскладка ещё не
        // применялась, окно стоит в виде сайдбара, и такие размеры вспоминать
        // на следующем драфте бессмысленно.
        if (!_placementApplied || Width <= 100 || Height <= 100) return;
        // Окно сейчас панель профиля, а не драфт: посреди драфта его ужимал
        // экран ожидания. Запомнить такое — открыть следующий драфт в 340 px.
        if (IdlePanel.Visibility == Visibility.Visible || Width < RememberMinW)
        {
            Log.Write($"раскладку не запоминаю: окно {Width:0}×{Height:0} — не вид драфта");
            return;
        }
        var s = AppSettings.Current;
        // Пишем, ЧТО запоминаем и что было до этого: размер иногда подменялся
        // между раскладкой и концом драфта, и по одному лишь «раскладка
        // «запомненная»» виновника было не найти.
        if (Math.Abs(s.DraftWidth - Width) > 1 || Math.Abs(s.DraftHeight - Height) > 1)
            Log.Write($"запоминаю раскладку: {Width:0}×{Height:0} в ({Left:0};{Top:0}) "
                      + $"— было {s.DraftWidth:0}×{s.DraftHeight:0}");
        s.DraftLeft = Left; s.DraftTop = Top;
        s.DraftWidth = Width; s.DraftHeight = Height;
        AppSettings.SaveQuiet();
    }

    /// Поставить окно по правилу из настроек. Ничего не делает, если окно уже
    /// подвинули руками в этом же драфте.
    private void ApplyDraftPlacement()
    {
        if (SandboxMode)
        {
            // В песочнице окно НЕ переезжает — но ширины ему хватить должно:
            // три колонки драфта в узкой панели профиля не помещаются. Растим
            // на месте,левый верхний угол не трогаем.
            var need = FullW * AppSettings.Current.FontScale * ClientScale();
            if (Width < need - 1)
            {
                _settingSize = true;
                SizeToContent = SizeToContent.Manual;
                MaxHeight = double.PositiveInfinity;
                MinWidth = Math.Min(MinW, need);
                Width = need;
                _settingSize = false;
                Log.Write($"песочница: расширил окно до {need:0}px, положение не трогаю");
            }
            _placementApplied = true;
            ApplyScale();
            return;
        }

        if (_placementApplied) return;
        var mode = AppSettings.Current.DraftPlacement;
        if (mode == "right") { _placementApplied = true; AnchorIfNotMoved(); return; }

        if (mode == "remember")
        {
            var s = AppSettings.Current;
            // Узкое запомненное (см. RememberMinW) — как будто не запоминали:
            // такие уже лежат у игроков в настройках и ездят синхронизацией.
            if (HasRememberedDraftSize(s))
            {
                // Режим окна обязательно ручной: на экране ожидания высота
                // считается по контенту, и присвоенная высота просто терялась —
                // окно оставалось таким, каким его сделала прошлая раскладка.
                _settingSize = true;
                SizeToContent = SizeToContent.Manual;
                MaxHeight = double.PositiveInfinity;
                MinWidth  = Math.Min(MinW, s.DraftWidth);
                MinHeight = Math.Min(MinH, s.DraftHeight);
                Left   = s.DraftLeft;
                Top    = s.DraftTop;
                Width  = s.DraftWidth;
                Height = s.DraftHeight;
                _settingSize = false;

            }
            else
            {
                // Первый запуск: вспоминать нечего — раскладываем так, чтобы
                // сетка чемпионов оставалась открытой, а дальше человек подвинет
                // окно под себя, и это положение и запомнится.
                mode = "center";
            }
            if (mode == "remember")
            {
                // Монитор, на котором стояло окно, могли отключить — тогда
                // запомненные координаты ведут в пустоту, и без этой проверки
                // человек видит «программа не открывается» и ничего в журнале.
                KeepHeaderOnScreen();
                Log.Write($"раскладка «запомненная»: окно {Width:0}×{Height:0} в ({Left:0};{Top:0})");
                _placementApplied = true;
                ApplyScale();
                return;
            }
        }

        if (!TryGetClientRect(out var r)) return;   // клиента не видно — попробуем позже

        double dpiX = 1, dpiY = 1;
        if (System.Windows.PresentationSource.FromVisual(this)?.CompositionTarget is { } ct)
        {
            dpiX = ct.TransformToDevice.M11;
            dpiY = ct.TransformToDevice.M22;
        }
        double cl = r.Left / dpiX, cw = (r.Right - r.Left) / dpiX;
        double cTop = r.Top / dpiY, chh = (r.Bottom - r.Top) / dpiY;

        // Доля ширины клиента, которую оставляем открытой слева:
        //   cover  — ничего (окно ровно поверх клиента);
        //   allies — узкая полоса слева: колонка своих пиков и таймер;
        //   center — почти три четверти: сетка выбора чемпиона занимает середину
        //            окна клиента и заканчивается примерно на 70% его ширины.
        double keepLeft = mode switch { "allies" => 0.25, "center" => 0.73, _ => 0.0 };

        // Левый край — там, где заканчивается открытая часть клиента.
        var x0 = cl + cw * keepLeft;

        // Ширину берём такую, чтобы содержимое поместилось ЦЕЛИКОМ на выбранном
        // масштабе. За правый край экрана окно при этом выходить может — это
        // осознанно: лучше пусть часть уедет (человек подвинет или у него монитор
        // шире), чем всё ужмётся до нечитаемого. Раньше окно вписывалось в экран,
        // а следом под его ширину ужимался масштаб — и шрифт становился мельче,
        // чем в самом клиенте.
        var needed = FullW * AppSettings.Current.FontScale * ClientScale();
        var w = keepLeft > 0 ? Math.Max(needed, cw - cw * keepLeft) : Math.Max(needed, cw);

        _settingSize = true;
        SizeToContent = SizeToContent.Manual;   // иначе высота считается по контенту
        MaxHeight = double.PositiveInfinity;
        MinWidth  = Math.Min(MinW, w);
        Left   = x0;
        Top    = cTop;
        Width  = w;
        Height = Math.Max(MinH, chh);
        _settingSize = false;
        _placementApplied = true;
        KeepHeaderOnScreen();
        Log.Write($"раскладка «{mode}»: клиент {cw:0}×{chh:0}, окно {Width:0}×{Height:0} в ({Left:0};{Top:0})");
        ApplyScale();
    }

    // Привязка только при появлении и пока пользователь не двигал окно сам.
    private void AnchorIfNotMoved()
    {
        // Раскладка драфта уже расставила окно — больше не трогаем. Иначе любое
        // обновление содержимого (а их за драфт десятки) возвращало бы окно на
        // исходное место, и подвинуть его руками было невозможно.
        if (_placementApplied && AppSettings.Current.DraftPlacement != "right") return;
        if (!_userMoved) AnchorToClient();
    }

    private IntPtr Hwnd => new System.Windows.Interop.WindowInteropHelper(this).Handle;

    private enum ResDir { Left=1, Right=2, Top=3, TopLeft=4, TopRight=5, Bottom=6, BottomLeft=7, BottomRight=8 }

    private void StartResize(ResDir dir, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        _userMoved = true; // ручной ресайз — фиксируем положение пользователя
        MaxHeight  = double.PositiveInfinity;   // тянут вручную — потолок не мешаем
        SendMessage(Hwnd, 0x0112 /*WM_SYSCOMMAND*/, new IntPtr(0xF000 /*SC_SIZE*/ + (int)dir), IntPtr.Zero);
        Log.Write($"окно растянули: {Width:0}×{Height:0} в ({Left:0};{Top:0})");
        e.Handled = true;
    }

    // 8 обработчиков, по одному на каждое направление
    private void ResN (object s, MouseButtonEventArgs e) => StartResize(ResDir.Top,         e);

    private void ResS (object s, MouseButtonEventArgs e) => StartResize(ResDir.Bottom,      e);

    private void ResW (object s, MouseButtonEventArgs e) => StartResize(ResDir.Left,        e);

    private void ResE (object s, MouseButtonEventArgs e) => StartResize(ResDir.Right,       e);

    private void ResNW(object s, MouseButtonEventArgs e) => StartResize(ResDir.TopLeft,     e);

    private void ResNE(object s, MouseButtonEventArgs e) => StartResize(ResDir.TopRight,    e);

    private void ResSW(object s, MouseButtonEventArgs e) => StartResize(ResDir.BottomLeft,  e);

    private void ResSE(object s, MouseButtonEventArgs e) => StartResize(ResDir.BottomRight, e);

    // Идёт программная установка размера (RestoreModeSize/OnToggle): такие
    // SizeChanged НЕ запоминаем. Иначе клобер: RestoreModeSize ставит Width,
    // SizeChanged срабатывает с ЕЩЁ СТАРОЙ маленькой высотой (после айдл-экрана)
    // и затирает _savedFullH — следующая строка Height=_savedFullH читает уже
    // испорченное значение, и окно навсегда сжимается по высоте.
    private bool _settingSize;

    // Пользователь вручную растянул окно в активном полном режиме — запоминаем
    // новый размер. Иначе следующая же перерисовка (пик/бан/смена фазы) вызовет
    // RestoreModeSize и вернёт старый _savedFullW/_savedFullH, обнулив ресайз.
    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyScale();   // окно стало уже/шире — содержимое подстраивается под него

        if (!_settingSize
            && _isFullMode
            && SizeToContent == SizeToContent.Manual         // не idle/компакт (там авто-высота)
            && IdlePanel.Visibility != Visibility.Visible)   // не экран ожидания
        {
            _savedFullW = e.NewSize.Width;
            _savedFullH = e.NewSize.Height;
        }
    }

    // Возврат к размеру активного режима (полный/компактный) при появлении пиков.
    private void RestoreModeSize()
    {
        // Возвращаемся ли из экрана ожидания. Он ужимает окно до ширины сайдбара
        // (ShowIdle: SizeToContent.Height, Width = IdleW), и тогда «размер уже
        // задан раскладкой драфта» — неправда.
        //
        // Ровно на этом ломался ARAM: в первых снимках champ select скамейки ещё
        // нет и чемпион не роздан, подбирать не из чего — показывается ожидание.
        // Дальше скамейка приходит, кандидаты появляются, а окно так и остаётся
        // шириной с сайдбар, потому что восстановление размера пропускалось.
        var fromIdle = IdlePanel.Visibility == Visibility.Visible;

        StatusText.Visibility = Visibility.Visible;
        IdlePanel.Visibility  = Visibility.Collapsed;

        if (_isFullMode)
        {
            // Локальные копии + флаг: установка Width дергает SizeChanged до
            // установки Height, и без защиты сохранённая высота затиралась.
            var (w, h) = (_savedFullW, _savedFullH);

            // В режиме «запомнить» правду говорит СОХРАНЁННАЯ раскладка, а не
            // _savedFull*. Последний обновляется обработчиком изменения
            // размера, а во время раскладки тот нарочно заглушён — и там
            // остаётся размер прошлого драфта. Возврат режима подменял им
            // только что восстановленный, ПОЛОЖЕНИЕ при этом не трогая: ровно
            // то, что владелец и описал — «высота стала стандартной, а окно
            // осталось где стояло». Дальше он поправлял размер руками, и конец
            // драфта запоминал уже испорченное.
            var place = AppSettings.Current;
            if (place.DraftPlacement == "remember" && HasRememberedDraftSize(place))
                (w, h) = (place.DraftWidth, place.DraftHeight);

            _settingSize = true;
            SizeToContent = SizeToContent.Manual;
            MaxHeight     = double.PositiveInfinity;
            MinHeight = Scaled(MinH);
            // Размер уже задан раскладкой драфта — оставляем как есть. Иначе на
            // переходе «баны → пики» окно прыгало обратно к сохранённому размеру,
            // и раскладка выглядела так, будто у банов и пиков она разная.
            // MinWidth тоже не трогаем: он шире раскладки и растянул бы окно.
            //
            // Но если окно побывало на экране ожидания, оно сейчас шириной с
            // сайдбар, а не с раскладку — там восстанавливать обязательно.
            // _savedFullW/H как раз и сняты перед сворачиванием, то есть хранят
            // размер раскладки.
            if (fromIdle || !(_placementApplied && AppSettings.Current.DraftPlacement != "right"))
            {
                // MinWidth зажимаем по восстанавливаемой ширине — так же, как в
                // раскладке драфта. На крупном клиенте Scaled(MinW) шире
                // запомненного окна, и WPF растягивал его до этого минимума:
                // возврат с экрана ожидания делал окно шире, чем оно было.
                MinWidth = Math.Min(Scaled(MinW), w);
                if (Math.Abs(Width - w) > 1 || Math.Abs(Height - h) > 1)
                    Log.Write($"возврат размера режима: {Width:0}×{Height:0} → {w:0}×{h:0} "
                              + $"(с ожидания: {(fromIdle ? "да" : "нет")}, "
                              + $"раскладка применена: {(_placementApplied ? "да" : "нет")})");
                Width    = w;
                Height   = h;
            }
            _settingSize = false;
        }
        else
        {
            SizeToContent = SizeToContent.Height;
            MinWidth  = 0;
            MinHeight = 0;
            Width     = CompactW;
        }
        ApplyScale();   // вид сменился — пересчитываем под новую ширину
    }
}
