using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace FlowEncode.Domain;

// Mirrors upstream deew 5.5 src/output_name.rs, src/util.rs (file_stem) and src/plan.rs (name_label).
public static class DeewOutputNaming
{
    public const string DefaultTemplate = "{stem}";
    public static readonly string[] Placeholders = ["stem", "name", "format", "codec", "channels", "bitrate", "lang", "title", "track"];
    public static readonly string[] OutputExtensions = ["ac3", "ec3", "eb3", "thd", "ac4", "aac"];
    private static readonly char[] Separators = ['.', ' ', '_', '-'];
    private static readonly Regex DelayTagRegex = new(@" ?DELAY [-|+]?[0-9]+m?s", RegexOptions.Compiled);

    public sealed record Values(
        string Stem = "",
        string Name = "",
        string Format = "",
        string Codec = "",
        string Channels = "",
        string Bitrate = "",
        string Lang = "",
        string Title = "",
        string Track = "")
    {
        public string Get(string placeholder) => placeholder switch
        {
            "stem" => Stem,
            "name" => Name,
            "format" => Format,
            "codec" => Codec,
            "channels" => Channels,
            "bitrate" => Bitrate,
            "lang" => Lang,
            "title" => Title,
            "track" => Track,
            _ => throw new InvalidOperationException($"Unknown placeholder {{{placeholder}}}.")
        };
    }

    private sealed record Part(bool IsField, string Text);

    public static void ValidateLiteralName(string name)
    {
        if (string.IsNullOrEmpty(name))
            throw new InvalidOperationException("Invalid deew output name: the name is empty.");
        if (name.IndexOfAny(['/', '\\']) >= 0)
            throw new InvalidOperationException("Invalid deew output name: the name can't contain a path separator, use -o for the folder.");
    }

    public static void ValidateTemplate(string template) => _ = Parse(template);

    public static string FileStem(string path, bool stripDelay)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        return stripDelay ? DelayTagRegex.Replace(stem, string.Empty) : stem;
    }

    public static string? ResolveFileName(string sourcePath, AudioSourceInfo? source, DeewEffectiveOutputProfile profile, string? configTemplate = null)
    {
        if (profile.Options.MeasureOnly || string.IsNullOrWhiteSpace(profile.Extension))
            return null;

        var ext = profile.Extension.TrimStart('.').ToLowerInvariant();
        var values = BuildValues(sourcePath, source, profile);
        if (!string.IsNullOrEmpty(profile.Options.OutputName))
        {
            ValidateLiteralName(profile.Options.OutputName);
            return Render([new Part(false, profile.Options.OutputName)], values, ext);
        }

        var templateText = !string.IsNullOrEmpty(profile.Options.OutputTemplate)
            ? profile.Options.OutputTemplate
            : string.IsNullOrEmpty(configTemplate) ? DefaultTemplate : configTemplate;
        var parts = Parse(templateText);
        return Render(parts, values, ext);
    }

    public static string RenderTemplate(string template, Values values, string extension)
        => Render(Parse(template), values, extension.TrimStart('.').ToLowerInvariant());

    public static string RenderLiteral(string name, Values values, string extension)
    {
        ValidateLiteralName(name);
        return Render([new Part(false, name)], values, extension.TrimStart('.').ToLowerInvariant());
    }

    public static Values BuildValues(string sourcePath, AudioSourceInfo? source, DeewEffectiveOutputProfile profile)
    {
        var format = profile.Options.Format;
        var isThd = format is "thd" or "thd-atmos";
        return new Values(
            Stem: FileStem(sourcePath, stripDelay: true),
            Name: FileStem(sourcePath, stripDelay: false),
            Format: format,
            Codec: FormatSummaryName(format),
            Channels: ChannelLabel(profile),
            Bitrate: isThd ? "" : profile.EffectiveBitrateKbps?.ToString(CultureInfo.InvariantCulture) ?? "",
            Lang: source?.Language ?? "",
            Title: source?.Title ?? "",
            Track: (profile.Options.TrackIndex ?? 0).ToString(CultureInfo.InvariantCulture));
    }

    public static string FormatSummaryName(string format) => format switch
    {
        "dd" => "DD",
        "ddp" => "DDP",
        "ddp-atmos" => "DDP Atmos",
        "thd" => "TrueHD",
        "thd-atmos" => "TrueHD Atmos",
        "ac4" => "AC4",
        "ac4-atmos" => "AC4 Atmos",
        "ac4-ims" => "AC4 IMS",
        "aac" => "AAC",
        _ => format
    };

    public static string ChannelLabel(DeewEffectiveOutputProfile profile) => profile.Options.Format switch
    {
        "ac4-ims" => "2.0",
        "ddp-atmos" => profile.Mode == "streaming" ? "5.1" : "7.1",
        "thd-atmos" => "7.1",
        "ac4-atmos" => "",
        _ => (profile.OutputSlots, profile.HasLfe) switch
        {
            (6, false) => "5.0",
            (8, false) => "7.0",
            (1, _) => "1.0",
            (2, _) => "2.0",
            (6, true) => "5.1",
            (8, true) => "7.1",
            _ => ""
        }
    };

    public static string Sanitize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            sb.Append(c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*' || char.IsControl(c) ? '_' : c);
        }
        return sb.ToString();
    }

    private static List<Part> Parse(string text)
    {
        if (text.IndexOfAny(['/', '\\']) >= 0)
            throw new InvalidOperationException("Invalid deew output template: the template can't contain a path separator, use -o for the folder.");

        var validList = string.Join(", ", Placeholders.Select(p => $"{{{p}}}"));
        var parts = new List<Part>();
        var literal = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '{')
            {
                var field = new StringBuilder();
                var closed = false;
                while (++i < text.Length)
                {
                    var next = text[i];
                    if (next == '}') { closed = true; break; }
                    if (next == '{')
                        throw new InvalidOperationException($"Invalid deew output template: unbalanced brace in the template, the valid placeholders are {validList}.");
                    field.Append(next);
                }
                if (!closed)
                    throw new InvalidOperationException($"Invalid deew output template: unbalanced brace in the template, the valid placeholders are {validList}.");
                var name = field.ToString();
                if (!Placeholders.Contains(name))
                    throw new InvalidOperationException($"Invalid deew output template: unknown placeholder {{{name}}}, the valid placeholders are {validList}.");
                if (literal.Length > 0)
                {
                    parts.Add(new Part(false, literal.ToString()));
                    literal.Clear();
                }
                parts.Add(new Part(true, name));
            }
            else if (c == '}')
            {
                throw new InvalidOperationException($"Invalid deew output template: unbalanced brace in the template, the valid placeholders are {validList}.");
            }
            else
            {
                literal.Append(c);
            }
        }
        if (literal.Length > 0)
            parts.Add(new Part(false, literal.ToString()));
        return parts;
    }

    private static string? TailExtension(List<Part> parts)
    {
        if (parts.Count == 0 || parts[^1].IsField)
            return null;
        var text = parts[^1].Text;
        var dot = text.LastIndexOf('.');
        if (dot < 0 || dot == text.Length - 1)
            return null;
        var ext = text[(dot + 1)..].ToLowerInvariant();
        return OutputExtensions.Contains(ext) ? ext : null;
    }

    private static string Render(List<Part> parts, Values values, string ext)
    {
        if (parts.Count == 1 && parts[0].IsField && parts[0].Text == "stem" && !string.IsNullOrEmpty(values.Stem))
            return $"{values.Stem}.{ext}";

        var name = RenderBase(parts, values);
        if (string.IsNullOrEmpty(name))
            throw new InvalidOperationException("Invalid deew output name: the output name is empty.");

        var tail = TailExtension(parts);
        if (tail is not null)
        {
            if (tail == ext) return name;
            throw new InvalidOperationException($"Invalid deew output name: the name ends with .{tail}, but the extension of this output is .{ext}, leave it out or end the name with .{ext}.");
        }
        return $"{name}.{ext}";
    }

    private static string RenderBase(List<Part> parts, Values values)
    {
        var items = new List<(int Kind, string Text)>(parts.Count); // 0=Text, 1=Value, 2=Empty
        var anyEmpty = false;
        foreach (var part in parts)
        {
            if (!part.IsField)
                items.Add((0, Sanitize(part.Text)));
            else
            {
                var value = Sanitize(values.Get(part.Text));
                if (string.IsNullOrEmpty(value)) { items.Add((2, "")); anyEmpty = true; }
                else items.Add((1, value));
            }
        }

        var done = new List<(int Kind, string Text)>();
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item.Kind == 2)
            {
                var left = "";
                if (done.Count > 0 && done[^1].Kind == 0)
                {
                    var text = done[^1].Text;
                    var kept = text.TrimEnd(Separators).Length;
                    left = text[kept..];
                    done[^1] = (0, text[..kept]);
                }
                var right = "";
                if (i + 1 < items.Count && items[i + 1].Kind == 0)
                {
                    var text = items[i + 1].Text;
                    var cut = text.Length - text.TrimStart(Separators).Length;
                    right = text[..cut];
                    items[i + 1] = (0, text[cut..]);
                }
                var merged = string.IsNullOrEmpty(left) ? right : left;
                done.Add((0, merged));
            }
            else if (item.Kind == 0 && item.Text.Length == 0)
            {
            }
            else
            {
                done.Add(item);
            }
        }

        if (anyEmpty)
        {
            for (var i = 0; i < done.Count; i++)
            {
                if (done[i].Kind != 0) break;
                var trimmed = done[i].Text.TrimStart(Separators);
                done[i] = (0, trimmed);
                if (trimmed.Length > 0) break;
            }
            for (var i = done.Count - 1; i >= 0; i--)
            {
                if (done[i].Kind != 0) break;
                var trimmed = done[i].Text.TrimEnd(Separators);
                done[i] = (0, trimmed);
                if (trimmed.Length > 0) break;
            }
        }

        var sb = new StringBuilder();
        foreach (var item in done)
        {
            if (item.Kind is 0 or 1) sb.Append(item.Text);
        }
        return sb.ToString().TrimEnd('.', ' ');
    }
}
