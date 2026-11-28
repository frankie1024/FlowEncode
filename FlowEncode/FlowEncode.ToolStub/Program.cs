using System.Text.Json;
using System.Text.RegularExpressions;

Console.OutputEncoding = new System.Text.UTF8Encoding(false);

// A real child process for lifecycle tests, independent of licensed external tools.
var tool = Path.GetFileNameWithoutExtension(Environment.ProcessPath!);
string? Option(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
var businessScenarioPath = Path.Combine(AppContext.BaseDirectory, "business-scenario.txt");
var businessScenario = File.Exists(businessScenarioPath) ? File.ReadAllText(businessScenarioPath).Trim() : null;
if (businessScenario is not null)
{
    await File.AppendAllTextAsync(Path.Combine(AppContext.BaseDirectory, tool + "-calls.log"), string.Join(" ", args) + "\n");
    if (businessScenario == "probe-hang" && tool == "av1an")
    {
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "probe-pid.txt"), Environment.ProcessId.ToString());
        await Task.Delay(TimeSpan.FromMinutes(1));
        return 0;
    }
}
if (tool is "vspipe" or "x264")
    await File.AppendAllTextAsync(Path.Combine(AppContext.BaseDirectory, tool + "-calls.log"), string.Join(" ", args) + "\n");
if (tool == "python" && args.Contains("-c"))
{
    await File.AppendAllTextAsync(Path.Combine(AppContext.BaseDirectory, "python-probes.log"), "probe\n");
    Console.WriteLine($"3.14.10\n{Environment.ProcessPath}\n64\nAMD64\nVS_PRESENT");
    return 0;
}
if (args.Contains("--hold-child")) { await Task.Delay(TimeSpan.FromMinutes(1)); return 0; }
const string deewVersion = "5.7.0";
if (args.Contains("--version") || args.Contains("-version") || args.Contains("--help"))
{
    var deeVersion = businessScenario == "legacy-dee" ? "6.6.0" : "6.7.0+test";
    Console.WriteLine(tool.StartsWith("deew") ? $"deew {deewVersion}" : tool.StartsWith("dee_") && tool != "dee_shim" ? $"Dolby Encoding Engine version {deeVersion}" : tool + " version 1.0.0");
    return 0;
}
if (tool == "vspipe" && args.Contains("--info"))
{
    using var script = JsonDocument.Parse(await File.ReadAllTextAsync(args[^1]));
    var mediaPath = script.RootElement.GetProperty("source").GetString()!;
    if (!File.Exists(mediaPath))
    {
        Console.Error.WriteLine("Script evaluation failed:");
        Console.Error.WriteLine("Traceback (most recent call last):");
        Console.Error.WriteLine($"FileNotFoundError: Source file does not exist: {mediaPath}");
        return 1;
    }
    Console.WriteLine("Width: 16\nHeight: 16\nFrames: 24\nFormat Name: YUV420P8\nBits: 8\nFPS: 24/1");
    return 0;
}
if (tool == "x264") return 0;
if (businessScenario is not null && tool == "ffmpeg" && args.Contains("-muxers"))
{
    Console.WriteLine("yuv4mpegpipe");
    return 0;
}
if (businessScenario is not null && tool == "ffmpeg" && (args.Contains("-encoders") || args.Contains("-filters")))
    return 0;
if (businessScenario is not null && tool == "ffprobe")
{
    if (args.Contains("-show_chapters"))
    {
        Console.WriteLine("{\"chapters\":[{\"id\":1,\"start_time\":\"0.000000\",\"tags\":{\"title\":\"序幕\"}},{\"id\":2,\"start_time\":\"312.345000\",\"tags\":{\"title\":\"第一章\"}}]}");
        return 0;
    }

    if (Option("-show_entries") == "stream=codec_name,pix_fmt,nb_read_frames")
    {
        Console.WriteLine("{\"streams\":[{\"codec_name\":\"av1\",\"pix_fmt\":\"yuv420p\",\"nb_read_frames\":\"1\"}]}");
        return 0;
    }

    var isVideoCheck = Option("-of") == "csv=p=0";
    Console.WriteLine(isVideoCheck ? businessScenario == "broken-output" ? "" : "video"
        : "{\"streams\":[{\"codec_name\":\"pcm_s24le\",\"channels\":2,\"channel_layout\":\"stereo\",\"sample_rate\":\"48000\"}]}");
    return 0;
}
if (businessScenario is not null && tool == "av1an")
{
    void Emit(string type) => Console.WriteLine(JsonSerializer.Serialize(new { type, ts = DateTimeOffset.UtcNow, message = "test backend failure" }));
    if (businessScenario is "failure-then-complete" or "cancel-then-complete")
        Emit(businessScenario == "failure-then-complete" ? "run_failed" : "run_cancelled");
    var outputFile = Option("-o")!;
    if (businessScenario != "no-output")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputFile)!);
        await File.WriteAllTextAsync(outputFile, businessScenario == "empty-output" ? "" : "encoded video");
    }
    Emit("run_completed");
    if (businessScenario == "complete-then-failure") Emit("run_failed");
    return 0;
}
if (businessScenario is "mkv-demux" or "mkv-demux-no-chapters" && tool == "eac3to")
{
    if (args.Length == 1 && args[0].EndsWith(".mkv", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine("Fast mode enabled");
        Console.WriteLine("Keeping dialnorm");
        Console.WriteLine("analyze: 100%");
        Console.WriteLine("MKV, 1 video track, 1 audio track, 2 subtitle tracks, 0:00:02, 24p");
        if (businessScenario == "mkv-demux")
        {
            Console.WriteLine("1: Chapters, 12 chapters");
            Console.WriteLine("2: h265/HEVC, 1920x1080p24");
            Console.WriteLine("3: FLAC, [jpn], 2.0 channels, 0:00:02, 24 bits, 48kHz");
            Console.WriteLine("   \"Japanese FLAC\"");
            Console.WriteLine("4: Subtitle (SRT), [chi]");
            Console.WriteLine("5: Subtitle (ASS), [eng]");
        }
        else
        {
            Console.WriteLine("1: h265/HEVC, 1920x1080p24");
            Console.WriteLine("2: FLAC, [jpn], 2.0 channels, 0:00:02, 24 bits, 48kHz");
            Console.WriteLine("3: Subtitle (SRT), [chi]");
        }
        return 0;
    }

    foreach (var stagedOutput in args.Where(a => a.Contains(".flowencode-temp", StringComparison.OrdinalIgnoreCase)))
    {
        var materialized = stagedOutput.EndsWith(".*", StringComparison.Ordinal)
            ? stagedOutput.Replace(".*", stagedOutput.Contains("_video", StringComparison.OrdinalIgnoreCase) ? ".h265" : ".flac")
            : stagedOutput;
        Directory.CreateDirectory(Path.GetDirectoryName(materialized)!);
        await File.WriteAllTextAsync(materialized, "extracted mkv track");
    }

    Console.WriteLine("process: 100%");
    Console.WriteLine("Done.");
    return 0;
}
if (tool == "dgdemux" && businessScenario is "dgdemux-named-outputs" or "dgdemux-missing-audio" or "dgdemux-missing-chapters" or "dgdemux-untagged-outputs")
{
    var prefix = Option("-o")!;
    var untagged = businessScenario == "dgdemux-untagged-outputs";
    await File.WriteAllTextAsync(prefix + (untagged ? ".avc" : " PID 4113.avc"), "extracted video");
    await File.WriteAllTextAsync(prefix + ".qp.txt", "chapter keyframes");
    if (businessScenario == "dgdemux-missing-audio")
    {
        await File.WriteAllTextAsync(prefix + " PID 4353 7.1 48 1536 eng DELAY 0ms.dts", "another audio track");
        await File.WriteAllTextAsync(prefix + " PID 4352.txt", "track metadata");
    }
    else
        await File.WriteAllTextAsync(prefix + (untagged ? ".hevc" : " PID 4352 7.1 48 1536 eng DELAY 0ms.dts"), "extracted track");
    if (businessScenario != "dgdemux-missing-chapters")
        await File.WriteAllTextAsync(prefix + ".chapters.txt", "extracted chapters");
    Console.WriteLine("Done!");
    return 0;
}
if (businessScenario is not null && tool is "dgdemux" or "eac3to")
{
    var outputFile = tool == "dgdemux" ? Option("-o") + ".264"
        : args.First(a => a.Contains(".flowencode-temp", StringComparison.OrdinalIgnoreCase)).Replace(".*", ".264");
    if (businessScenario != "no-output")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputFile)!);
        await File.WriteAllTextAsync(outputFile, businessScenario == "empty-output" ? "" : "extracted media");
    }
    Console.WriteLine(tool == "dgdemux" ? "Done!" : "Done.");
    return 0;
}
if (args.Contains("-gc"))
{
    var keys = "dd_1_0 dd_2_0 dd_5_1 ddp_1_0 ddp_2_0 ddp_5_1 ddp_7_1_standard ddp_7_1_bluray ddp_atmos_streaming ddp_atmos_standard ddp_atmos_bluray ac4_2_0 ac4_5_1 ac4_atmos ac4_ims aac_2_0".Split(' ');
    await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "config.toml"),
        "surround_phase_shift = true\nlow_pass_filter = true\nsurround_3db_attenuation = false\ntruehdd_warp_mode = ''\ntruehdd_bed_conform = false\npipe = 'auto'\ntemp_path = ''\nlogo = 1\nmax_instances = '50%'\n[output]\nto_input = true\ntemplate = '{stem}'\n[default_bitrates]\n" +
        string.Join("\n", keys.Select(k => k + " = 256")) +
        "\n[default_modes]\nddp = 'standard'\nddp_atmos = 'streaming'\nac4_ims = 'general'\n[summary_sections]\ndeew_info = true\nbinaries = true\ninput_info = true\noutput_info = true\nother = true\n[gui]\nprogress = 'cli'\nscaling = '100%'\nuse_system_font = false\ntheme = 'system'\n");
    return 0;
}
var sourcePath = tool == "ffprobe" ? args[^1] : Option("-i")!;
using var input = JsonDocument.Parse(await File.ReadAllTextAsync(sourcePath));
var data = input.RootElement;
var scenario = data.TryGetProperty("scenario", out var scenarioValue) ? scenarioValue.GetString() ?? "success" : "success";
var channels = data.TryGetProperty("channels", out var channelsValue) ? channelsValue.GetInt32() : 2;
if (tool == "ffprobe")
{
    var track = Option("-select_streams");
    if (track == "a:1") channels = 8;
    var layout = data.TryGetProperty("layout", out var layoutValue) ? layoutValue.GetString()
        : channels switch { 8 => "7.1", 7 => "7.0", 6 => "5.1", 5 => "5.0", _ => "stereo" };
    var codec = data.TryGetProperty("codec", out var codecValue) ? codecValue.GetString() : "pcm_s24le";
    var profile = data.TryGetProperty("profile", out var profileValue) ? profileValue.GetString() : "";
    var rate = data.TryGetProperty("sampleRate", out var rateValue) ? rateValue.GetString() : "48000";
    var tags = new Dictionary<string, string>();
    if (data.TryGetProperty("language", out var langValue) && langValue.GetString() is string lang) tags["language"] = lang;
    if (data.TryGetProperty("title", out var titleValue) && titleValue.GetString() is string title) tags["title"] = title;
    Console.WriteLine(JsonSerializer.Serialize(new { streams = new[] { new { codec_name = codec, profile, channels, channel_layout = layout, sample_rate = rate, bits_per_sample = "24", duration = "60", tags = tags.Count > 0 ? tags : null } } }));
    return 0;
}
if (args.Contains("--probe-json"))
{
    var auro = data.TryGetProperty("auro", out var auroValue) && Option("-ti") == "1" ? new { layout = auroValue.GetString() } : null;
    Console.WriteLine(JsonSerializer.Serialize(new { files = new[] { new { auro } } }));
    return 0;
}
if (args.Contains("-nps") || args.Contains("--no-phase-shift"))
{
    Console.Error.WriteLine("Unrecognized surround phase-shift option.");
    return 2;
}
var configPath = Path.Combine(AppContext.BaseDirectory, "config.toml");
var config = await File.ReadAllTextAsync(configPath);
var temp = JsonSerializer.Deserialize<string>(Regex.Match(config, "(?m)^temp_path\\s*=\\s*(\".*\")").Groups[1].Value)!;
Directory.CreateDirectory(temp);
await File.WriteAllTextAsync(Path.Combine(temp, "intermediate.pcm"), "PCM");
var events = Environment.GetEnvironmentVariable("DEEW_EVENTS_FILE")!;
if (scenario == "cancel")
    await File.WriteAllTextAsync(Path.Combine(temp, "process-id.txt"), Environment.ProcessId.ToString());
async Task Event(object value) => await File.AppendAllTextAsync(events, JsonSerializer.Serialize(value) + "\n");
await Event(new { type = "hello", version = deewVersion });
await Event(new { type = "progress", job = 0, stage = "starting", percent = (double?)null, db = (double?)null });
await Event(new { type = "warning", text = "测试诊断" });
if (scenario == "spatial")
{
    await Event(new { type = "progress", job = 0, stage = "decode", decoder = "harletty", percent = 50 });
    await Task.Delay(350);
}
if (scenario == "live-log")
{
    await Event(new { type = "progress", job = 0, stage = "encode", percent = 50, db = (double?)null });
    for (var line = 0; line < 60; line++) Console.WriteLine($"输入摘要 {line}");
    Console.WriteLine("\u001b[32m实际 stdout 输出\u001b[0m");
    Console.Error.WriteLine("实际 stderr 输出");
    Console.Out.Flush(); Console.Error.Flush();
    await Task.Delay(800);
    Console.Write("末尾未换行"); Console.Out.Flush();
}
if (scenario == "stage-eta")
{
    await Event(new { type = "progress", job = 0, stage = "ffmpeg", percent = 50, db = (double?)null });
    // No more progress events: the host must still update the estimate on its polling timer.
    await Task.Delay(5800);
    await Event(new { type = "progress", job = 0, stage = "measure", percent = 0, db = (double?)null });
    await Task.Delay(300);
    await Event(new { type = "progress", job = 0, stage = "measure", percent = 50, db = -27 });
    await Task.Delay(300);
}
var output = Option("-o")!;
// Deliberately honor the upstream to_input priority to catch a missing lease override.
if (Regex.IsMatch(config, @"(?m)^\s*to_input\s*=\s*true")) output = Path.GetDirectoryName(sourcePath)!;
Directory.CreateDirectory(output);
if (args.Contains("-kl"))
{
    await File.WriteAllTextAsync(Path.Combine(output, "output.deew-log.html"), "diagnostic");
    await File.WriteAllTextAsync(Path.Combine(output, "output.thd.log.html"), "truehd diagnostic");
}
if (scenario == "cancel")
{
    Console.WriteLine("waiting for cancellation"); Console.Out.Flush();
    await Task.Delay(TimeSpan.FromMinutes(1));
}
if (scenario == "piped-progress")
{
    await Event(new { type = "progress", job = 0, stage = "measure", percent = (double?)null, db = (double?)null });
    await Event(new { type = "progress", job = 0, stage = "measure", percent = 50.0, db = (double?)null });
    await Event(new { type = "progress", job = 0, stage = "measure", percent = 100.0, db = -24.0 });
    await Task.Delay(250);
    // Stage transition pulse (percent: null) after numeric progress must not reset host or TaskCoordinator progress.
    await Event(new { type = "progress", job = 0, stage = "encode", percent = (double?)null, db = -24.0 });
    await Task.Delay(250);
    await Event(new { type = "progress", job = 0, stage = "encode", percent = 40.0, db = -24.0 });
    await Event(new { type = "progress", job = 0, stage = "encode", percent = 99.0, db = -24.0 });
}
var measured = args.Contains("-mo");
if (scenario is "rich-measure" or "quiet-measure")
    await Event(new { type = "progress", job = 0, stage = "measure", percent = 90, db = scenario == "quiet-measure" ? -45 : -27,
        gating = "dialogue", speech = 16, program = -18.9, lra = 13.1, true_peak = -4.9 });
await Event(new { type = "progress", job = 0, stage = measured ? "measure" : "encode", percent = 100,
    db = scenario == "missing-db" ? (double?)null : scenario == "quiet-measure" ? -45.0 : -27.0 });
if (!measured && scenario != "empty" && scenario != "error-tail")
{
    var format = Option("-f");
    var ext = format switch { "dd" => ".ac3", "thd" or "thd-atmos" => ".thd", "ac4" or "ac4-atmos" or "ac4-ims" => ".ac4", "aac" => ".aac", _ => ".ec3" };
    if (format == "ddp" && (channels is 7 or 8 || args.Contains("-ti")) && Option("--ddp-mode") == "bluray") ext = ".eb3";
    var outputNameArg = Option("-on") ?? Option("--output-name") ?? args.FirstOrDefault(a => a.StartsWith("--output-name=", StringComparison.Ordinal))?["--output-name=".Length..];
    var outputTemplateArg = Option("-ot") ?? Option("--output-template") ?? args.FirstOrDefault(a => a.StartsWith("--output-template=", StringComparison.Ordinal))?["--output-template=".Length..];
    if (outputTemplateArg is null)
    {
        var templateMatch = Regex.Match(config, @"(?m)^\s*template\s*=\s*['""]([^'""]+)['""]");
        if (templateMatch.Success) outputTemplateArg = templateMatch.Groups[1].Value;
    }
    var stem = Regex.Replace(Path.GetFileNameWithoutExtension(sourcePath), @" ?DELAY [-|+]?[0-9]+m?s", "");
    string fileName;
    if (!string.IsNullOrEmpty(outputNameArg))
        fileName = outputNameArg.EndsWith(ext, StringComparison.OrdinalIgnoreCase) ? outputNameArg : outputNameArg + ext;
    else if (!string.IsNullOrEmpty(outputTemplateArg) && outputTemplateArg != "{stem}")
    {
        var lang = data.TryGetProperty("language", out var lv) ? lv.GetString() ?? "" : "";
        var title = data.TryGetProperty("title", out var tv) ? tv.GetString() ?? "" : "";
        var rendered = outputTemplateArg
            .Replace("{stem}", stem)
            .Replace("{name}", Path.GetFileNameWithoutExtension(sourcePath))
            .Replace("{format}", format ?? "ddp")
            .Replace("{lang}", lang)
            .Replace("{title}", title)
            .Replace("{track}", Option("-ti") ?? "0")
            .Trim('.', '_', '-', ' ');
        fileName = rendered.EndsWith(ext, StringComparison.OrdinalIgnoreCase) ? rendered : rendered + ext;
    }
    else fileName = stem + ext;
    await File.WriteAllTextAsync(Path.Combine(output, fileName), "encoded media");
    if (scenario == "collision")
        await File.WriteAllTextAsync(Path.Combine(output, "z-conflict" + ext), "second encoded media");
}
await Event(new { type = "done", job = 0 });
if (scenario == "error-tail") await Event(new { type = "error", text = "error appended after done" });
if (scenario == "partial-tail") await File.AppendAllTextAsync(events, "{\"type\":\"error\",\"text\":\"incomplete");
if (scenario == "exit130") return 130;
return scenario == "failure" ? 1 : 0;
