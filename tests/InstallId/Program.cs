using System.IO;
using System.Text;
using Counterplay;

namespace Counterplay.Tests;

/// <summary>
/// Идентификатор установки. Раньше он считался от ПЕРВОГО сетевого адаптера и
/// менялся вместе с сетью: включили VPN — компьютер приходил на сервер как
/// новый. Теперь он один раз вычисляется и ложится в файл, а дальше берётся
/// оттуда.
///
/// Проверка работает с настоящим файлом в %APPDATA% — другого пути к нему нет.
/// Прежнее содержимое сохраняем и возвращаем на место, в том числе при падении.
/// </summary>
internal static class Program
{
    private static int _fails;

    private static string Path_ => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Counterplay", "install.id");

    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Log.FileDisabled = true;   // не сорить в журнал игрока

        var existed = File.Exists(Path_);
        var before = existed ? File.ReadAllText(Path_) : null;
        Console.WriteLine(existed
            ? $"файл уже был: {before!.Trim()[..8]}… (вернём как было)"
            : "файла не было (создадим и уберём за собой)");

        try
        {
            // ── 1. Первый вызов: файла нет — значение вычисляется и сохраняется
            Wipe();
            var first = Telemetry.DeviceId();
            Check("идентификатор похож на настоящий",
                  first.Length is >= 6 and <= 64 && first != "unknown", first);
            Check("файл появился", File.Exists(Path_), "");
            Check("в файле лежит ровно он", File.ReadAllText(Path_).Trim() == first, "");

            // ── 2. Повторный вызов даёт то же самое
            Check("второй вызов совпадает", Telemetry.DeviceId() == first, "");

            // ── 3. ГЛАВНОЕ: сохранённое значение важнее вычисленного.
            // Подкладываем чужой идентификатор — его и должны получить, что бы
            // ни творилось с сетевыми адаптерами.
            const string pinned = "0123456789abcdef0123456789abcdef";
            File.WriteAllText(Path_, pinned);
            Check("сохранённое значение побеждает вычисленное",
                  Telemetry.DeviceId() == pinned, Telemetry.DeviceId());

            // ── 4. Преемственность: у давнего пользователя файла нет, и в него
            // должен лечь ТОТ ЖЕ идентификатор, под которым его знает сервер, —
            // то есть вычисленный по старому правилу.
            Wipe();
            var fresh = Telemetry.DeviceId();
            Check("без файла считается по-старому (тот же, что был у сервера)",
                  fresh == first, $"{fresh} против {first}");

            // ── 5. Мусор в файле не должен становиться идентификатором
            File.WriteAllText(Path_, "   ");
            var afterJunk = Telemetry.DeviceId();
            Check("пустой файл игнорируется", afterJunk == first, afterJunk);
            Check("и переписывается вычисленным",
                  File.ReadAllText(Path_).Trim() == first, "");
        }
        finally
        {
            // Возвращаем машину в исходное состояние при любом исходе.
            if (existed) File.WriteAllText(Path_, before!);
            else Wipe();
            Console.WriteLine(existed ? "прежний файл возвращён" : "файл убран");
        }

        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "ИТОГ: идентификатор устойчив" : $"ИТОГ: провалено — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    private static void Wipe()
    {
        try { if (File.Exists(Path_)) File.Delete(Path_); } catch { /* занят — дальше видно будет */ }
    }

    private static void Check(string what, bool ok, string detail)
    {
        Console.WriteLine($"  [{(ok ? "ок" : "ПЛОХО")}] {what}{(detail.Length > 0 ? "  — " + detail : "")}");
        if (!ok) _fails++;
    }
}
