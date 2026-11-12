using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using FlowEncode.Domain;
using Microsoft.UI.Xaml;

namespace FlowEncode.ViewModels;

public sealed class DeewOptionsViewModel : ObservableObject
{
    private readonly Action _changed;
    private readonly Func<AppText> _texts;
    private DeewOptions _options;
    private int _channels = 6;
    private string _defaultMode = "standard";
    private string _defaultAtmos = "streaming";
    private IReadOnlyDictionary<string, int>? _bitrates;
    private string _warning = "";
    private bool _refreshing;

    public DeewOptionsViewModel(DeewOptions? options, Action changed, Func<AppText> texts)
    { _options = options ?? new(); _changed = changed; _texts = texts; Refresh(); }

    private AppText Texts => _texts();
    public string DefaultLabel => Texts.Pick("跟随 deew 默认", "Follow deew defaults");
    public IReadOnlyList<string> FormatOptions => DeewArguments.Formats;
    public IReadOnlyList<string> DdpModes => [DefaultLabel, "standard", "bluray"];
    public IReadOnlyList<string> AtmosModes => [DefaultLabel, "streaming", "standard", "bluray"];
    public IReadOnlyList<string> DrcOptions => _options.Format.StartsWith("ac4", StringComparison.Ordinal)
        ? [DefaultLabel, "film_light", "film_standard", "music_light", "music_standard", "speech"]
        : [DefaultLabel, "none", "film_light", "film_standard", "music_light", "music_standard", "speech"];
    public IReadOnlyList<string> DownmixOptions => new[] { 1, 2, 6 }.Where(n => n < _channels && (_options.Format is "dd" or "ddp" || n != 1)).Select(n => n.ToString(CultureInfo.InvariantCulture)).Prepend(DefaultLabel).ToArray();
    public ObservableCollection<string> BitrateOptions { get; } = [];
    public string Format { get => _options.Format; set => Update(_options with { Format = value ?? "ddp" }); }
    public string DdpMode { get => _options.DdpMode ?? DefaultLabel; set => Update(_options with { DdpMode = Optional(value) }); }
    public string AtmosMode { get => _options.DdpAtmosMode ?? DefaultLabel; set => Update(_options with { DdpAtmosMode = Optional(value) }); }
    public string Drc { get => _options.Drc ?? DefaultLabel; set => Update(_options with { Drc = Optional(value) }); }
    public string Downmix { get => _options.DownmixChannels?.ToString(CultureInfo.InvariantCulture) ?? DefaultLabel; set => Update(_options with { DownmixChannels = int.TryParse(value, out var n) ? n : null }); }
    public string Bitrate { get => _options.BitrateKbps?.ToString(CultureInfo.InvariantCulture) ?? DefaultLabel; set => Update(_options with { BitrateKbps = int.TryParse(value, out var n) ? n : null }); }
    public bool DisableLfe { get => _options.DisableLfe; set => Update(_options with { DisableLfe = value }); }
    public double Dialnorm { get => _options.Dialnorm ?? 0; set => Update(_options with { Dialnorm = FiniteInt(value, 0) }); }
    public string Delay { get => _options.Delay ?? ""; set => Update(_options with { Delay = string.IsNullOrWhiteSpace(value) ? null : value.Trim() }); }
    public double TrackIndex { get => _options.TrackIndex ?? 0; set => Update(_options with { TrackIndex = FiniteInt(value, 0) }); }
    public bool MeasureOnly { get => _options.MeasureOnly; set => Update(_options with { MeasureOnly = value }); }
    public Visibility MeasureOnlyVisibility => !Atmos ? Visibility.Visible : Visibility.Collapsed;
    public bool KeepLog { get => _options.KeepLog; set => Update(_options with { KeepLog = value }); }
    public bool KeepTemp { get => _options.KeepTemp; set => Update(_options with { KeepTemp = value }); }
    public string Instances { get => _options.Instances ?? ""; set => Update(_options with { Instances = string.IsNullOrWhiteSpace(value) ? null : value.Trim() }); }
    public string AdditionalArguments { get => _options.AdditionalArguments ?? ""; set => Update(_options with { AdditionalArguments = value }); }
    public IReadOnlyList<string> TempModes => [Texts.Pick("跟随输出卷", "Output volume"), Texts.Pick("自定义临时目录", "Custom temporary directory"), Texts.Pick("用 deew 自身设置", "Use deew settings")];
    public int TempModeIndex { get => (int)_options.TempMode; set => Update(_options with { TempMode = (DeewTempMode)Math.Clamp(value, 0, 2) }); }
    public string CustomTempRoot { get => _options.CustomTempRoot ?? ""; set => Update(_options with { CustomTempRoot = value }); }
    public Visibility CustomTempVisibility => _options.TempMode == DeewTempMode.Custom ? Visibility.Visible : Visibility.Collapsed;
    private bool Atmos => _options.Format.EndsWith("-atmos", StringComparison.Ordinal);
    public Visibility DdpModeVisibility => _options.Format == "ddp" && (_options.DownmixChannels ?? _channels) >= 7 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility AtmosModeVisibility => _options.Format == "ddp-atmos" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility BitrateVisibility => !_options.Format.StartsWith("thd", StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DownmixVisibility => !Atmos && _options.Format != "aac" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility LfeVisibility => !Atmos ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DialnormVisibility => _options.Format is not ("aac" or "ac4-atmos") ? Visibility.Visible : Visibility.Collapsed;
    public string Warning => _warning;
    public Visibility WarningVisibility => string.IsNullOrEmpty(_warning) ? Visibility.Collapsed : Visibility.Visible;
    public string EffectiveDescription { get; private set; } = "";

    public DeewOptions Build(ICollection<string>? warnings = null)
    {
        // Only visible panel values participate; advanced arguments are always validated explicitly.
        var o = _options with {
            DdpMode = DdpModeVisibility == Visibility.Visible ? _options.DdpMode : null,
            DdpAtmosMode = AtmosModeVisibility == Visibility.Visible ? _options.DdpAtmosMode : null,
            DownmixChannels = DownmixVisibility == Visibility.Visible ? _options.DownmixChannels : null,
            MeasureOnly = !Atmos && _options.MeasureOnly,
            DisableLfe = LfeVisibility == Visibility.Visible && _options.DisableLfe,
            BitrateKbps = BitrateVisibility == Visibility.Visible ? _options.BitrateKbps : null,
            Dialnorm = DialnormVisibility == Visibility.Visible ? _options.Dialnorm : null };
        return DeewArguments.Normalize(o, warnings);
    }

    public DeewOptions Stored => _options;
    public DeewEffectiveOutputProfile Profile => DeewBitrates.Apply(DeewArguments.Resolve(Build(), _channels, _defaultMode, _defaultAtmos), _bitrates);
    public void SetSourceChannels(int channels) { _channels = channels > 8 ? 8 : Math.Max(1, channels); Refresh(); }
    public void SetDefaults(string ddpMode, string atmosMode, IReadOnlyDictionary<string, int> bitrates)
    { _defaultMode = ddpMode; _defaultAtmos = atmosMode; _bitrates = bitrates; Refresh(); }
    public void Refresh()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var warnings = new List<string>();
            var profile = DeewBitrates.Apply(DeewArguments.Resolve(Build(warnings), _channels, _defaultMode, _defaultAtmos, warnings), _bitrates);
            _warning = string.Join(Environment.NewLine, warnings);
            if (Atmos) _warning += Texts.Pick("\nAtmos 为实验功能，3.0.0 尚无真实 Atmos 素材验收。", "\nAtmos is experimental; 3.0.0 has not passed genuine Atmos media acceptance.");
            EffectiveDescription = profile.Options.MeasureOnly ? Texts.Pick("仅测量响度，不生成媒体文件", "Measure loudness only; no media output")
                : $"{profile.Options.Format} · {profile.OutputSlots} slots · DRC {profile.Drc} · {profile.Mode} · {profile.EffectiveBitrateKbps?.ToString() ?? "lossless"} kbps · {profile.Extension}";
            if (_options.TempMode == DeewTempMode.DeewSettings) _warning += Texts.Pick("\n使用 deew 自管临时目录；取消后可能有残留，FlowEncode 不递归清理该目录。", "\nThe deew-managed temporary directory may retain files after cancellation; FlowEncode never recursively cleans it.");
            var candidates = DeewBitrates.Allowed(profile).Select(b => b.ToString(CultureInfo.InvariantCulture)).Prepend(DefaultLabel).ToArray();
            if (!BitrateOptions.SequenceEqual(candidates)) { BitrateOptions.Clear(); foreach (var candidate in candidates) BitrateOptions.Add(candidate); }
        }
        catch (Exception ex) { _warning = ex.Message; EffectiveDescription = ""; }
        OnPropertyChanged(string.Empty);
        _refreshing = false;
    }
    private void Update(DeewOptions options)
    {
        if (_refreshing || _options == options) return;
        _options = options; Refresh(); _changed();
    }
    private string? Optional(string? value) => value == DefaultLabel || string.IsNullOrWhiteSpace(value) ? null : value;
    private static int FiniteInt(double value, int fallback) => double.IsFinite(value) && value >= int.MinValue && value <= int.MaxValue ? (int)value : fallback;
}
