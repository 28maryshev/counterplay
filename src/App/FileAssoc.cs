using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Counterplay;

/// <summary>
/// Связка расширения <c>.cpool</c> (файл пула чемпионов) с программой: двойной
/// клик по присланному файлу открывает Counterplay и предлагает загрузить пул.
///
/// Без этого Windows про расширение ничего не знает и предлагает выбрать
/// программу из списка — человек выбирал блокнот и видел столбик номеров
/// чемпионов, решив, что файл испорчен.
///
/// Пишем только в HKCU: программа ставится на пользователя, права администратора
/// ей не нужны и просить их ради ассоциации нельзя.
/// </summary>
public static class FileAssoc
{
    public const string Extension = ".cpool";
    private const string ProgId   = "Counterplay.Pool";

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    private const int  SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_IDLIST       = 0x0000;

    /// <summary>
    /// Команда, которая ДОЛЖНА стоять в реестре. Берём реальный exe, а не стаб:
    /// стаб теряет аргументы (см. Autostart), а здесь весь смысл в аргументе —
    /// пути к файлу. Папка <c>current</c> между обновлениями не меняется.
    /// </summary>
    private static string? DesiredCommand =>
        Autostart.AppExePath is { } exe ? $"\"{exe}\" \"%1\"" : null;

    /// <summary>
    /// Прописать связку, если её ещё нет или она устарела. Зовётся на каждом
    /// старте: путь меняется при переустановке, а спрашивать человека об этом
    /// незачем. Ничего не делает у неустановленной сборки (запуск из папки
    /// разработки не должен перехватывать расширение у настоящей программы).
    /// </summary>
    public static void EnsureRegistered()
    {
        if (!Autostart.Supported) return;              // не установка — не трогаем систему
        if (DesiredCommand is not { } cmd) return;

        try
        {
            using var classes = Registry.CurrentUser.OpenSubKey(@"Software\Classes", writable: true);
            if (classes is null) return;

            // Уже записано то же самое — выходим молча, чтобы не дёргать оболочку.
            using (var open = classes.OpenSubKey($@"{ProgId}\shell\open\command"))
                if (open?.GetValue(null) as string == cmd) return;

            using (var ext = classes.CreateSubKey(Extension))
                ext.SetValue(null, ProgId);

            using (var prog = classes.CreateSubKey(ProgId))
            {
                prog.SetValue(null, "Counterplay champion pool");
                using (var icon = prog.CreateSubKey("DefaultIcon"))
                    icon.SetValue(null, $"\"{Autostart.AppExePath}\",0");
                using (var open = prog.CreateSubKey(@"shell\open\command"))
                    open.SetValue(null, cmd);
            }

            // Без этого проводник узнает о новой связке только после перезахода.
            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
            Log.Write($"расширение {Extension} связано с программой");
        }
        catch (Exception ex) { Log.Write($"связка {Extension} не записалась: {ex.Message}"); }
    }
}
