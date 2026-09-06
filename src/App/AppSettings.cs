using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Counterplay;

/// <summary>
/// Пользовательские настройки интерфейса: что показывать на каждом экране.
/// Лежат рядом с пулами (%APPDATA%\Counterplay\settings.json) и переживают
/// обновления программы.
///
/// Принцип: настройка ВЫКЛЮЧАЕТ элемент, а не включает. По умолчанию всё
/// включено — человек, который в настройки не заходил, видит программу целиком;
/// а тот, кому мешает конкретный блок, гасит именно его.
/// </summary>
public sealed class AppSettings
{
    // ── Экран ожидания ──────────────────────────────────────────────────────
    public bool ReadyRank    { get; set; } = true;   // эмблема, ник, ранг, LP, прогресс
    public bool ReadyLast5   { get; set; } = true;   // последние 5 игр с LP
    public bool ReadyWinrate { get; set; } = true;   // блок «винрейт + график»
    public bool ReadyPool    { get; set; } = true;   // пул чемпионов и его режимы
    public bool ReadyChamps  { get; set; } = true;   // полоса «винрейт за 30 дней»
    public bool ReadyBeta    { get; set; } = true;   // плашка беты и поддержки
    public bool ReadyPhase   { get; set; } = true;   // строка фазы (Готов / Лобби / Драфт)

    /// Что рисовать на графике: "winrate" — процент побед, "rating" — движение
    /// ранга по дивизионам, "off" — только цифры без графика.
    public string ChartMode { get; set; } = "winrate";

    /// Окно графика в днях: 30 — текущая форма, 90 — тренд за сезон.
    public int ChartDays { get; set; } = 90;

    /// Очередь, которая открывается по умолчанию: "last" — последняя выбранная.
    public string DefaultQueue { get; set; } = "last";

    /// Компактная карточка ранга: мельче эмблема, без полосы прогресса.
    public bool ReadyCompact { get; set; }

    // ── Драфт (продолжение) ─────────────────────────────────────────────────
    /// Сколько рекомендаций показывать в списке.
    public int DraftCount { get; set; } = 6;

    /// Показывать чемпионов, которых нет на аккаунте (красная рамка).
    public bool DraftUnowned { get; set; } = true;

    /// Поменять команды местами: моя справа, враги слева.
    public bool DraftMirror { get; set; }

    // ── Общие ───────────────────────────────────────────────────────────────
    /// Непрозрачность окна, 0.5…1.0.
    public double Opacity { get; set; } = 1.0;

    /// Всегда поверх других окон. По умолчанию оверлей уходит за окно клиента,
    /// когда активен клиент, — так он не закрывает собой пол-экрана.
    public bool AlwaysOnTop { get; set; }

    /// Не прятаться в трей на время игры. Riot разрешает подсказки только в
    /// драфте, поэтому в игре окно остаётся пустым — это лишь про то, видно его
    /// или нет.
    public bool KeepDuringGame { get; set; }

    /// Масштаб интерфейса: 0.9 / 1.0 / 1.15.
    public double FontScale { get; set; } = 1.0;

    // ── Драфт ───────────────────────────────────────────────────────────────
    /// Подбор целиком. Выключен — в драфте окно уходит в трей, а программа
    /// остаётся информационной панелью: ранг, форма, чемпионы за месяц.
    public bool DraftEnabled    { get; set; } = true;

    public bool DraftRolePool   { get; set; } = true;  // все чемпионы роли до пика
    public bool DraftReasons    { get; set; } = true;  // текстовые доводы в карточках
    public bool DraftMetrics    { get; set; } = true;  // полоски показателей
    public bool DraftItems      { get; set; } = true;  // предметы против кандидата
    public bool DraftHover      { get; set; } = true;  // подсветки при наведении
    public bool DraftCombos     { get; set; } = true;  // связки команды и скобки
    public bool DraftDamage     { get; set; } = true;  // полоса физ/маг урона
    public bool DraftArch       { get; set; } = true;  // значки архетипа и стиль команды
    public bool DraftSideIcons  { get; set; } = true;  // синергии у своих / контры у врагов
    public bool DraftRunes      { get; set; } = true;  // панель рун и сборки после пика

    // ── Окно в драфте ───────────────────────────────────────────────────────
    /// Куда ставить оверлей, когда начинается драфт:
    ///   "right"    — справа от клиента (как было);
    ///   "cover"    — точно поверх окна клиента;
    ///   "allies"   — поверх, но левая часть клиента (наши пики) видна;
    ///   "center"   — поверх, но центр с сеткой чемпионов виден;
    ///   "remember" — там, где окно стояло в конце прошлого драфта.
    public string DraftPlacement { get; set; } = "right";

    /// Запомненное положение окна (для "remember"). 0 = ещё не запоминали.
    public double DraftLeft   { get; set; }
    public double DraftTop    { get; set; }
    public double DraftWidth  { get; set; }
    public double DraftHeight { get; set; }

    // ── Баны ────────────────────────────────────────────────────────────────
    public bool BansTierList { get; set; } = true;     // тир-лист под списком банов

    // ── Хранение ────────────────────────────────────────────────────────────
    // ОТДЕЛЬНЫЙ файл, не settings.json: там живёт свободный key-value стор
    // (язык, автозапуск), который пишется как JsonObject. Сериализация нашего
    // класса в тот же файл затирала бы соседние ключи — язык слетал бы при
    // первом же изменении настроек интерфейса.
    private static string Path_ => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Counterplay", "ui.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
        { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static AppSettings? _current;

    public static AppSettings Current
    {
        get
        {
            if (_current != null) return _current;
            try
            {
                if (File.Exists(Path_))
                    _current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path_));
            }
            catch { /* битый файл — вернёмся к значениям по умолчанию */ }
            return _current ??= new AppSettings();
        }
    }

    /// Сохранить молча — без перерисовки оверлея. Для служебных записей вроде
    /// запомненного положения окна: они не меняют вид, а перерисовка посреди
    /// драфта дорога и заметна.
    public static void SaveQuiet()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path_)!);
            var tmp = Path_ + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Current, JsonOpts));
            File.Move(tmp, Path_, overwrite: true);
        }
        catch { /* не критично */ }
    }

    /// Сохранить и сообщить окну, что пора перерисоваться.
    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path_)!);
            // Пишем во временный файл и подменяем: обрыв на середине записи
            // (выключили питание, убили процесс) не оставит обрезанный JSON,
            // который потом не прочитается.
            var tmp = Path_ + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Current, JsonOpts));
            File.Move(tmp, Path_, overwrite: true);
        }
        catch { /* не критично: настройки просто не переживут перезапуск */ }
        Changed?.Invoke();
    }

    /// Копия для правки в окне настроек: пока не нажали «Применить», оверлей
    /// живёт по старым значениям и не дёргается на каждый щелчок.
    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    /// Принять отредактированную копию: сохранить и перерисовать оверлей.
    public static void Apply(AppSettings edited)
    {
        _current = edited;
        Save();
    }

    /// Вернуть всё к «показывать целиком».
    public static void Reset()
    {
        _current = new AppSettings();
        Save();
    }

    /// Настройки изменились — оверлей применяет их без перезапуска.
    public static event Action? Changed;
}
