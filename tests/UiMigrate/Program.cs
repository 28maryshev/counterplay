using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using Counterplay;

namespace Counterplay.Tests;

/// <summary>
/// Разовые переносы ui.json при смене умолчаний (<c>AppSettings.Migrate</c>).
///
/// Файл пишется после каждого драфта, поэтому у всех, кто уже играл, старое
/// умолчание лежит на диске как явный выбор. Без переноса новое умолчание
/// увидели бы только чистые установки. Владелец 26.11.2026: строку «Готов ·
/// фаза» скрыть по умолчанию — у него самого она осталась бы на месте.
///
/// Проверяем: старый файл → строка скрыта и номер схемы поднят на диске;
/// файл новой схемы с включённой строкой → выбор человека не трогаем; файла
/// нет → по умолчанию скрыта; схема 0 → оба переноса сразу.
/// </summary>
internal static class Program
{
    private static int _fails;

    private static string Dir => AppPaths.Root;
    private static string UiPath => Path.Combine(Dir, "ui.json");

    private static int Main()
    {
        // Своя папка вместо папки игрока — до первого обращения к хранилищу.
        AppPaths.RootOverride = Path.Combine(Path.GetTempPath(), "counterplay-test-root", "UiMigrate");
        Directory.CreateDirectory(AppPaths.RootOverride);
        Console.OutputEncoding = Encoding.UTF8;
        Log.FileDisabled = true;

        try
        {
            // ── 1. Файл схемы 1, строка фазы включена старым умолчанием
            Console.WriteLine("старый файл (схема 1):");
            Load("""{ "ReadyPhase": true, "ChartDays": 30, "Rev": 1 }""");
            Check("строка фазы скрыта", !AppSettings.Current.ReadyPhase, "");
            Check("прочее не тронуто", AppSettings.Current.ChartDays == 30, $"{AppSettings.Current.ChartDays}");
            var disk = JsonNode.Parse(File.ReadAllText(UiPath))!;
            Check("перенос записан на диск", (bool?)disk["ReadyPhase"] == false && (int?)disk["Rev"] == 2,
                  $"ReadyPhase={disk["ReadyPhase"]}, Rev={disk["Rev"]}");

            // ── 2. Человек включил строку уже после переноса — это его выбор
            Console.WriteLine("\nфайл новой схемы, строку включили руками:");
            Load("""{ "ReadyPhase": true, "Rev": 2 }""");
            Check("строка фазы осталась включённой", AppSettings.Current.ReadyPhase, "");

            // ── 3. Чистая установка
            Console.WriteLine("\nфайла нет:");
            Load(null);
            Check("по умолчанию строка скрыта", !AppSettings.Current.ReadyPhase, "");
            Check("кнопка сброса тоже прячет", !AppSettings.Defaults().ReadyPhase, "");

            // ── 4. Совсем старый файл: оба переноса сразу
            Console.WriteLine("\nфайл схемы 0:");
            Load("""{ "ReadyPhase": true, "ChartMode": "winrate" }""");
            Check("график ранга вместо винрейта", AppSettings.Current.ChartMode == "rating",
                  AppSettings.Current.ChartMode);
            Check("и строка фазы скрыта", !AppSettings.Current.ReadyPhase, "");
        }
        finally
        {
            try { Directory.Delete(AppPaths.RootOverride!, recursive: true); } catch { /* занят — не страшно */ }
        }

        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "ИТОГ: переносы настроек работают" : $"ИТОГ: провалено — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    /// Подложить ui.json (null — убрать) и заставить настройки перечитаться.
    private static void Load(string? json)
    {
        if (json is null) { if (File.Exists(UiPath)) File.Delete(UiPath); }
        else File.WriteAllText(UiPath, json);
        AppSettings.Reload();
    }

    private static void Check(string what, bool ok, string detail)
    {
        Console.WriteLine($"  [{(ok ? "ок" : "ПЛОХО")}] {what}{(detail.Length > 0 ? "  — " + detail : "")}");
        if (!ok) _fails++;
    }
}
