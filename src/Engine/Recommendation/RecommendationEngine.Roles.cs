using Microsoft.Data.Sqlite;

namespace Counterplay;

// Движок подбора — роли: перевод названий клиента и базы, доля роли, главная роль чемпиона.
public sealed partial class RecommendationEngine
{
    // LCU position → ключ роли в БД
    public static string LcuToDbRole(string pos) => pos.ToLowerInvariant() switch
    {
        "top"     => "top",
        "jungle"  => "jungle",
        "middle"  => "mid",
        "bottom"  => "adc",
        "utility" => "support",
        _         => pos
    };

    /// Обратно: db-роль → позиция в терминах LCU. Нужно, чтобы посчитать пик
    /// НАПАРНИКА его же ролью — движок берёт роль из DraftState.MyPosition.
    public static string DbToLcuRole(string role) => role.ToLowerInvariant() switch
    {
        "top"     => "top",
        "jungle"  => "jungle",
        "mid"     => "middle",
        "adc"     => "bottom",
        "support" => "utility",
        _         => role
    };

    private readonly Dictionary<int, Dictionary<string, double>> _roleShareCache = new();

    /// Доля игр чемпиона на роли (по base_wr, взвешенно по патчам), с кэшем.
    /// Публична: оверлей использует её для авто-раскладки ролей вражеской команды.
    public double RoleShare(int champId, string role)
    {
        if (!_roleShareCache.TryGetValue(champId, out var shares))
        {
            shares = new Dictionary<string, double>();
            try
            {
                var cmd = _db.CreateCommand();
                cmd.CommandText = $@"
                    SELECT role, SUM(games*{PW}) FROM base_wr
                    WHERE champion_id=@c AND tier_bucket=@t AND patch IN (@p1,@p2,@p3)
                    GROUP BY role";
                cmd.Parameters.AddWithValue("@c",  champId);
                cmd.Parameters.AddWithValue("@t",  TierBucket);
                cmd.Parameters.AddWithValue("@p1", _p1);
                cmd.Parameters.AddWithValue("@p2", _p2);
                cmd.Parameters.AddWithValue("@p3", _p3);
                double total = 0;
                var raw = new Dictionary<string, double>();
                using var rd = cmd.ExecuteReader();
                while (rd.Read())
                {
                    var g = rd.GetDouble(1);
                    raw[rd.GetString(0)] = g;
                    total += g;
                }
                if (total > 0)
                    foreach (var (r, g) in raw) shares[r] = g / total;
            }
            catch { /* нет данных — пустые доли */ }
            _roleShareCache[champId] = shares;
        }
        return shares.GetValueOrDefault(role, 0.0);
    }

    private readonly Dictionary<int, string> _roleCache = new();

    // Самая частая роль чемпиона по числу игр (base_wr), с кэшем.
    // Публична: настройки пула дефолтят роль чемпиона при выборе в связку.
    public string PrimaryRole(int champId) => InferPrimaryRole(champId);

    private string InferPrimaryRole(int champId)
    {
        if (_roleCache.TryGetValue(champId, out var cached)) return cached;
        var role = "";
        try
        {
            var cmd = _db.CreateCommand();
            cmd.CommandText = $@"
                SELECT role FROM base_wr
                WHERE champion_id=@c AND tier_bucket=@t AND patch IN (@p1,@p2,@p3)
                GROUP BY role ORDER BY SUM(games*{PW}) DESC LIMIT 1";
            cmd.Parameters.AddWithValue("@c",  champId);
            cmd.Parameters.AddWithValue("@t",  TierBucket);
            cmd.Parameters.AddWithValue("@p1", _p1);
            cmd.Parameters.AddWithValue("@p2", _p2);
            cmd.Parameters.AddWithValue("@p3", _p3);
            role = cmd.ExecuteScalar() as string ?? "";
        }
        catch { /* нет данных — пустая роль */ }
        _roleCache[champId] = role;
        return role;
    }
}
