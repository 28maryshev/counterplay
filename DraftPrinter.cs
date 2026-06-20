namespace Counterplay;

public static class DraftPrinter
{
    public static void Print(DraftState s, IReadOnlyList<Recommendation>? recs = null)
    {
        Console.WriteLine("──────────── ДРАФТ ────────────");
        Console.WriteLine($"Моя роль: {Role(s.MyPosition)}");

        Console.WriteLine("Союзники:");
        foreach (var p in s.MyTeam)
            Console.WriteLine("  " + Describe(p));

        Console.WriteLine("Враги:");
        var i = 1;
        foreach (var p in s.TheirTeam)
            Console.WriteLine($"  Enemy {i++}: {Describe(p)}");

        if (s.DirectOpponent is { } opp)
            Console.WriteLine($"Прямой оппонент: {DataDragon.Name(opp.ChampionId)} ({Role(opp.Position)})");
        else
            Console.WriteLine("Прямой оппонент: роли врага не раскрыты.");

        Console.WriteLine($"Баны (мы):   {Bans(s.MyTeamBans)}");
        Console.WriteLine($"Баны (враг): {Bans(s.TheirTeamBans)}");

        Console.WriteLine();

        if (recs is null)
        {
            Console.WriteLine("  [Рекомендации] база данных не загружена.");
        }
        else if (recs.Count == 0)
        {
            if (string.IsNullOrEmpty(s.MyPosition))
                Console.WriteLine("  [Рекомендации] роль ещё не определена — жди назначения слота.");
            else
                Console.WriteLine($"  [Рекомендации] нет кандидатов для роли «{Role(s.MyPosition)}» в текущем патче/бакете.");
        }
        else
        {
            Console.WriteLine("─── РЕКОМЕНДАЦИИ ───");
            for (int n = 0; n < recs.Count; n++)
            {
                var r = recs[n];
                Console.WriteLine($"  {n + 1}. {DataDragon.Name(r.ChampionId),-16} score={Signed(r.Score)}");
                Console.WriteLine($"     база={Signed(r.BaseDelta)}%  " +
                                  $"vs опп={Signed(r.DirectDelta)}%  " +
                                  $"vs состав={Signed(r.OtherDelta)}%  " +
                                  $"синергия={Signed(r.SynergyDelta)}%");
                foreach (var reason in r.Reasons)
                    Console.WriteLine($"     • {reason}");
            }
        }

        Console.WriteLine();
    }

    private static string Describe(DraftPlayer p)
    {
        string champ;
        if (p.ChampionId != 0)        champ = DataDragon.Name(p.ChampionId);
        else if (p.PickIntentId != 0)  champ = $"ховер: {DataDragon.Name(p.PickIntentId)}";
        else                           champ = "—";

        var pos = string.IsNullOrEmpty(p.Position) ? "?" : Role(p.Position);
        var me  = p.IsLocalPlayer ? "  ← я" : "";
        return $"[{pos,-7}] {champ}{me}";
    }

    private static string Signed(double v) =>
        (v >= 0 ? "+" : "") + v.ToString("F1");

    private static string Bans(IReadOnlyList<int> b) =>
        b.Count == 0 ? "—" : string.Join(", ", b.Select(id => DataDragon.Name(id)));

    private static string Role(string pos) => pos switch
    {
        "top"     => "Top",
        "jungle"  => "Jungle",
        "middle"  => "Mid",
        "bottom"  => "ADC",
        "utility" => "Support",
        ""        => "?",
        _         => pos,
    };
}
