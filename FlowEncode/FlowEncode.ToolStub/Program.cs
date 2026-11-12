using System.Text.Json;
using System.Text.RegularExpressions;

// A real child process for lifecycle tests, independent of licensed external tools.
var tool = Path.GetFileNameWithoutExtension(Environment.ProcessPath!);
string? Option(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
if (tool == "python" && args.Contains("-c"))
{
    await File.AppendAllTextAsync(Path.Combine(AppContext.BaseDirectory, "python-probes.log"), "probe\n");
    Console.WriteLine($"3.14.10\n{Environment.ProcessPath}\n64\nAMD64\nVS_PRESENT");
    return 0;
}
if (args.Contains("--hold-child")) { await Task.Delay(TimeSpan.FromMinutes(1)); return 0; }
if (args.Contains("--version") || args.Contains("-version") || args.Contains("--help"))
{
    Console.WriteLine(tool.StartsWith("deew") ? "deew 5.1.0" : tool.StartsWith("dee_") ? "Dolby Encoding Engine version 6.7.0+test" : tool + " version 1.0.0");
    return 0;
}
if (args.Contains("-gc"))
{
    var keys = "dd_1_0 dd_2_0 dd_5_1 ddp_1_0 ddp_2_0 ddp_5_1 ddp_7_1_standard ddp_7_1_bluray ddp_atmos_streaming ddp_atmos_standard ddp_atmos_bluray ac4_2_0 ac4_5_1 ac4_atmos aac_2_0".Split(' ');
    await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "config.toml"),
        "temp_path = ''\nlogo = 1\nmax_instances = '50%'\n[output]\nto_input = true\n[default_bitrates]\n" +
        string.Join("\n", keys.Select(k => k + " = 256")) + "\n[summary_sections]\ndeew_info = true\nbinaries = true\ninput_info = true\noutput_info = true\nother = true\n");
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
    var layout = channels switch { 8 => "7.1", 7 => "7.0", 6 => "5.1", 5 => "5.0", _ => "stereo" };
    Console.WriteLine(JsonSerializer.Serialize(new { streams = new[] { new { codec_name = "pcm_s24le", profile = "", channels, channel_layout = layout, sample_rate = "48000", bits_per_sample = "24", duration = "60" } } }));
    return 0;
}
var configPath = Path.Combine(AppContext.BaseDirectory, "config.toml");
var config = await File.ReadAllTextAsync(configPath);
var temp = JsonSerializer.Deserialize<string>(Regex.Match(config, "(?m)^temp_path\\s*=\\s*(\".*\")").Groups[1].Value)!;
Directory.CreateDirectory(temp);
await File.WriteAllTextAsync(Path.Combine(temp, "intermediate.pcm"), "PCM");
var events = Environment.GetEnvironmentVariable("DEEW_EVENTS_FILE")!;
async Task Event(object value) => await File.AppendAllTextAsync(events, JsonSerializer.Serialize(value) + "\n");
await Event(new { type = "hello", version = "5.1.0" });
await Event(new { type = "progress", job = 0, stage = "starting", percent = (double?)null, db = (double?)null });
await Event(new { type = "warning", text = "测试诊断" });
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
var measured = args.Contains("-mo");
await Event(new { type = "progress", job = 0, stage = measured ? "measure" : "encode", percent = 100,
    db = scenario == "missing-db" ? (double?)null : -27.0 });
if (!measured && scenario != "empty" && scenario != "error-tail")
{
    var format = Option("-f");
    var ext = format switch { "dd" => ".ac3", "thd" or "thd-atmos" => ".thd", "ac4" or "ac4-atmos" => ".ac4", "aac" => ".aac", _ => ".ec3" };
    if (format == "ddp" && (channels is 7 or 8 || args.Contains("-ti")) && Option("--ddp-mode") == "bluray") ext = ".eb3";
    await File.WriteAllTextAsync(Path.Combine(output, Path.GetFileNameWithoutExtension(sourcePath) + ext), "encoded media");
    if (scenario == "collision")
        await File.WriteAllTextAsync(Path.Combine(output, "z-conflict" + ext), "second encoded media");
}
await Event(new { type = "done", job = 0 });
if (scenario == "error-tail") await Event(new { type = "error", text = "error appended after done" });
if (scenario == "partial-tail") await File.AppendAllTextAsync(events, "{\"type\":\"error\",\"text\":\"incomplete");
if (scenario == "exit130") return 130;
return scenario == "failure" ? 1 : 0;
