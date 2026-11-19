using System.Windows;
using System.Windows.Media;

namespace Counterplay;

/// <summary>
/// Два столбца карточки чемпиона рядом: контрпики (винрейт против него) и
/// синергия (винрейт вместе). В карточке их до двух блоков: общий — его
/// соперники по линии и напарники со всех ролей, и «моя роль» — те же списки
/// только с моей линии, под своей шапкой. Один шаблон на оба
/// (ChampTipListsTemplate), чтобы разметка не жила двумя копиями.
/// </summary>
public sealed class ChampTipLists
{
    // Шапка блока: у общего её нет, у «моей роли» — значок и подпись роли.
    public string       Header     { get; init; } = "";
    public ImageSource? HeaderIcon { get; init; }
    public Visibility   HeaderVis  => Header.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public List<ChampTipRow> Counters  { get; init; } = [];
    public List<ChampTipRow> Synergies { get; init; } = [];
    public Visibility CountersEmptyVis => Counters.Count  == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SynergyEmptyVis  => Synergies.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    // Подписи — строками перевода: карточки кэшируются до смены языка.
    public string CountersTitle => Loc.T("champTip.counters");
    public string CountersCol   => Loc.T("champTip.countersCol");
    public string SynergyTitle  => Loc.T("champTip.synergy");
    public string SynergyCol    => Loc.T("champTip.synergyCol");
    public string NoDataText    => Loc.T("champTip.noData");
}
