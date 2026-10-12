using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Color = System.Windows.Media.Color;
using ComboBox = System.Windows.Controls.ComboBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using RadioButton = System.Windows.Controls.RadioButton;

namespace Counterplay;

// Панель песочницы — слоты и роли: выбор чемпионов и ролей, перемешивание, друг и его роль.
sealed partial class TestPanel
{
    // Пользователь сам поставил чемпиона → снимаем метку «авто» (авто-драфт его
    // больше не сотрёт). Программные установки авто-драфта идут с _autoSetting.
    private void OnChampChanged(int cell)
    {
        if (!_autoSetting) _autoPicked.Remove(cell);
        Recompute();
    }

    // Выпадающий список роли строки. Роли уникальны: выбрал занятую другой строкой
    // роль — строки меняются местами (перестановка ролей, как реальная смена линий).
    private ComboBox MakeRoleCombo(string[] roles, ComboBox[] combos, int i, string color)
    {
        var cb = new ComboBox
        {
            ItemsSource = RoleNames, Width = 54, Margin = new Thickness(0, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center, FontSize = 11, FontWeight = FontWeights.Bold,
            SelectedIndex = Array.IndexOf(LcuRoles, roles[i]),
            Foreground = (Brush)new BrushConverter().ConvertFromString(color)!,
            ToolTip = "Роль строки — выбери вручную (роли уникальны: при повторе строки меняются местами)"
        };
        cb.SelectionChanged += (_, _) => OnRoleChanged(roles, combos, i);
        return cb;
    }

    private void OnRoleChanged(string[] roles, ComboBox[] combos, int i)
    {
        if (_settingRoles) return;
        int sel = combos[i].SelectedIndex;
        if (sel < 0) return;
        var newRole = LcuRoles[sel];
        if (roles[i] == newRole) return;
        int j = Array.IndexOf(roles, newRole);   // строка, у которой сейчас эта роль
        // Сменили роль своей строки — это тоже выбор роли «я».
        if (ReferenceEquals(roles, _rowRoles) && (i == MeCell() || j == MeCell())) _meChosen = true;
        if (j >= 0) (roles[i], roles[j]) = (roles[j], roles[i]);   // меняем местами
        else roles[i] = newRole;
        SyncRoleCombos(roles, combos);
        Recompute();
    }

    private void SyncRoleCombos(string[] roles, ComboBox[] combos)
    {
        _settingRoles = true;
        for (int k = 0; k < 5; k++) combos[k].SelectedIndex = Array.IndexOf(LcuRoles, roles[k]);
        _settingRoles = false;
    }

    // Перемешивает роли строк ОБЕИХ команд (Фишер-Йетс) — независимо, и следит,
    // чтобы порядок врагов не совпал с моим: очередь пика у ролей всегда разная.
    private void ShuffleRoles()
    {
        static void Shuffle(string[] a, Random rng)
        {
            for (int i = a.Length - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (a[i], a[j]) = (a[j], a[i]);
            }
        }

        Shuffle(_rowRoles, _rng);
        do Shuffle(_enemyRoles, _rng);
        while (_enemyRoles.SequenceEqual(_rowRoles));

        SyncRoleCombos(_rowRoles, _roleCombos);
        SyncRoleCombos(_enemyRoles, _enemyRoleCombos);
        Recompute();
    }

    // ── Друг ─────────────────────────────────────────────────────────────────

    /// Список друзей — дуо-пулы песочницы. Пулы сменились (другой профиль) —
    /// список заполняется заново, и друг сбрасывается: прежний пул исчез.
    private void FillMates()
    {
        _fillingMates = true;
        _mateOptions.Clear();
        var items = new List<string> { "— играю один" };
        _mateOptions.Add(null);
        foreach (var d in PoolStore.Current().DuoPools)
        {
            var nick = DuoNaming.PartnerNick(d, SessionTracker.TopPairs(d.FriendPuuid, 1));
            var tile = d.FriendName.Length > 0 ? d.FriendName : "дуо-пул";
            items.Add(nick.Length > 0 && nick != tile ? $"{nick} · {tile}" : tile);
            _mateOptions.Add(d);
        }
        _mateCombo.ItemsSource = items;
        _mateCombo.SelectedIndex = 0;
        _fillingMates = false;
        SelectMate(null);
    }

    private void SelectMate(DuoPool? duo)
    {
        _mate = duo;
        if (duo is null)
        {
            Party.SandboxMate(null);
            _mateRole = "";
            _fillingMates = true;
            _mateRoleCombo.SelectedIndex = -1;
            _fillingMates = false;
            _mateRoleCombo.IsEnabled = false;
            _mateNote = PoolStore.Current().DuoPools.Count == 0
                ? "дуо-пулов нет — настрой в окне пулов"
                : "";
            _mateInfo.Text = _mateNote;
            Recompute();
            return;
        }

        // Пул, настроенный на этого друга, — в дело, как кнопкой «Дуо».
        PoolStore.SetActive(PoolKind.Duo, duo.Id);
        _overlay.RefreshPoolMode();

        var nick = DuoNaming.PartnerNick(duo, SessionTracker.TopPairs(duo.FriendPuuid, 1));
        Party.SandboxMate(MatePuuid(duo), nick);

        _mateRole = DefaultMateRole(duo);
        _fillingMates = true;
        _mateRoleCombo.SelectedIndex = Array.IndexOf(LcuRoles, _mateRole);
        _fillingMates = false;
        _mateRoleCombo.IsEnabled = true;

        var together = duo.FriendPuuid.Length > 0
            ? SessionTracker.MateStats(duo.FriendPuuid).Games : 0;
        _mateNote = duo.FriendPuuid.Length > 0
            ? $"пул включён · вместе {together} игр"
            : "пул включён · напарник в пуле не записан, считаю его условным";
        _mateInfo.Text = _mateNote;
        Recompute();
    }

    /// puuid друга для его слота. У пула, собранного руками и ни разу не
    /// игранного вместе, хозяина нет — даём условный, и программа опознает
    /// его как человека из пати (запомнит в копии пула песочницы).
    private static string MatePuuid(DuoPool d) =>
        d.FriendPuuid.Length > 0 ? d.FriendPuuid : "TEST-friend-" + d.Id;

    /// Его роль по пулу: где в его половине больше всего чемпионов (у
    /// фиксированных связок — роль из первой пары). Мою роль пропускаем —
    /// на одной линии вдвоём не стоят.
    private string DefaultMateRole(DuoPool d)
    {
        var mine = _rowRoles[MeCell()];
        var byPool = d.Manual
            ? d.ManualPairs.Select(p => p.FriendRole).Where(r => r.Length > 0)
            : d.Friend.Where(kv => kv.Value.Any(x => x != 0))
                      .OrderByDescending(kv => kv.Value.Count(x => x != 0))
                      .Select(kv => kv.Key);
        foreach (var db in byPool)
        {
            var lcu = RecommendationEngine.DbToLcuRole(db);
            if (LcuRoles.Contains(lcu) && lcu != mine) return lcu;
        }
        return LcuRoles.First(r => r != mine);
    }

    /// Строка друга: союзник на его роли. -1 — друга нет или его роль совпала
    /// с моей.
    private int MateCell()
    {
        if (_mate is null || _mateRole.Length == 0) return -1;
        var i = Array.IndexOf(_rowRoles, _mateRole);
        return i == MeCell() ? -1 : i;
    }

    /// Чемпионы его половины на эту роль — из них он и пикает в авто-драфте.
    private static List<int> MatePicks(DuoPool d, string dbRole) =>
        d.Manual
            ? [.. d.ManualPairs.Where(p => p.FriendRole.Length == 0 || p.FriendRole == dbRole)
                               .Select(p => p.Friend).Where(x => x != 0).Distinct()]
            : [.. d.FriendForRole(dbRole).Where(x => x != 0)];
}
