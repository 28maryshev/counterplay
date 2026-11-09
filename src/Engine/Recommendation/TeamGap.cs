namespace Counterplay;

/// <summary>
/// Дыра состава своей команды: чего ей не хватает (фронт, отход, контроль…).
/// Kind — вид тега (по нему подпись <c>pickTag.{Kind}</c> и значок), Fills —
/// закрывает ли чемпион эту дыру, Tip — подсказка тега, Weight — насколько
/// дыра важна (доля надбавки в счёт, см. <c>RecommendationEngine.Needs.cs</c>;
/// 0 — дыра только показывается, в счёте её уже учитывает другое слагаемое).
/// </summary>
public sealed record TeamGap(string Kind, Func<int, bool> Fills, string Tip, double Weight);
