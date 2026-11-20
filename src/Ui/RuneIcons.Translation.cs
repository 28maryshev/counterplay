using System.IO;
using System.Text.Json;

namespace Counterplay;

// Справочник рун — свой перевод для языков, которых у Riot нет. Владелец
// (20.11): «делай переводы в рунах на остальные языки». У украинского в Data
// Dragon локали нет — справочник приходит английским (Loc: uk → en_US), и
// игрок видел руны по-английски. Перевод вшит в программу:
// assets/runes/<язык>.json — названия деревьев, названия рун и короткие
// описания.
//
// Описание Riot правит вместе с балансом («30-60 урона» → «40-70»), а перевод
// сам не обновится. Поэтому у каждого переведённого описания лежит английский
// текст, с которого переводили (src): справочник поменялся — показываем свежий
// английский, а не перевод со старыми цифрами. Название не меняется — оно
// переведено всегда.
public static partial class RuneIcons
{
    /// Сколько описаний показано английским: Riot поменял текст после перевода.
    /// Для журнала и проверки.
    public static int StaleTranslations { get; private set; }

    /// Наложить перевод языка lang, если он вшит; нет — ничего.
    private static void ApplyTranslation(string lang)
    {
        StaleTranslations = 0;
        var asm = typeof(RuneIcons).Assembly;
        var name = asm.GetManifestResourceNames()
                      .FirstOrDefault(n => n.EndsWith($".runes.{lang}.json", StringComparison.OrdinalIgnoreCase));
        if (name is null) return;
        using var s = asm.GetManifestResourceStream(name)!;
        using var doc = JsonDocument.Parse(s);
        ApplyTranslation(doc.RootElement);
        if (StaleTranslations > 0)
            Log.Write($"руны: перевод {lang} — описаний устарело {StaleTranslations}, показаны по-английски");
    }

    /// {"trees": {"8000": "…"}, "runes": {"8005": {"name", "desc", "src"}}}.
    private static void ApplyTranslation(JsonElement root)
    {
        if (root.TryGetProperty("trees", out var trees))
            foreach (var t in trees.EnumerateObject())
                if (int.TryParse(t.Name, out var id) && StyleNames.ContainsKey(id) && t.Value.GetString() is { Length: > 0 } tn)
                    StyleNames[id] = tn;

        if (!root.TryGetProperty("runes", out var runes)) return;
        foreach (var r in runes.EnumerateObject())
        {
            if (!int.TryParse(r.Name, out var id) || !Runes.TryGetValue(id, out var info)) continue;
            var tr = r.Value;
            var rn = tr.TryGetProperty("name", out var n) ? n.GetString() : null;
            var desc = info.Desc;
            if (tr.TryGetProperty("desc", out var d) && d.GetString() is { Length: > 0 } td)
            {
                // Переводили с этого английского — показываем перевод; Riot
                // текст поменял — свежий английский.
                if (tr.TryGetProperty("src", out var src) && src.GetString() == info.Desc) desc = td;
                else StaleTranslations++;
            }
            Runes[id] = info with { Name = rn is { Length: > 0 } ? rn : info.Name, Desc = desc };
        }
    }
}
