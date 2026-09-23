using System.Net.Http;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Counterplay;

/// Анонимная телеметрия для метрики активных пользователей (DAU/WAU) на сайте.
/// Отправляет при запуске обезличенный ID устройства (SHA256 от MAC — не обратимо
/// к самому MAC) и версию. Никаких игровых/персональных данных. Ошибки глушим.
public static class Telemetry
{
    private const string Url = "https://counterplays.com/api/telemetry";

    // Общий секрет с сервером. В КОД НЕ ПИШЕТСЯ: подставляется при сборке
    // (-p:TelemetrySecret=…, см. build/release.ps1) и попадает в сборку атрибутом.
    // Полноценной защитой это быть не может — из .exe строку всё равно достанут,
    // — но и лежать открытым текстом в публичном репозитории ей незачем.
    // Пусто (обычная dotnet build без параметра) — шлём без заголовка.
    private static readonly string Secret =
        typeof(Telemetry).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "TelemetrySecret")?.Value ?? "";

    public static async Task PingAsync()
    {
        try
        {
            var version = typeof(Telemetry).Assembly.GetName().Version?.ToString() ?? "0";
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            if (Secret.Length > 0)
                http.DefaultRequestHeaders.Add("x-telemetry-secret", Secret);
            var json = JsonSerializer.Serialize(
                new { installId = DeviceId(), version, installedAt = InstalledAt() });
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            await http.PostAsync(Url, content);
        }
        catch { /* телеметрия не критична — молчим */ }
    }

    /// Когда программа появилась на этом компьютере. Берём дату создания своей
    /// папки в %APPDATA% — она заводится при первом запуске и переживает
    /// обновления: для того, кто поставил Counterplay месяц назад, она месячной
    /// давности, для нового — сегодняшняя.
    ///
    /// Нужно это серверу: после того как база телеметрии была потеряна, каждый
    /// давний пользователь при следующем запуске выглядел как новая установка.
    /// Отличить их по одному лишь идентификатору нельзя — знает об этом только
    /// сама программа. Не получилось прочитать — не шлём ничего, сервер тогда
    /// решит по-старому.
    private static string? InstalledAt()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Counterplay");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            return Directory.GetCreationTimeUtc(dir).ToString("o");
        }
        catch { return null; }
    }

    /// <summary>
    /// Обезличенный идентификатор этого компьютера.
    ///
    /// ПЕРВЫМ делом смотрим сохранённый файл <c>%APPDATA%\Counterplay\install.id</c>,
    /// и только если его нет — вычисляем (хэш MAC, иначе случайный GUID) и
    /// сохраняем.
    ///
    /// Порядок был обратный, и это ломало счёт. Хэш брался от ПЕРВОГО
    /// не-loopback и не-tunnel сетевого адаптера, а «первый» — величина
    /// непостоянная: многие VPN-адаптеры числятся Ethernet, а не Tunnel, да и
    /// порядок перечисления ничем не закреплён. Включили VPN, переткнули кабель,
    /// выключили адаптер — идентификатор другой, и один и тот же компьютер
    /// приходил как новый. В базе сайта таких строк набралось 47 из 88 (замер
    /// 2026-09-23); в счёт установок они не попадали (сервер ловит их по дате
    /// появления программы), но статистику активных пользователей размывали.
    ///
    /// Преемственность не рвётся: у тех, кто уже пользуется программой, файла
    /// нет, при первом же запуске в него ляжет их нынешний хэш MAC — тот самый,
    /// под которым их знает сервер.
    /// </summary>
    public static string DeviceId()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Counterplay", "install.id");

        // Сохранённый идентификатор — главный источник. Мусор в файле (пустой,
        // обрезанный) игнорируем и считаем заново: пустой id сервер всё равно
        // отвергнет, и компьютер выпадет из статистики целиком.
        try
        {
            if (File.Exists(path))
            {
                var saved = File.ReadAllText(path).Trim();
                if (saved.Length is >= 6 and <= 64) return saved;
            }
        }
        catch { /* файл недоступен — вычислим ниже */ }

        var id = Computed();

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, id);
            Log.Write($"идентификатор установки сохранён ({id[..8]}…)");
        }
        catch { /* не записалось — ничего страшного, посчитаем снова в следующий раз */ }

        return id;
    }

    /// Вычислить идентификатор заново: хэш MAC, иначе случайный GUID.
    private static string Computed()
    {
        try
        {
            var mac = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                         && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                .Select(n => n.GetPhysicalAddress().ToString())
                .FirstOrDefault(s => !string.IsNullOrEmpty(s) && s != "000000000000");
            if (!string.IsNullOrEmpty(mac))
            {
                var bytes = SHA256.HashData(Encoding.UTF8.GetBytes("counterplay:" + mac));
                return Convert.ToHexString(bytes)[..32].ToLowerInvariant();
            }
        }
        catch { /* ниже фолбэк */ }

        return Guid.NewGuid().ToString("N");
    }
}
