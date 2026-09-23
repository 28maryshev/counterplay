using System.IO;
using System.Text;
using Microsoft.Data.Sqlite;
using Counterplay;

namespace Counterplay.Tests;

/// <summary>
/// Удержание патча и тир-лист.
///
/// В день выхода патча данных по нему ещё нет — движок обязан остаться на
/// прошлом. Проверка удержания смотрела в жёстко заданный «emerald», а
/// побакетная база содержит ТОЛЬКО свой бакет: у золота, серебра и мастера там
/// нули. Ноль у прошлого патча читался как «делить не на что, значит новый
/// готов» — и удержание молча выключалось. Тир-лист считается по одному
/// текущему патчу и оказывался пустым, а пустой список в интерфейсе означает
/// «полосу спрятать».
///
/// Работаем на НАСТОЯЩЕЙ базе (другой под рукой нет), ничего в ней не меняя.
/// </summary>
internal static class Program
{
    private static int _fails;

    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Log.FileDisabled = true;   // не сорить в журнал игрока

        var db = RecommendationEngine.FindDb();
        if (db is null) { Console.WriteLine("базы нет — проверять нечего"); return 0; }
        Console.WriteLine($"база: {db}");

        // Какие патчи и бакеты в ней лежат.
        using (var con = new SqliteConnection($"Data Source={db}"))
        {
            con.Open();
            var cmd = con.CreateCommand();
            cmd.CommandText = @"SELECT patch, tier_bucket, SUM(games) FROM base_wr
                                GROUP BY patch, tier_bucket ORDER BY patch DESC LIMIT 6";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                Console.WriteLine($"  патч {r.GetString(0),6}  бакет {r.GetString(1),-8} игр {r.GetInt64(2),10:N0}");
        }

        foreach (var bucket in new[] { "silver", "gold", "emerald", "master" })
        {
            using var engine = RecommendationEngine.Create(db, bucket);
            var tiers = engine.TierList(perRole: 15);
            var roles = tiers.Select(t => t.Role).Distinct().Count();

            Console.WriteLine($"\nбакет {bucket}: патчи {engine.PatchDisplay}, "
                              + $"в тир-листе {tiers.Count} чемпионов на {roles} ролях");

            // Главное: тир-лист не должен быть пустым. Пустой = полоса скрыта.
            Check($"{bucket}: тир-лист не пустой", tiers.Count > 0, $"{tiers.Count}");
            Check($"{bucket}: роли на месте", roles >= 5, $"{roles}");
        }

        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "ИТОГ: тир-лист считается" : $"ИТОГ: провалено — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    private static void Check(string what, bool ok, string detail)
    {
        Console.WriteLine($"  [{(ok ? "ок" : "ПЛОХО")}] {what}{(detail.Length > 0 ? "  — " + detail : "")}");
        if (!ok) _fails++;
    }
}
