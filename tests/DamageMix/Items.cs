using Counterplay;

namespace Counterplay.Tests;

/// <summary>
/// Предметы «контрят тебя и команду»: показываем только то, что во вражеском
/// составе есть КОМУ купить, и только то, что против этого пика правда работает.
///
/// Владелец на живом драфте: «у них танк только Наутилус, который вряд ли
/// соберёт эти итемы, а на топе Триндамир, который пойдёт в керри». Потом:
/// «надо разнообразить на все защитные итемы — например, если у врага энчантер,
/// под Раммусом показывать Микаэля».
///
/// Второе требует осторожности: действие предметов сверено с их описаниями в
/// item.json, и на сверке отвалились два предположения по памяти. Микаэль
/// снимает контроль «КРОМЕ подброса и подавления» — значит Мальфиту он не
/// ответ, зато Раммусу с его провокацией ответ. Ртуть, наоборот, подавление
/// снимает, а подброс — нет.
/// </summary>
internal static class Items
{
    // Свои: четыре мага, кандидат — пятый. Перекос, за который «накажут».
    private static readonly int[] ApTeam = [142, 68, 112, 32];   // Зои, Рамбл, Виктор, Амуму
    private const int Seraphine = 147;

    private const int Nautilus = 111, Trynda = 23, Kalista = 429, Brand = 63, Yone = 777, Zed = 238;
    private const int Ornn = 516, Galio = 3;
    private const int Rammus = 33, Malphite = 54, Malzahar = 90, Soraka = 16, Lulu = 117,
                      Aatrox = 266, Jinx = 222, Thresh = 412, Ahri = 103;

    // Составы врага. Названия — по тому, КТО там есть: от этого и зависит,
    // какой предмет вообще кто-то купит.
    private static readonly int[] WithEnchanter = [Soraka, Brand, Kalista, Nautilus, Zed];
    private static readonly int[] NoEnchanter   = [Brand, Kalista, Trynda, Zed, Yone];
    private static readonly int[] OnlyCarries   = [Kalista, Brand, Yone, Trynda, Zed];
    private static readonly int[] TwoTanks      = [Nautilus, Ornn, Kalista, Brand, Yone];
    private static readonly int[] FighterAndTanks = [Aatrox, Zed, Nautilus, Ornn, Rammus];
    private static readonly int[] TwoEnchanters   = [Soraka, Lulu, Nautilus, Rammus, Ornn];

    // ID предметов названы здесь заново нарочно: если их переписать в движке,
    // проверка должна заметить это сама, а не повторить ошибку следом.
    private const int Mikael = 3222, Mercurial = 3139, Banshee = 3102, EdgeOfNight = 3814,
                      Morello = 3165, MortalReminder = 3033, Chempunk = 6609,
                      GuardianAngel = 3026, Zhonya = 3157,
                      SerpentFang = 6695, Steelcaps = 3047;
    // Сопротивление — предметы передней линии, ради которых список и правился.
    private static readonly int[] Resists = [3075, 3143, 3110, 3065, 2504, 4401];

    /// Возвращает число провалов; сами проверки печатает переданный Check.
    public static int Run(Action<string, bool, string> check)
    {
        var fails = 0;
        void Say(string what, bool ok, string detail)
        {
            check(what, ok, detail);
            if (!ok) fails++;
        }

        string Names(IEnumerable<int> ids) =>
            string.Join(", ", ids.Select(i => ItemName(i)));

        // ── Случай владельца: сопротивление берёт передняя линия ──────────
        var one = ItemValue.CounterItems(Seraphine, ApTeam, [Nautilus, Trynda, Kalista, Brand, Yone]);
        Say("с одним танком сопротивления не предлагаем",
            !one.Intersect(Resists).Any(), Names(one));

        var two = ItemValue.CounterItems(Seraphine, ApTeam, TwoTanks);
        Say("с двумя — предупреждаем", two.Intersect(Resists).Any(), Names(two));

        var carries = ItemValue.CounterItems(Seraphine, ApTeam, OnlyCarries);
        Say("против одних кэрри — тоже нет", !carries.Intersect(Resists).Any(), Names(carries));

        // Врагов ещё не показали — показывать нечего. Владелец увидел обратное
        // на живом драфте: состав врага пуст, а строка предметов уже полна.
        // Предупреждение без того, кто его исполнит, — выдумка.
        var blind = ItemValue.CounterItems(Seraphine, ApTeam, []);
        Say("пока врагов не видно — предметов нет", blind.Count == 0, $"{blind.Count} шт.");

        // Галио — и маг, и танк: он и в передней линии, и Морелло ему по руке.
        Say("танк-маг считается передней линией", ChampionTraits.IsTanky(Galio), "да");

        // ── Пример владельца: Раммус и Микаэль ─────────────────────────────
        var ram = ItemValue.CounterItems(Rammus, [], WithEnchanter);
        Say("Раммусу при вражеском энчантере — Микаэль", ram.Contains(Mikael), Names(ram));

        var ramNo = ItemValue.CounterItems(Rammus, [], NoEnchanter);
        Say("   без энчантера покупать его некому", !ramNo.Contains(Mikael), Names(ramNo));

        // ── Микаэль не снимает подброс ─────────────────────────────────────
        // Мальфит и Раммус оба «жёсткий контроль», но ответ у них разный.
        var malph = ItemValue.CounterItems(Malphite, [], WithEnchanter);
        Say("Мальфиту Микаэль НЕ ответ — там подброс", !malph.Contains(Mikael), Names(malph));
        Say("   ответ — щит от заклинания",
            malph.Contains(Banshee) || malph.Contains(EdgeOfNight), Names(malph));

        // ── Подавление: Микаэль мимо, Ртуть по делу ────────────────────────
        // Ртутный ЯТАГАН, а не кушак: в сборках лежат только готовые предметы,
        // заготовок там нет вовсе — кушак не встретился ни у одной связки.
        var malz = ItemValue.CounterItems(Malzahar, [], WithEnchanter);
        Say("Малзахару — Ртуть, а не Микаэль",
            malz.Contains(Mercurial) && !malz.Contains(Mikael), Names(malz));

        // ── Хук ────────────────────────────────────────────────────────────
        var thresh = ItemValue.CounterItems(Thresh, [], WithEnchanter);
        Say("на хук — щит от заклинания",
            thresh.Contains(Banshee) || thresh.Contains(EdgeOfNight), Names(thresh));

        // ── Гривус идёт к своему покупателю ────────────────────────────────
        var sorMage = ItemValue.CounterItems(Soraka, [], WithEnchanter);
        Say("хилу при вражеском маге — Морелло", sorMage.Contains(Morello), Names(sorMage));
        Say("   и стрелку своё — Напоминание", sorMage.Contains(MortalReminder), Names(sorMage));

        var sorFight = ItemValue.CounterItems(Soraka, [], FighterAndTanks);
        Say("магов нет, есть боец — Бензопила вместо Морелло",
            sorFight.Contains(Chempunk) && !sorFight.Contains(Morello), Names(sorFight));

        // Энчантеру отдельного гривуса не нужно: он и сам маг, ему Морелло.
        // Химтех-гнилушку из таблицы убрали — её не собирает НИКТО (0 связок
        // из 668), и предлагать её значило бы врать.
        var sorEnch = ItemValue.CounterItems(Soraka, [], TwoEnchanters);
        Say("у врага энчантеры — им Морелло", sorEnch.Contains(Morello), Names(sorEnch));

        Say("   и гривусов не больше двух сразу",
            sorMage.Count(i => i is Morello or MortalReminder or Chempunk) <= 2,
            Names(sorMage));

        // ── Взрыв ──────────────────────────────────────────────────────────
        // Зед тегами не размечен вовсе — его опознаёт класс «Assassin».
        var zed = ItemValue.CounterItems(Zed, [], WithEnchanter);
        Say("убийце-физику — Ангел-хранитель", zed.Contains(GuardianAngel), Names(zed));

        var ahri = ItemValue.CounterItems(Ahri, [], WithEnchanter);
        Say("магу-бёрсту — Песочные часы", ahri.Contains(Zhonya), Names(ahri));

        // ── Автоатакующему керри ───────────────────────────────────────────
        var jinx = ItemValue.CounterItems(Jinx, [], TwoTanks);
        Say("против крит-стрелка — Знамение и Ледяное сердце",
            jinx.Contains(3143) && jinx.Contains(3110), Names(jinx));

        var jinxNoTank = ItemValue.CounterItems(Jinx, [], OnlyCarries);
        Say("   без передней линии их брать некому",
            !jinxNoTank.Contains(3143) && !jinxNoTank.Contains(3110), Names(jinxNoTank));

        // ── Щиты ───────────────────────────────────────────────────────────
        var lulu = ItemValue.CounterItems(Lulu, [], WithEnchanter);
        Say("щитовику — Клык змея", lulu.Contains(SerpentFang), Names(lulu));

        // ── Автоатаки ──────────────────────────────────────────────────────
        var trynd = ItemValue.CounterItems(Trynda, [], TwoTanks);
        Say("автоатакующему — Стальные набойки", trynd.Contains(Steelcaps), Names(trynd));

        // ── Предмет должен быть в сборке того, кто его якобы купит ─────────
        //
        // Владелец: «будут возмущаться, что Посох серафима на ЛеБлан или Зайру
        // никто не собирает — почему он там вообще показывается». Класс говорит,
        // кому предмет ПО РУКЕ, а сборки — кто его правда берёт.
        //
        // Сборки тут выдуманные и заданы прямо здесь: проверяется не то, что
        // отдаёт сервер, а то, что подбор его слушается.
        var enemies = new[] { Nautilus, Ornn, Kalista, Brand, Yone };
        IReadOnlyCollection<int>? Nothing(int _) => Array.Empty<int>();
        IReadOnlyCollection<int>? Unknown(int _) => null;

        var byClass = ItemValue.CounterItems(Seraphine, ApTeam, enemies, Unknown);
        var byBuild = ItemValue.CounterItems(Seraphine, ApTeam, enemies, Nothing);
        Say("никто ничего не собирает — предметов нет", byBuild.Count == 0, $"{byBuild.Count} шт.");
        Say("   а по классу они были", byClass.Count > 0, $"{byClass.Count} шт.");

        // Оставим в сборках ровно один предмет — он один и должен остаться.
        var only = byClass[0];
        IReadOnlyCollection<int>? OnlyOne(int _) => new[] { only };
        var kept = ItemValue.CounterItems(Seraphine, ApTeam, enemies, OnlyOne);
        Say("остаётся ровно то, что собирают",
            kept.Count == 1 && kept[0] == only, Names(kept));

        // Данных по одному врагу нет — про него судим по классу, как раньше.
        IReadOnlyCollection<int>? AllButOrnn(int id) => id == Ornn ? null : [];
        var partial = ItemValue.CounterItems(Seraphine, ApTeam, enemies, AllButOrnn);
        Say("про кого сборок нет — тот судится по классу", partial.Count > 0, Names(partial));

        // ── Иконка есть у каждого предмета, какой может выпасть ────────────
        // Пока список иконок жил отдельной строкой, новый предмет выпадал в
        // подбор, а иконки к нему не было — и он молча пропадал с экрана.
        var known = ItemValue.All.ToHashSet();
        var orphans = new HashSet<int>();
        int[][] comps = [WithEnchanter, NoEnchanter, OnlyCarries, TwoTanks,
                         FighterAndTanks, TwoEnchanters, []];
        foreach (var champ in DataDragon.GetAllIconUrls().Keys)
            foreach (var comp in comps)
                foreach (var item in ItemValue.CounterItems(champ, ApTeam, comp))
                    if (!known.Contains(item)) orphans.Add(item);
        Say("у каждого предмета из подбора есть иконка", orphans.Count == 0,
            orphans.Count == 0 ? "да" : string.Join(", ", orphans));

        var notPreloaded = ItemValue.All.Except(ItemIcons.Ids).ToList();
        Say("   и она качается заранее, а не на лету", notPreloaded.Count == 0,
            notPreloaded.Count == 0 ? "да" : string.Join(", ", notPreloaded));

        return fails;
    }

    /// Название предмета, если справочник загружен; иначе сам id.
    private static string ItemName(int id)
    {
        var n = ItemIcons.NameOf(id);
        return n.StartsWith('#') ? id.ToString() : n;
    }
}
