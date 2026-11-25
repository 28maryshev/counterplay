namespace Counterplay;

/// <summary>
/// Кнопка фразы на полосе чата драфта (OverlayWindow.Phrases).
/// Index — номер фразы (0…4), по нему клик берёт текст из настроек.
/// State — короткая вспышка после нажатия: "sent" ушло, "fail" нет, "" — покой.
/// </summary>
public sealed class PhraseChip
{
    public int    Index { get; init; }
    public string Text  { get; init; } = "";
    public string Tip   { get; init; } = "";
    public string State { get; init; } = "";
}
