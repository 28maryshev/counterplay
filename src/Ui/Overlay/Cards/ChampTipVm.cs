using System.Windows;
using System.Windows.Media;

namespace Counterplay;

/// <summary>
/// Карточка чемпиона при наведении на слот состава: шапка, три плитки (винрейт
/// в патче со сдвигом к прошлому, бан-рейт, место в тир-листе), контрпики и
/// синергия с винрейтами — общие и с моей роли (ChampTipLists). Собирает
/// OverlayWindow.ChampTip.cs.
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

    // Общие списки: соперники по его линии и напарники со всех ролей.
    public ChampTipLists  General { get; init; } = new();
    // Те же списки с моей линии; нет — роль у меня не выдана или та же, что у
    // него (тогда общие контрпики уже с моей линии, а напарником он мне не будет).
    public ChampTipLists? Mine    { get; init; }
    public Visibility     MineVis => Mine is not null ? Visibility.Visible : Visibility.Collapsed;

    public string Footer { get; init; } = "";

    // Подписи — строками перевода: карточки кэшируются до смены языка.
    public string WrLabel     => Loc.T("champTip.wr");
    public string BanLabel    => Loc.T("champTip.ban");
    public string TierLabel   => Loc.T("champTip.tier");
}
