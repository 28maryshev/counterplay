namespace Counterplay;

// Движок подбора — теги и иконки карточки пика (вместо строк доводов).
public sealed partial class RecommendationEngine
{
    // Пороги показа. Это не веса: на счёт они не влияют, только решают, есть ли
    // на карточке тег или иконка. Цифра в теге та же, что в счёте пика.
    private const double TAG_LANE_MIN    = 0.3;   // |линия| — меньше это ровный матчап
    private const double TAG_TEAM_MIN    = 0.5;   // |против команды|
    private const double TAG_SYN_GOOD    = 0.3;   // синергия «за» (как и цифра у связки)
    private const double TAG_SYN_BAD     = 0.5;   // синергия «против»
    private const double ENEMY_NAMED     = 0.3;   // враг попадает в подсказку «против команды»
    private const double ALLY_BAD        = 1.0;   // союзник, с которым заметно хуже
    private const double ICON_PERSONAL   = 1.5;   // личный винрейт — как у строки «твой винрейт»
    private const double ICON_PATCH_SHIFT = 1.5;  // сдвиг винрейта к прошлому патчу, пп
    private const double ICON_STYLE_TOOLS = 3;    // инструменты против их стиля — как у строки

    /// <summary>
    /// Теги и иконки показанного кандидата. Тегов с цифрой — не больше трёх:
    /// «линия» и «вслепую» вместе не бывают (вслепую оппонента нет); за ними —
    /// теги дыр состава без цифры (<see cref="NeedTags"/>: до двух зелёных и
    /// один красный у последнего пика). Иконки — сначала плюсы, потом связки
    /// с союзниками, потом минусы. <paramref name="open"/> — дыра, которая
    /// останется открытой, если последний пик её не закроет (<see cref="OpenGap"/>).
    /// </summary>
    private PickBadges BuildBadges(PickFacts f, TeamGap? open)
    {
        var tags = new List<PickTag>();
        var plus = new List<PickIcon>();
        var links = new List<PickIcon>();
        var minus = new List<PickIcon>();

        LaneTag(f, tags);
        TeamTag(f, tags);
        SynergyBadges(f, tags, links, minus);
        NeedTags(f, open, tags);
        PatchIcons(f, plus, minus);
        PlayerIcons(f, plus, minus);
        StyleIcons(f, plus, minus);
        CompIcons(f, plus, minus);
        TempoIcon(f, minus);
        DropRepeats(tags, plus, links);

        return new PickBadges(tags, [.. plus, .. links, .. minus]);
    }

    // ── Теги ───────────────────────────────────────────────────────────

    private static void LaneTag(PickFacts f, List<PickTag> tags)
    {
        if (f.LaneBlind)
        {
            if (f.Risk.Verdict == 0) return;
            var pct = $"{100 * f.Risk.Chance:F0}";
            tags.Add(new PickTag("blind", f.Risk.Verdict,
                Loc.T(f.Risk.Verdict > 0 ? "pickTag.blindSafe" : "pickTag.blindRisky", pct),
                Plain(BlindReason(f.Risk) ?? ""),
                100 * Math.Abs(f.Risk.Chance - f.Risk.Typical)));   // насколько шанс не как у всех, пп
            return;
        }
        if (f.LaneOppId == 0 && f.DuoOppId == 0) return;
        if (Math.Abs(f.Lane) < TAG_LANE_MIN) return;

        var tip = new List<string>();
        if (f.LaneOppId != 0)
        {
            var opp = DataDragon.Name(f.LaneOppId);
            var d = f.LaneOnly;
            tip.Add(d >= 2.0  ? Loc.T("reason.lineWinStrong", opp, $"{d:F1}")
                  : d >= 1.0  ? Loc.T("reason.lineWin", opp, $"{d:F1}")
                  : d >= 0.3  ? Loc.T("reason.lineEdge", opp)
                  : d <= -0.3 ? Loc.T("reason.lineHard", opp, $"{d:F1}")
                  : Loc.T("reason.lineNeutral", opp));
        }
        if (f.DuoOppId != 0 && !f.DuoBlind && Math.Abs(f.Duo) >= TAG_LANE_MIN)
            tip.Add(Loc.T(f.Duo > 0 ? "reason.botlaneGood" : "reason.botlaneBad", DataDragon.Name(f.DuoOppId)));

        tags.Add(new PickTag("lane", Math.Sign(f.Lane),
            Loc.T("pickTag.lane", Num(f.Lane)), string.Join("\n", tip), f.Lane));
    }

    private static void TeamTag(PickFacts f, List<PickTag> tags)
    {
        if (Math.Abs(f.Team) < TAG_TEAM_MIN) return;
        var good = f.Team > 0;
        var named = f.ByEnemy
            .Where(e => good ? e.Delta >= ENEMY_NAMED : e.Delta <= -ENEMY_NAMED)
            .OrderBy(e => good ? -e.Delta : e.Delta).Take(2)
            .Select(e => $"{DataDragon.Name(e.Id)} ({Num(e.Delta)}%)").ToList();
        var tip = named.Count == 0 ? Loc.T("pickTag.teamTip")
                : Loc.T(good ? "reason.favMatchups" : "reason.hardMatchups", string.Join(", ", named));
        tags.Add(new PickTag("team", good ? 1 : -1, Loc.T("pickTag.team", Num(f.Team)), tip, f.Team));
    }

    /// Тег синергии и иконки связок. На теге — самая сильная названная связка,
    /// остальные связки и структурные доводы (сетап под ганки…) — иконками. Связки
    /// показываются и без тега: владелец однажды не нашёл в карточке подброс с
    /// ультом Ясуо, хотя связка была.
    private static void SynergyBadges(PickFacts f, List<PickTag> tags, List<PickIcon> links, List<PickIcon> minus)
    {
        var pairs = new List<(string Kind, string Text, double Delta)>();
        var seen = new HashSet<string>();
        foreach (var a in f.ByAlly.OrderByDescending(x => x.Delta))
            if (TeamSynergies.ExplainPair(f.ChampId, a.Id, a.Role) is { } ex && seen.Add(ex.Kind))
                pairs.Add((ex.Kind, ex.Text, a.Delta));
        string PairLine((string Kind, string Text, double Delta) p) =>
            p.Delta >= TAG_SYN_GOOD ? $"{p.Text} ({Num(p.Delta)}%)" : p.Text;

        var worst = f.ByAlly.Where(a => a.Delta <= -ALLY_BAD).OrderBy(a => a.Delta).ToList();
        var inTag = "";
        if (f.Synergy >= TAG_SYN_GOOD)
        {
            var top = pairs.FirstOrDefault();
            inTag = top.Kind ?? "";
            var label = top.Kind is { } k ? Loc.T($"pairShort.{k}") : Loc.T("pickTag.synergyWord");
            var tip = pairs.Count > 0 ? string.Join("\n", pairs.Select(PairLine)) : Loc.T("reason.fitsTeam");
            tags.Add(new PickTag("synergy", 1, $"{label} {Num(f.Synergy)}%", tip, f.Synergy));
        }
        else if (f.Synergy <= -TAG_SYN_BAD)
        {
            var named = f.ByAlly.Where(a => a.Delta < 0).OrderBy(a => a.Delta).Take(2)
                .Select(a => $"{DataDragon.Name(a.Id)} ({Num(a.Delta)}%)");
            tags.Add(new PickTag("synergy", -1, Loc.T("pickTag.synergy", Num(f.Synergy)),
                Loc.T("reason.synBad", string.Join(", ", named)), f.Synergy));
            worst = [];   // худшие союзники уже в подсказке тега
        }

        foreach (var p in pairs.Where(p => p.Kind != inTag))
            links.Add(new PickIcon("pair." + p.Kind, 1, Loc.T($"pairShort.{p.Kind}"), PairLine(p)));
        foreach (var (kind, text) in f.Structural)
            links.Add(new PickIcon(kind, 1, Loc.T($"pickIcon.{kind}"), Plain(text)));
        if (worst.Count > 0)
            minus.Add(new PickIcon("pairBad", -1, Loc.T("pickIcon.pairBad"),
                Loc.T("reason.pairBad", DataDragon.Name(worst[0].Id), Num(worst[0].Delta))));
    }

    // ── Иконки ─────────────────────────────────────────────────────────

    private void PatchIcons(PickFacts f, List<PickIcon> plus, List<PickIcon> minus)
    {
        var wr = $"{50 + f.BaseDelta:F1}";
        if (f.Grade == "OP") plus.Add(Icon("patchOp", 1, Loc.T("reason.baseTop", wr)));
        else if (f.Grade == "S") plus.Add(Icon("patchStrong", 1, Loc.T("reason.baseGood", wr)));
        else if (f.Grade == "C") minus.Add(Icon("patchWeak", -1, Loc.T("reason.baseLow", wr)));

        // Сводка по патчам — та же, что в карточке чемпиона при наведении на
        // слот: меньше порога тир-листа игр в патче — «мало игр».
        if (PatchWr(f.ChampId, f.Role) is not { } p)
        {
            minus.Add(Icon("fewGames", -1, Loc.T("reason.fewGames")));
            return;
        }
        if (p.Delta is not { } shift) return;
        var was = $"{p.Wr - shift:F1}";
        var now = $"{p.Wr:F1}";
        if (shift >= ICON_PATCH_SHIFT) plus.Add(Icon("patchUp", 1, Loc.T("reason.patchUp", was, now)));
        else if (shift <= -ICON_PATCH_SHIFT) minus.Add(Icon("patchDown", -1, Loc.T("reason.patchDown", was, now)));
    }

    private void PlayerIcons(PickFacts f, List<PickIcon> plus, List<PickIcon> minus)
    {
        if (f.Comfort >= 3.8) plus.Add(Icon("comfort", 1, Loc.T("reason.comfortHigh")));
        else if (f.Comfort >= 1.9) plus.Add(Icon("familiar", 1, Loc.T("reason.comfortMid")));

        if (Math.Abs(f.Personal) < ICON_PERSONAL || _forMate) return;
        var (g, w) = MyHistory().Recent(f.ChampId);
        if (g <= 0) return;
        var pct = $"{100.0 * w / g:F0}";
        if (f.Personal > 0) plus.Add(Icon("personalUp", 1, Loc.T("reason.personalUp", pct, g)));
        else minus.Add(Icon("personalDown", -1, Loc.T("reason.personalDown", pct, g)));
    }

    /// <summary>
    /// Стиль врага, о котором иконка может говорить: только ЯВНЫЙ
    /// (<see cref="ChampionTraits.DominantStyle"/> — тот же порог, что у подсветки
    /// стиля своей команды), а «фронт-ту-бэк» — только если у врагов есть танк.
    /// В счёте стиль берётся мягче (просто перевес), но подсказка говорит
    /// словами «их команда стоит за крепким фронтом», и без порога она врала:
    /// владелец (07.11) увидел «не пробивает их фронт» против Вейн, Эвелинн,
    /// Мэл и Лулу — «они даже фронтлайн не пикнули»; фронт-ту-бэк там дали
    /// гиперкерри и щиты Лулу.
    /// </summary>
    private static ChampionTraits.Arch? ShownEnemyStyle(IReadOnlyList<int> enemies)
    {
        var style = ChampionTraits.DominantStyle(enemies);
        return style == ChampionTraits.Arch.FrontToBack && !enemies.Any(ChampionTraits.IsTanky) ? null : style;
    }

    /// <summary>
    /// Против их стиля: есть чем ответить — плюс (как строка «против дайва»).
    /// Минус — только когда ответить правда нечем, по тем же словам, что в
    /// подсказке: против дайва — ни пила, ни отхода, ни рывков; против пока —
    /// ни рывков, ни захода, и сам не бьёт издалека; против фронта — ближний
    /// бой без поука и бёрста. Дальняя атака — тоже дальность: у Джинкс её
    /// нет в метках, а ракеты есть (владелец, 07.11).
    /// </summary>
    private static void StyleIcons(PickFacts f, List<PickIcon> plus, List<PickIcon> minus)
    {
        if (f.EnemyStyle is not { } dom) return;
        var id = f.ChampId;
        var (want, tools) = StyleParts(id, dom);
        var (good, goodText, bad, helpless) = dom switch
        {
            ChampionTraits.Arch.Dive => ("antiDive", "reason.antiDive", "noDive",
                ChampionTraits.Peel(id) == 0 && ChampionTraits.Disengage(id) == 0 && ChampionTraits.Gapclose(id) == 0),
            ChampionTraits.Arch.PickPoke => ("antiPoke", "reason.antiPickPoke", "noPoke",
                ChampionTraits.Gapclose(id) == 0 && ChampionTraits.Engage(id) == 0 && !ChampionTraits.LongRange(id)),
            _ => ("antiFront", "reason.antiFront", "noFront",
                !DataDragon.IsRanged(id) && !ChampionTraits.LongRange(id) && ChampionTraits.Burst(id) == 0),
        };
        if (tools >= ICON_STYLE_TOOLS) plus.Add(Icon(good, 1, Loc.T(goodText)));
        else if (want <= 0 && helpless) minus.Add(Icon(bad, -1, Loc.T($"reason.{bad}")));
    }

    /// Состав своей команды: тип урона и общая уязвимость (одним предметом
    /// гасят многих), плюс — наказывает вынужденный предмет врага. Подсказки —
    /// своя на каждую категорию: какие предметы купит враг, названия как в клиенте.
    private static void CompIcons(PickFacts f, List<PickIcon> plus, List<PickIcon> minus)
    {
        // Бонус дают только две категории (ItemValue.ExploitBonus).
        if (f.Exploit.Bonus > 0 && f.Exploit.Forced switch
            {
                ItemValue.Cat.Cc       => "reason.exploitCc",
                ItemValue.Cat.AutoCrit => "reason.exploitAuto",
                _                      => null,
            } is { } exploitTip)
            plus.Add(Icon("exploit", 1, Loc.T(exploitTip)));

        var kind = Loc.T(f.Dmg.Ad ? "cat.physical" : "cat.magic");
        if (f.Dmg.Delta > 0) plus.Add(Icon("dmgBalance", 1, Loc.T("reason.dmgBalance", kind)));
        else if (f.Dmg.Stack) minus.Add(Icon("dmgStack", -1, Loc.T("reason.dmgStack", kind)));

        if (f.Vuln.Penalty > 0 && f.Vuln.Cat is { } cat && f.Vuln.Count >= 2)
            minus.Add(Icon("vuln", -1, Loc.T(cat switch
            {
                ItemValue.Cat.Cc       => "reason.vulnCc",
                ItemValue.Cat.AutoCrit => "reason.vulnAuto",
                ItemValue.Cat.Shield   => "reason.vulnShield",
                _                      => "reason.vulnHeal",
            }, f.Vuln.Count)));
    }

    /// ARAM: врагов нет — синергия тегом без названия связки, дыры состава и
    /// сила иконками.
    private static PickBadges AramBadges(double baseDelta, double synDelta, double comfort, bool front, bool heal)
    {
        var tags = new List<PickTag>();
        if (synDelta > 0.5)
            tags.Add(new PickTag("synergy", 1, Loc.T("pickTag.synergy", Num(synDelta)), Loc.T("reason.fitsTeam"), synDelta));
        var icons = new List<PickIcon>();
        var wr = $"{50 + baseDelta:F1}";
        if (baseDelta >= 2.5) icons.Add(Icon("patchOp", 1, Loc.T("reason.baseTop", wr)));
        else if (baseDelta >= 1.0) icons.Add(Icon("patchStrong", 1, Loc.T("reason.baseGood", wr)));
        if (comfort >= 3.8) icons.Add(Icon("comfort", 1, Loc.T("reason.comfortHigh")));
        else if (comfort >= 1.9) icons.Add(Icon("familiar", 1, Loc.T("reason.comfortMid")));
        if (front) icons.Add(Icon("aramFront", 1, Loc.T("reason.aramFront")));
        if (heal) icons.Add(Icon("aramHeal", 1, Loc.T("reason.aramHeal")));
        return new PickBadges(tags, icons);
    }

    private static PickIcon Icon(string kind, int sign, string tip) =>
        new(kind, sign, Loc.T($"pickIcon.{kind}"), tip);

    /// Строка довода без служебного знака в начале.
    private static string Plain(string s) =>
        s.Length > 0 && (s[0] == SIGN_GOOD || s[0] == SIGN_BAD || s[0] == SIGN_KEY) ? s[1..] : s;

    /// Число со знаком для тега и подсказки: «+1,3», «−0,7» (типографский минус).
    private static string Num(double v) => (v >= 0 ? "+" : "−") + Math.Abs(v).ToString("F1");
}
