using System.Windows;
using System.Windows.Media;

namespace Counterplay;

/// <summary>
/// Карточка чемпиона при наведении на слот состава: шапка, три плитки (винрейт
/// в патче со сдвигом к прошлому, бан-рейт, место в тир-листе), контрпики и
/// синергия с винрейтами. Собирает OverlayWindow.ChampTip.cs.
/// </summary>
public sealed class ChampTipVm
{
    public ImageSource? Icon      { get; init; }
    public string       Name      { get; init; } = "";
    public string       NameColor { get; init; } = "White";
    public string       RingColor { get; init; } = "#C89B3C";
    public ImageSource? RoleIcon  { get; init; }
    public string       RoleText  { get; init; } = "";

    public string     WrText     { get; init; } = "—";
    public string     WrBrush    { get; init; } = "#4E6275";
    public string     DeltaText  { get; init; } = "";
    public string     DeltaBrush { get; init; } = "#6E8598";
    // Под числом — почему его нет («мало игр в патче»).
    public string     WrNote     { get; init; } = "";
    public Visibility WrNoteVis  => WrNote.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public string BanText  { get; init; } = "";
    public string BanBrush { get; init; } = "#8AA0B2";

    public string     Grade      { get; init; } = "";
    public string     GradeColor { get; init; } = "#6E5140";
    public Visibility GradeVis   => Grade.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string     PlaceText  { get; init; } = "";

    public List<ChampTipRow> Counters  { get; init; } = [];
    public List<ChampTipRow> Synergies { get; init; } = [];
    public Visibility CountersEmptyVis => Counters.Count  == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SynergyEmptyVis  => Synergies.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public string Footer { get; init; } = "";

    // Подписи — строками перевода: карточки кэшируются до смены языка.
    public string WrLabel     => Loc.T("champTip.wr");
    public string BanLabel    => Loc.T("champTip.ban");
    public string TierLabel   => Loc.T("champTip.tier");
    public string CountersTitle => Loc.T("champTip.counters");
    public string CountersCol   => Loc.T("champTip.countersCol");
    public string SynergyTitle  => Loc.T("champTip.synergy");
    public string SynergyCol    => Loc.T("champTip.synergyCol");
    public string NoDataText    => Loc.T("champTip.noData");
}
