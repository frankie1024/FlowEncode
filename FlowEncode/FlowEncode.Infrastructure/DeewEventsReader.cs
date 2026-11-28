using System.Text;
using System.Text.Json;
using FlowEncode.Domain;

namespace FlowEncode.Infrastructure;

// One reader per fresh job file. Offset and Decoder both survive polling boundaries.
internal sealed class DeewEventsReader
{
    private long _offset;
    private readonly Decoder _decoder = new UTF8Encoding(false, false).GetDecoder();
    private readonly StringBuilder _partial = new();
    private readonly DeewEffectiveOutputProfile _profile;
    private readonly Action<string> _diagnostic;
    private readonly Action<double?, string, double?, DeewDbSource> _progress;
    private readonly DeewStageTimeEstimate _timeEstimate;
    private Dictionary<string, (double Start, double End)> _stageRanges;
    public bool HasHello { get; private set; }
    public string? Error { get; private set; }
    public double? Db { get; private set; }
    public DeewLoudnessMetrics? LoudnessMetrics { get; private set; }
    public double Fraction { get; private set; }
    public TimeSpan? Remaining => _timeEstimate.Remaining;

    public DeewEventsReader(DeewEffectiveOutputProfile profile, Action<string> diagnostic,
        Action<double?, string, double?, DeewDbSource> progress, TimeProvider? clock = null, bool piped = false)
    {
        _profile = profile; _diagnostic = diagnostic; _progress = progress;
        _timeEstimate = new(clock ?? TimeProvider.System);
        _stageRanges = BuildStageRanges(profile, piped);
    }

    private static Dictionary<string, (double Start, double End)> BuildStageRanges(DeewEffectiveOutputProfile profile, bool piped, bool hasDecode = false)
    {
        hasDecode |= profile.HasSpatialDecode;
        var effectivePiped = piped && !hasDecode && (profile.Options.MeasureOnly || profile.Options.Format is "dd" or "ddp" or "ac4" or "aac");
        var hasFfmpeg = !hasDecode && !effectivePiped;
        var stages = new List<(string Name, double Weight)>();

        if (profile.Options.MeasureOnly)
        {
            if (hasFfmpeg) stages.Add(("ffmpeg", 15));
            if (hasDecode) stages.Add(("decode", 25));
            stages.Add(("measure", 60));
        }
        else
        {
            var dialnormSet = profile.Options.Dialnorm is not null and not 0;
            var viaManifest = dialnormSet && profile.Options.Format == "ac4";
            var skipped = dialnormSet && profile.Options.Format is "dd" or "ddp" or "ddp-atmos";
            var hasMeasure = viaManifest || (!skipped && profile.Options.Format is not ("aac" or "thd"));

            if (hasFfmpeg) stages.Add(("ffmpeg", 15));
            if (hasDecode) stages.Add(("decode", 25));
            if (hasMeasure) stages.Add(("measure", effectivePiped ? 40 : 25));
            stages.Add(("encode", 60));
        }

        var total = stages.Sum(s => s.Weight);
        var map = new Dictionary<string, (double Start, double End)>(StringComparer.Ordinal);
        var cursor = 0.0;
        for (var i = 0; i < stages.Count; i++)
        {
            var end = i == stages.Count - 1 ? 1.0 : cursor + stages[i].Weight / total;
            map[stages[i].Name] = (cursor, end);
            cursor = end;
        }
        return map;
    }

    public async Task ReadAvailableAsync(string path, bool final, CancellationToken token)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, true);
            if (stream.Length < _offset) { _diagnostic("deew events file was truncated; progress unavailable."); return; }
            stream.Position = _offset;
            var bytes = new byte[4096];
            var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
            int count;
            while ((count = await stream.ReadAsync(bytes, token)) > 0)
            {
                _offset += count;
                var charCount = _decoder.GetChars(bytes, 0, count, chars, 0, flush: false);
                for (var i = 0; i < charCount; i++)
                    if (chars[i] == '\n') { Accept(_partial.ToString().TrimEnd('\r')); _partial.Clear(); }
                    else _partial.Append(chars[i]);
                if (_partial.Length > 1024 * 1024) { _diagnostic("Oversized deew event discarded."); _partial.Clear(); }
            }
            if (final && _partial.Length > 0)
            {
                // Only newline-terminated records are protocol events.
                _diagnostic("Incomplete deew event at EOF: " + _partial);
                _partial.Clear();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { if (final) _diagnostic("deew event progress unavailable: " + ex.Message); }
        if (final && !HasHello) _diagnostic("deew hello event missing; progress unavailable. Text diagnostics and result validation remain active.");
    }

    internal void Accept(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var e = doc.RootElement;
            var type = e.GetProperty("type").GetString();
            string Text() => e.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
            if (type == "hello")
            {
                HasHello = true;
                var version = e.GetProperty("version").GetString() ?? "";
                if (!DeewToolchain.IsSupportedVersion(version)) Error = $"deew {DeewArguments.MinimumVersion} or newer is required; received " + version;
            }
            else if (type == "error") { Error = Text(); if (string.IsNullOrWhiteSpace(Error)) Error = "deew reported an error."; _diagnostic("ERROR: " + Error); }
            else if (type is "warning" or "prompt") _diagnostic(type + ": " + Text());
            else if (type == "progress")
            {
                if (e.TryGetProperty("job", out var job) && job.GetInt32() != 0) return;
                var stage = e.TryGetProperty("stage", out var s) ? s.GetString() ?? "" : "";
                var harlettyDecode = stage == "decode" && e.TryGetProperty("decoder", out var decoder)
                    && decoder.ValueKind == JsonValueKind.String && decoder.GetString() == "harletty";
                if (harlettyDecode && !_stageRanges.ContainsKey(stage))
                    _stageRanges = BuildStageRanges(_profile, piped: false, hasDecode: true);
                double? percent = e.TryGetProperty("percent", out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var n) && double.IsFinite(n) ? Math.Clamp(n, 0, 100) : null;
                double? eventDb = e.TryGetProperty("db", out var db) && db.ValueKind == JsonValueKind.Number && db.TryGetDouble(out var value)
                    && double.IsFinite(value) && (_profile.Options.MeasureOnly || value is >= -31 and <= 0) ? value : null;
                var ac4Manifest = _profile.Options.Format == "ac4" && _profile.Options.Dialnorm is not null and not 0;
                // AC4 switches from measured source loudness to the requested manifest dialnorm during encoding.
                // Preserve the measured value separately instead of overwriting it with the target.
                if (eventDb.HasValue && (!ac4Manifest || stage == "measure")) Db = eventDb;
                if (!ac4Manifest || stage == "measure")
                {
                    double? Number(string name) => e.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.Number
                        && field.TryGetDouble(out var number) && double.IsFinite(number) ? number : null;
                    var gating = e.TryGetProperty("gating", out var g) && g.ValueKind == JsonValueKind.String
                        && g.GetString() is "dialogue" or "level" ? g.GetString() : null;
                    var speech = Number("speech") is double share && share is >= 0 and <= 100 ? share : (double?)null;
                    var lra = Number("lra") is double rangeLu && rangeLu >= 0 ? rangeLu : (double?)null;
                    var metrics = new DeewLoudnessMetrics(gating ?? LoudnessMetrics?.Gating,
                        speech ?? LoudnessMetrics?.SpeechPercent, Number("program") ?? LoudnessMetrics?.ProgramLoudnessLkfs,
                        lra ?? LoudnessMetrics?.LoudnessRangeLu, Number("true_peak") ?? LoudnessMetrics?.TruePeakDbtp);
                    if (metrics != new DeewLoudnessMetrics()) LoudnessMetrics = metrics;
                }
                var origin = ac4Manifest && stage == "encode" ? DeewDbSource.SpecifiedDialnorm : _profile.DbSource;
                _timeEstimate.Update(stage, percent);
                var range = _stageRanges.TryGetValue(stage, out var planned)
                    ? planned
                    : stage switch { "ffmpeg" => (0.0, .15), "decode" => (0.0, .25), "measure" => (0.0, .40), "encode" => (.40, 1.0), _ => (0.0, 0.0) };
                double? fraction = null;
                if (percent.HasValue && range.Item2 > range.Item1)
                {
                    Fraction = Math.Min(.999, Math.Max(Fraction, range.Item1 + (range.Item2 - range.Item1) * percent.Value / 100));
                    fraction = Fraction;
                }
                _progress(fraction, harlettyDecode ? "harletty decode" : stage, eventDb, origin);
            }
            // done deliberately has no effect: upstream also emits it on failure and stop.
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { _diagnostic("Invalid deew event: " + line); }
    }
}
