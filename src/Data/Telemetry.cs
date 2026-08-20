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
    // Должен совпадать с TELEMETRY_SHARED_SECRET на сервере (в .env сайта).
    private const string Secret = "dev-telemetry-secret";

    public static async Task PingAsync()
    {
        try
        {
            var version = typeof(Telemetry).Assembly.GetName().Version?.ToString() ?? "0";
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            http.DefaultRequestHeaders.Add("x-telemetry-secret", Secret);
            var json = JsonSerializer.Serialize(new { installId = DeviceId(), version });
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            await http.PostAsync(Url, content);
        }
        catch { /* телеметрия не критична — молчим */ }
    }

    // Стабильный обезличенный идентификатор устройства: хэш MAC; если MAC нет —
    // случайный GUID, сохранённый в %APPDATA%\Counterplay\install.id.
    private static string DeviceId()
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

        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Counterplay", "install.id");
            if (File.Exists(path)) return File.ReadAllText(path).Trim();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var id = Guid.NewGuid().ToString("N");
            File.WriteAllText(path, id);
            return id;
        }
        catch { return "unknown"; }
    }
}
