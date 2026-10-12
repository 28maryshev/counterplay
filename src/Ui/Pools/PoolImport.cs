using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using ComboBox = System.Windows.Controls.ComboBox;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Image = System.Windows.Controls.Image;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace Counterplay;

/// <summary>
/// Загрузка пула из файла — общая для плитки в настройках и для файла,
/// открытого двойным кликом в проводнике.
///
/// Разница только в том, известно ли, КУДА класть. С плитки известно: на какую
/// половину окна нажали, туда и кладём. Из проводника половины нет — про личный
/// пул спрашиваем, дуо-файл кладём дуо-пулом без вопросов.
/// </summary>
static class PoolImport
{
    /// <param name="intoDuo">
    /// true — в «Дуо», false — в «Пул», null — спросить (открыли файл снаружи).
    /// </param>
    /// <returns>true, если пул добавлен.</returns>
    public static bool Load(Window owner, string path, bool? intoDuo)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception ex)
        {
            Log.Write($"пул не прочитался: {ex.Message}");
            Confirm.Tell(owner, Loc.T("pool.importFile"), Loc.T("pool.importBad"));
            return false;
        }
        return Apply(owner, text, intoDuo, Path.GetFileNameWithoutExtension(path));
    }

    /// <summary>
    /// Разобрать и положить готовый JSON — пришёл он из файла или из кода.
    /// <paramref name="fallbackName"/> подставляется безымянному пулу (имя файла);
    /// у кода имени файла нет, поэтому null.
    /// </summary>
    public static bool Apply(Window owner, string text, bool? intoDuo, string? fallbackName)
    {
        var (pool, duo, ownerPuuid, ownerName) = PoolFile.ParseWithOwner(text);
        if (pool is null && duo is null)
        {
            Confirm.Tell(owner, Loc.T("pool.importFile"), Loc.T("pool.importBad"));
            return false;
        }

        // Безымянный пул выглядел бы пустой плиткой — подписываем чем есть.
        var name = pool?.Name ?? duo!.FriendName;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = string.IsNullOrWhiteSpace(fallbackName) ? Loc.T("pool.pool") : fallbackName!;
            if (pool is not null) pool.Name = name; else duo!.FriendName = name;
        }

        var a = PoolStore.Current();

        // Куда класть личный пул. Из проводника спрашиваем; отказ — ничего не делаем.
        var toDuo = intoDuo;
        if (pool is not null && toDuo is null)
        {
            var choice = Confirm.Pick(owner, Loc.T("pool.importFile"), Loc.T("pool.importWhere", name),
                                      Loc.T("pool.importAsPool"), Loc.T("pool.importAsDuo"));
            if (choice is null) return false;
            toDuo = choice == 1;
        }

        // Личный пул друга, положенный в «Дуо»: он — половина друга, своя
        // половина берётся из моего пула (какого — спрашиваем).
        if (toDuo == true && pool is not null)
        {
            if (!MyPoolPicker.Ask(owner, a.Pools, name, out var mine)) return false;
            // Имя пары складываем из обоих: сначала мой пул, потом пул друга —
            // по плитке сразу видно, из чего она собрана. Длину режем: имя на
            // плитке переносится, а не обрезается, и длинное расползётся.
            var pairName = mine is null ? name : $"{mine.Name} + {name}";
            if (pairName.Length > 40) pairName = pairName[..40];
            duo = new DuoPool
            {
                FriendName = pairName,
                Friend     = CloneRoles(pool.ByRole),
                Mine       = mine is null ? new() : CloneRoles(mine.ByRole),
                // Хозяин второй половины — тот, кто прислал файл. Дальше связка
                // ищется в команде по нему, а не по совпадению имени: играя
                // впятером, пул срабатывает именно на этого человека.
                FriendPuuid = ownerPuuid,
                // Ник человека — отдельно от подписи плитки: та склеена из
                // названий половин и на месте имени читается бессмыслицей.
                FriendNick  = ownerName,
            };
            name = pairName;   // о нём же и сообщаем
            pool = null;
        }

        if (pool is not null) a.Pools.Add(pool); else a.DuoPools.Add(duo!);
        PoolStore.Persist();
        Confirm.Tell(owner, Loc.T("pool.importFile"), Loc.T("pool.imported", name));
        return true;
    }

    private static Dictionary<string, List<int>> CloneRoles(Dictionary<string, List<int>> src) =>
        src.ToDictionary(kv => kv.Key, kv => new List<int>(kv.Value));
}
