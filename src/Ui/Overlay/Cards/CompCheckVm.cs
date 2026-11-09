using System.Windows.Media;
using Brush = System.Windows.Media.Brush;

namespace Counterplay;

/// <summary>
/// Плашка полосы состава под колонкой команды: вид дыры (Kind — фронт,
/// контроль, чистка волн…), подпись, кто её закрывает (Tip) и состояние:
/// State 1 — есть, −1 — нет и пиков не осталось, 0 — пока нет, но команда ещё
/// выбирает. Цвет текста и рамки — по состоянию.
/// </summary>
public sealed record CompCheckVm(string Kind, int State, Geometry? Glyph, string Label, string Tip, Brush Fg, Brush Border);
