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

    // ── Драфт ───────────────────────────────────────────────────────────────
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

    // ── Баны ────────────────────────────────────────────────────────────────
    public bool BansTierList { get; set; } = true;     // тир-лист под списком банов

    // ── Хранение ────────────────────────────────────────────────────────────
    private static string Path_ => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Counterplay", "settings.json");

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

    /// Сохранить и сообщить окну, что пора перерисоваться.
    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path_)!);
            File.WriteAllText(Path_, JsonSerializer.Serialize(Current, JsonOpts));
        }
        catch { /* не критично: настройки просто не переживут перезапуск */ }
        Changed?.Invoke();
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
