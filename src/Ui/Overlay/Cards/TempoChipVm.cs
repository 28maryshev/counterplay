using Brush = System.Windows.Media.Brush;

namespace Counterplay;

/// <summary>
/// Плашка «винрейт по длине игры» под колонкой команды: отрезок игры (Label —
/// «до 25 мин»), насколько команда на нём выигрывает чаще или реже обычного
/// (Value — «+0,6%»), пояснение (Tip). Цвет по знаку: плюс — зелёная, минус —
/// красная, около нуля — серая.
/// </summary>
public sealed record TempoChipVm(string Label, string Value, string Tip, Brush Fg, Brush Border);
