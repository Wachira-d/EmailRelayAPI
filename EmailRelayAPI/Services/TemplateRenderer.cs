using System.Net;
using System.Text.RegularExpressions;

namespace EmailRelayAPI.Services;

public interface ITemplateRenderer
{
    /// <summary>Replaces {{name}} (encoded when <paramref name="htmlEncode"/>) and {{{name}}} (raw) placeholders.</summary>
    string Render(string template, IReadOnlyDictionary<string, string>? variables, bool htmlEncode);
}

public partial class TemplateRenderer : ITemplateRenderer
{
    [GeneratedRegex(@"\{\{\{\s*([\w.\-]+)\s*\}\}\}")]
    private static partial Regex RawPlaceholder();

    [GeneratedRegex(@"\{\{\s*([\w.\-]+)\s*\}\}")]
    private static partial Regex Placeholder();

    public string Render(string template, IReadOnlyDictionary<string, string>? variables, bool htmlEncode)
    {
        if (string.IsNullOrEmpty(template)) return template;
        var vars = variables ?? new Dictionary<string, string>();

        string Lookup(string name)
            => vars.TryGetValue(name, out var v) ? v
             : vars.FirstOrDefault(kv => kv.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value ?? string.Empty;

        var result = RawPlaceholder().Replace(template, m => Lookup(m.Groups[1].Value));
        result = Placeholder().Replace(result, m =>
        {
            var value = Lookup(m.Groups[1].Value);
            return htmlEncode ? WebUtility.HtmlEncode(value) : value;
        });
        return result;
    }
}
