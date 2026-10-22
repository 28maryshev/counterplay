using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace Counterplay;

/// <summary>
/// Учётные данные LCU из командной строки процесса LeagueClientUx — запасной
/// путь, когда lockfile не годится.
///
/// Китайский клиент (Tencent, WeGame) оставляет lockfile пустым всегда, а порт
/// и пароль передаёт только аргументами `--app-port=` и `--remoting-auth-token=`.
/// Обычный клиент передаёт их так же, так что путь выручает и там, где lockfile
/// не нашёлся: клиент запущен от администратора (папку процесса тогда не
/// прочитать) и стоит не в обычном месте.
/// </summary>
public static partial class LcuCommandLine
{
    private const string ProcName = "LeagueClientUx";

    /// Учётные данные запущенного клиента или null, если клиента нет или его
    /// командную строку не прочитать.
    public static LcuCredentials? TryRead()
    {
        foreach (var proc in Process.GetProcessesByName(ProcName))
        {
            try
            {
                if (Get(proc.Id) is { } cmd && Parse(cmd, proc.Id) is { } creds) return creds;
            }
            catch { /* процесс закрылся или доступ запрещён — пробуем следующий */ }
            finally { proc.Dispose(); }
        }
        return null;
    }

    // Аргумент целиком, а не подстрока: рядом стоит `--riotclient-app-port=`
    // с портом клиента Riot, и он идёт раньше нужного.
    [GeneratedRegex("""(?:^|[\s"])--app-port=(\d+)""")]
    private static partial Regex PortArg();

    [GeneratedRegex("""(?:^|[\s"])--remoting-auth-token=([^\s"]+)""")]
    private static partial Regex TokenArg();

    /// Достаёт порт и пароль из командной строки. null — нет одного из двух.
    public static LcuCredentials? Parse(string cmd, int pid)
    {
        var port  = PortArg().Match(cmd);
        var token = TokenArg().Match(cmd);
        if (!port.Success || !token.Success) return null;
        if (!int.TryParse(port.Groups[1].Value, out var p) || p is <= 0 or > 65535) return null;
        return new LcuCredentials("LeagueClient", pid, p, token.Groups[1].Value, "https");
    }

    // ── Командная строка чужого процесса ──────────────────────────────────
    // ProcessCommandLineInformation (Windows 8.1+) требует только права
    // PROCESS_QUERY_LIMITED_INFORMATION. Их дают и на процесс, запущенный от
    // администратора, так что программе без прав клиент WeGame тоже виден.
    // Путь через WMI этого не умеет и к тому же медленный.

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int  ProcessCommandLineInformation = 60;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        SafeProcessHandle process, int infoClass, IntPtr info, int length, out int returned);

    private static string? Get(int pid)
    {
        using var h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (h.IsInvalid) return null;

        // Первый вызов сообщает нужный размер, второй заполняет буфер.
        NtQueryInformationProcess(h, ProcessCommandLineInformation, IntPtr.Zero, 0, out var len);
        if (len <= 0) return null;

        var buf = Marshal.AllocHGlobal(len);
        try
        {
            if (NtQueryInformationProcess(h, ProcessCommandLineInformation, buf, len, out _) != 0)
                return null;
            // В начале буфера UNICODE_STRING: длина в байтах, затем (после
            // выравнивания) указатель на строку — она лежит в том же буфере.
            var bytes = (ushort)Marshal.ReadInt16(buf);
            var str   = Marshal.ReadIntPtr(buf, IntPtr.Size);
            return str == IntPtr.Zero ? null : Marshal.PtrToStringUni(str, bytes / 2);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }
}
