using System.Text;
using System.Text.Json;
using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Syntax;

namespace FlowEncode.Infrastructure;

internal sealed class DeewConfigEditor
{
    private readonly string _text;
    private readonly DocumentSyntax _syntax;
    public TomlTable Model { get; }
    public string DdpMode => Mode("ddp", "standard");
    public string AtmosMode => Mode("ddp_atmos", "streaming");
    public string TempPath => (string)Model["temp_path"];
    public IReadOnlyDictionary<string, int> Bitrates => ((TomlTable)Model["default_bitrates"])
        .ToDictionary(p => p.Key, p => checked((int)(long)p.Value));

    public DeewConfigEditor(byte[] bytes)
    {
        _text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
        _syntax = Toml.Parse(_text);
        if (_syntax.HasErrors) throw new InvalidDataException("Invalid deew config.toml: " + _syntax.Diagnostics);
        Model = _syntax.ToModel();
        Require<string>(Model, "temp_path");
        Require<long>(Model, "logo");
        if (!Model.TryGetValue("max_instances", out var instances) || instances is not (string or long))
            throw new InvalidDataException("deew config: max_instances must be an integer or string.");
        var bitrates = Require<TomlTable>(Model, "default_bitrates");
        foreach (var key in new[] { "dd_1_0", "dd_2_0", "dd_5_1", "ddp_1_0", "ddp_2_0", "ddp_5_1", "ac4_2_0", "ac4_5_1", "ac4_atmos", "aac_2_0" })
            Require<long>(bitrates, key);
        foreach (var key in new[] { "ddp_7_1_standard", "ddp_7_1_bluray", "ddp_atmos_streaming", "ddp_atmos_standard", "ddp_atmos_bluray" })
            if (bitrates.ContainsKey(key)) Require<long>(bitrates, key);
        var summary = Require<TomlTable>(Model, "summary_sections");
        foreach (var key in new[] { "deew_info", "binaries", "input_info", "output_info", "other" }) Require<bool>(summary, key);
        if (Model.TryGetValue("output", out var output))
        {
            if (output is not TomlTable table) throw new InvalidDataException("deew config: output must be a table.");
            if (table.ContainsKey("to_input")) Require<bool>(table, "to_input");
            if (table.ContainsKey("path")) Require<string>(table, "path");
        }
        if (DdpMode is not ("standard" or "bluray") || AtmosMode is not ("streaming" or "standard" or "bluray"))
            throw new InvalidDataException("deew config: unsupported default_modes value.");
    }

    private string Mode(string key, string fallback)
    {
        if (!Model.TryGetValue("default_modes", out var value)) return fallback;
        if (value is not TomlTable table) throw new InvalidDataException("deew config: default_modes must be a table.");
        return table.ContainsKey(key) ? Require<string>(table, key) : fallback;
    }

    public byte[] Edit(string? managedTempPath)
    {
        var edits = new List<(int Offset, int Length, string Value)>();
        void Replace(KeyValueSyntax node, string value)
        {
            var span = node.Value!.Span;
            edits.Add((span.Offset, span.Length, value));
        }
        // Syntax spans handle quoted keys, both string forms and multiline strings without regex guessing.
        foreach (var node in _syntax.KeyValues)
        {
            var key = node.Key!.ToString().Trim().Trim('"', '\'');
            if (key == "logo") Replace(node, "0");
            if (key == "temp_path" && managedTempPath is not null) Replace(node, Quote(managedTempPath));
        }
        var output = _syntax.Tables.FirstOrDefault(t => t.Name!.ToString().Trim().Trim('"', '\'') == "output");
        var toInput = output?.Items.FirstOrDefault(k => k.Key!.ToString().Trim().Trim('"', '\'') == "to_input");
        var newline = _text.Contains("\r\n") ? "\r\n" : "\n";
        if (toInput is not null) Replace(toInput, "false");
        else if (output is not null) edits.Add((output.CloseBracket!.Span.End.Offset + 1, 0, newline + "to_input = false"));
        else if (Model.ContainsKey("output"))
        {
            // Inline/dotted tables are valid TOML; preserve their metadata through the TOML model serializer.
            Model["logo"] = 0L;
            if (managedTempPath is not null) Model["temp_path"] = managedTempPath;
            ((TomlTable)Model["output"])["to_input"] = false;
            return Encoding.UTF8.GetBytes(Toml.FromModel(Model));
        }
        else edits.Add((_text.Length, 0, newline + "[output]" + newline + "to_input = false" + newline));
        var text = new StringBuilder(_text);
        foreach (var edit in edits.OrderByDescending(e => e.Offset)) text.Remove(edit.Offset, edit.Length).Insert(edit.Offset, edit.Value);
        var bytes = Encoding.UTF8.GetBytes(text.ToString());
        var check = new DeewConfigEditor(bytes);
        if ((bool)((TomlTable)check.Model["output"])["to_input"] || (long)check.Model["logo"] != 0
            || managedTempPath is not null && check.TempPath != managedTempPath)
            throw new InvalidDataException("Could not safely edit deew config.toml.");
        return bytes;
    }

    private static string Quote(string value) => JsonSerializer.Serialize(value);
    private static T Require<T>(TomlTable table, string key) => table.TryGetValue(key, out var value) && value is T typed
        ? typed : throw new InvalidDataException($"deew config: missing or invalid {key} ({typeof(T).Name}). Repair the config manually; -gc would reset it.");
}
