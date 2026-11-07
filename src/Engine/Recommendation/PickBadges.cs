namespace Counterplay;

/// <summary>
/// Тег карточки пика — ключевой вывод про этот драфт с цифрой: линия, вслепую,
/// против команды, синергия. Kind — вид (по нему окно берёт значок), Sign —
/// +1 за пик, −1 против; Text — что написано на плашке, Tip — подсказка.
/// Value — сила вывода, пп (у «вслепую» — насколько шанс контры не как у всех):
/// по ней окно делает самый сильный тег вида самым ярким.
/// </summary>
public sealed record PickTag(string Kind, int Sign, string Text, string Tip, double Value);

/// <summary>
/// Иконка карточки пика — признак без текста: сила в патче, наигранность,
/// связка, перекос урона… Title — короткое имя, Tip — фраза в подсказке.
/// Связки (Kind с «pair.» и структурные) окно ставит отдельной группой.
/// </summary>
public sealed record PickIcon(string Kind, int Sign, string Title, string Tip);

/// Теги и иконки кандидата. Порядок иконок уже задан движком: плюсы, связки, минусы.
public sealed record PickBadges(IReadOnlyList<PickTag> Tags, IReadOnlyList<PickIcon> Icons)
{
    public static readonly PickBadges Empty = new([], []);
}

/// <summary>
/// Всё, из чего складываются теги и иконки кандидата. Движок собирает это при
/// подсчёте очков (числа те же, что в счёте), а сами значки строит только для
/// показанных кандидатов — им нужен винрейт по патчам, это запрос в базу.
/// </summary>
public sealed record PickFacts(
    int ChampId, string Role,
    // Линия: показанное число (на боте — с их дуо), сама линия и бот по отдельности.
    double Lane, double LaneOnly, int LaneOppId, double Duo, int DuoOppId, bool DuoBlind,
    bool LaneBlind, BlindRisk Risk,
    double Team, IReadOnlyList<(int Id, double Delta)> ByEnemy,
    double Synergy, IReadOnlyList<(int Id, string Role, double Delta)> ByAlly,
    string Grade, double BaseDelta, double Comfort, double Personal,
    ChampionTraits.Arch? EnemyStyle,   // явный стиль врага для иконок (ShownEnemyStyle), не тот, что в счёте
    (double Delta, bool Stack, bool Ad) Dmg,
    (double Penalty, ItemValue.Cat? Cat, int Count) Vuln,
    (double Bonus, ItemValue.Cat? Forced) Exploit,
    IReadOnlyList<(string Kind, string Text)> Structural);
