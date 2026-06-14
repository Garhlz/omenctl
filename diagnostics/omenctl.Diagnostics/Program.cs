using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using OmenCtl.Core;

JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web)
{
    WriteIndented = true
};

int exitCode = await MainAsync(args).ConfigureAwait(false);
return exitCode;

async Task<int> MainAsync(string[] argv)
{
    if(argv.Length == 0 || IsHelp(argv[0]))
    {
        PrintUsage();
        return 0;
    }

    try
    {
        RepoContext repo = RepoContext.Discover();
        CliOptions options = CliOptions.Parse(argv[1..]);

        return argv[0] switch
        {
            "snapshot-log" => await RunSnapshotLogAsync(repo, options).ConfigureAwait(false),
            "apply-readback-batch" => await RunApplyReadbackBatchAsync(repo, options).ConfigureAwait(false),
            "curve-watch" => await RunCurveWatchAsync(repo, options).ConfigureAwait(false),
            "smoke" => await RunSmokeAsync(repo, options).ConfigureAwait(false),
            _ => Fail($"Unknown command: {argv[0]}")
        };
    }
    catch(Exception ex)
    {
        Console.Error.WriteLine($"error: {ex}");
        return 1;
    }
}

async Task<int> RunSnapshotLogAsync(RepoContext repo, CliOptions options)
{
    int count = options.GetInt("count", 5, min: 1, max: 10000);
    int intervalSeconds = options.GetInt("interval-seconds", 2, min: 1, max: 3600);

    await using AgentSession session = await AgentSession.StartAsync(repo).ConfigureAwait(false);
    AgentEnvelope first = await session.SendAsync(new AgentCommand("snapshot")).ConfigureAwait(false);
    string product = first.RequireProduct();
    OutputTargets output = OutputTargets.Create(repo, product, "snapshot-log", options.GetString("output"));

    List<string> cpuSources = [];
    List<string> gpuSources = [];
    Dictionary<string, int> warningCounts = new(StringComparer.Ordinal);
    int successCount = 0;

    await using JsonlWriter writer = new(output.JsonlPath, jsonOptions);

    await writer.WriteAsync(new
    {
        eventType = "snapshot",
        index = 0,
        recordedAt = DateTimeOffset.Now,
        response = first
    }).ConfigureAwait(false);
    AccumulateSnapshot(first, cpuSources, gpuSources, warningCounts, ref successCount);

    for(int index = 1; index < count; index++)
    {
        await Task.Delay(TimeSpan.FromSeconds(intervalSeconds)).ConfigureAwait(false);
        AgentEnvelope response = await session.SendAsync(new AgentCommand("snapshot")).ConfigureAwait(false);
        await writer.WriteAsync(new
        {
            eventType = "snapshot",
            index,
            recordedAt = DateTimeOffset.Now,
            response
        }).ConfigureAwait(false);
        AccumulateSnapshot(response, cpuSources, gpuSources, warningCounts, ref successCount);
    }

    object summary = new
    {
        command = "snapshot-log",
        product,
        count,
        intervalSeconds,
        successCount,
        cpuSources = CountByValue(cpuSources),
        gpuSources = CountByValue(gpuSources),
        warningCounts,
        jsonl = output.JsonlPath,
        summary = output.SummaryPath
    };

    await output.WriteSummaryAsync(summary, jsonOptions).ConfigureAwait(false);
    PrintSummary(summary);
    return 0;
}

async Task<int> RunApplyReadbackBatchAsync(RepoContext repo, CliOptions options)
{
    int[] delays = options.GetIntList("delays", [0, 1, 3, 5, 15], min: 0, max: 3600);

    BatchCommand[] commands =
    [
        new("manual-35-35", new AgentCommand("setManual", CpuLevel: 35, GpuLevel: 35)),
        new("manual-45-45", new AgentCommand("setManual", CpuLevel: 45, GpuLevel: 45)),
        new("manual-50-50", new AgentCommand("setManual", CpuLevel: 50, GpuLevel: 50)),
        new("max", new AgentCommand("setMax"))
    ];

    await using AgentSession session = await AgentSession.StartAsync(repo).ConfigureAwait(false);
    string product = (await session.SendAsync(new AgentCommand("snapshot")).ConfigureAwait(false)).RequireProduct();
    OutputTargets output = OutputTargets.Create(repo, product, "apply-readback-batch", options.GetString("output"));

    List<object> commandSummaries = [];
    await using JsonlWriter writer = new(output.JsonlPath, jsonOptions);

    foreach(BatchCommand item in commands)
    {
        AgentEnvelope response = await session.SendAsync(new
        {
            cmd = "applyAndReadback",
            command = item.Command,
            delays
        }).ConfigureAwait(false);

        await writer.WriteAsync(new
        {
            eventType = "apply-readback-batch",
            label = item.Label,
            recordedAt = DateTimeOffset.Now,
            delays,
            response
        }).ConfigureAwait(false);

        commandSummaries.Add(BuildApplySummary(item.Label, response));
    }

    object summary = new
    {
        command = "apply-readback-batch",
        product,
        delays,
        commands = commandSummaries,
        jsonl = output.JsonlPath,
        summary = output.SummaryPath
    };

    await output.WriteSummaryAsync(summary, jsonOptions).ConfigureAwait(false);
    PrintSummary(summary);
    return 0;
}

async Task<int> RunCurveWatchAsync(RepoContext repo, CliOptions options)
{
    int durationSeconds = options.GetInt("duration-seconds", 60, min: 5, max: 86400);
    int pollSeconds = options.GetInt("poll-seconds", 5, min: 1, max: 3600);
    int curveIntervalSeconds = options.GetInt("curve-interval-seconds", 5, min: 1, max: 3600);
    int hysteresisC = options.GetInt("hysteresis-c", 2, min: 0, max: 10);

    // Use the canonical device profile curve — single source of truth
    FanCurvePoint[] points = DeviceProfiles.EightBabCurve;

    await using AgentSession session = await AgentSession.StartAsync(repo).ConfigureAwait(false);
    string product = (await session.SendAsync(new AgentCommand("snapshot")).ConfigureAwait(false)).RequireProduct();
    OutputTargets output = OutputTargets.Create(repo, product, "curve-watch", options.GetString("output"));

    List<double> temperatures = [];
    List<string> sources = [];
    int errorCount = 0;
    int maxTickCount = 0;
    int maxApplyCount = 0;
    int sourceFlaps = 0;
    string? lastSource = null;
    int? lastApplyCount = null;
    int applyChanges = 0;

    await using JsonlWriter writer = new(output.JsonlPath, jsonOptions);
    AgentEnvelope start = await session.SendAsync(new
    {
        cmd = "startCurve",
        intervalSeconds = curveIntervalSeconds,
        hysteresisC,
        points
    }).ConfigureAwait(false);

    await writer.WriteAsync(new
    {
        eventType = "curve-start",
        recordedAt = DateTimeOffset.Now,
        response = start
    }).ConfigureAwait(false);

    Stopwatch watch = Stopwatch.StartNew();
    while(watch.Elapsed < TimeSpan.FromSeconds(durationSeconds))
    {
        await Task.Delay(TimeSpan.FromSeconds(pollSeconds)).ConfigureAwait(false);
        AgentEnvelope status = await session.SendAsync(new { cmd = "curveStatus" }).ConfigureAwait(false);
        AgentEnvelope snapshot = await session.SendAsync(new AgentCommand("snapshot")).ConfigureAwait(false);
        await writer.WriteAsync(new
        {
            eventType = "curve-status",
            elapsedSeconds = Math.Round(watch.Elapsed.TotalSeconds, 2),
            recordedAt = DateTimeOffset.Now,
            response = status,
            snapshot
        }).ConfigureAwait(false);

        if(status.TryGetDataProperty("lastTemperature", out JsonElement tempElement) && tempElement.ValueKind == JsonValueKind.Number)
            temperatures.Add(tempElement.GetDouble());
        else if(snapshot.TryGetNestedNumber(["temps", "cpu", "value"], out double cpuTemp))
            temperatures.Add(cpuTemp);
        if(status.TryGetDataProperty("lastTemperatureSource", out JsonElement sourceElement) && sourceElement.ValueKind == JsonValueKind.String)
        {
            string source = sourceElement.GetString()!;
            sources.Add(source);
            if(lastSource is not null && !string.Equals(lastSource, source, StringComparison.Ordinal))
                sourceFlaps++;
            lastSource = source;
        }
        else if(snapshot.TryGetNestedString(["temps", "cpu", "source"], out string? cpuSource))
        {
            sources.Add(cpuSource!);
            if(lastSource is not null && !string.Equals(lastSource, cpuSource, StringComparison.Ordinal))
                sourceFlaps++;
            lastSource = cpuSource!;
        }
        if(status.TryGetDataProperty("lastError", out JsonElement errorElement) && errorElement.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(errorElement.GetString()))
            errorCount++;
        if(status.TryGetDataProperty("tickCount", out JsonElement tickElement) && tickElement.ValueKind == JsonValueKind.Number)
            maxTickCount = Math.Max(maxTickCount, tickElement.GetInt32());
        if(status.TryGetDataProperty("applyCount", out JsonElement applyElement) && applyElement.ValueKind == JsonValueKind.Number)
        {
            int apply = applyElement.GetInt32();
            maxApplyCount = Math.Max(maxApplyCount, apply);
            if(lastApplyCount is not null && apply != lastApplyCount.Value)
                applyChanges++;
            lastApplyCount = apply;
        }
    }

    AgentEnvelope stop = await session.SendAsync(new { cmd = "stopCurve" }).ConfigureAwait(false);
    await writer.WriteAsync(new
    {
        eventType = "curve-stop",
        recordedAt = DateTimeOffset.Now,
        response = stop
    }).ConfigureAwait(false);

    object summary = new
    {
        command = "curve-watch",
        product,
        durationSeconds,
        pollSeconds,
        curveIntervalSeconds,
        hysteresisC,
        sampleCount = temperatures.Count,
        temperature = temperatures.Count == 0 ? null : new
        {
            min = temperatures.Min(),
            max = temperatures.Max(),
            avg = Math.Round(temperatures.Average(), 2)
        },
        sources = CountByValue(sources),
        sourceFlaps,
        errorCount,
        maxTickCount,
        maxApplyCount,
        applyChanges,
        jsonl = output.JsonlPath,
        summary = output.SummaryPath
    };

    await output.WriteSummaryAsync(summary, jsonOptions).ConfigureAwait(false);
    PrintSummary(summary);
    return 0;
}

void AccumulateSnapshot(
    AgentEnvelope response,
    List<string> cpuSources,
    List<string> gpuSources,
    IDictionary<string, int> warningCounts,
    ref int successCount)
{
    if(!response.Ok)
        return;

    successCount++;
    if(response.TryGetNestedString(["temps", "cpu", "source"], out string? cpuSource))
        cpuSources.Add(cpuSource!);
    if(response.TryGetNestedString(["temps", "gpu", "source"], out string? gpuSource))
        gpuSources.Add(gpuSource!);

    if(response.TryGetNestedArray(["warnings"], out JsonElement warnings))
    {
        foreach(JsonElement warning in warnings.EnumerateArray())
        {
            string key = warning.GetString() ?? "unknown";
            warningCounts[key] = warningCounts.TryGetValue(key, out int current) ? current + 1 : 1;
        }
    }
}

object BuildApplySummary(string label, AgentEnvelope response)
{
    string? requestedLevel = null;
    string? finalBiosLevel = null;
    List<string> warnings = [];
    bool passed = false;

    if(response.Ok && response.Data.ValueKind == JsonValueKind.Array)
    {
        JsonElement[] rows = response.Data.EnumerateArray().ToArray();
        if(rows.Length > 0
            && rows[0].ValueKind == JsonValueKind.Object
            && rows[0].TryGetProperty("requestedCommand", out JsonElement reqCmd))
            requestedLevel = reqCmd.GetString();

        if(rows.Length > 0
            && rows[^1].ValueKind == JsonValueKind.Object
            && rows[^1].TryGetProperty("resultSnapshot", out JsonElement snapshot))
        {
            if(JsonElementHelpers.TryGetNestedString(snapshot, ["biosFan", "level"], out string? biosLevel))
                finalBiosLevel = biosLevel;
            if(JsonElementHelpers.TryGetNestedArray(snapshot, ["warnings"], out JsonElement warnArr))
            {
                foreach(JsonElement w in warnArr.EnumerateArray())
                    warnings.Add(w.GetString() ?? "");
            }
        }
    }

    // pass if: ok, got a readback level, and no hardware error warnings
    passed = response.Ok && finalBiosLevel is not null
        && !warnings.Any(w => w.Contains("bios_unavailable", StringComparison.OrdinalIgnoreCase)
                          || w.Contains("bios_error", StringComparison.OrdinalIgnoreCase));

    return new
    {
        label,
        ok = response.Ok,
        passed,
        requestedLevel,
        finalBiosLevel,
        warnings = warnings.Count > 0 ? warnings : null,
        error = response.Error
    };
}

Dictionary<string, int> CountByValue(IEnumerable<string> values) =>
    values
        .GroupBy(value => value, StringComparer.Ordinal)
        .OrderBy(group => group.Key, StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

void PrintSummary(object summary)
{
    Console.WriteLine(JsonSerializer.Serialize(summary, jsonOptions));
}

int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}

bool IsHelp(string value) =>
    string.Equals(value, "help", StringComparison.OrdinalIgnoreCase)
    || string.Equals(value, "--help", StringComparison.OrdinalIgnoreCase)
    || string.Equals(value, "-h", StringComparison.OrdinalIgnoreCase);

async Task<int> RunSmokeAsync(RepoContext repo, CliOptions options)
{
    Console.WriteLine("=== omenctl smoke test ===");

    int exitCode;

    Console.WriteLine("\n[1/3] snapshot-log (3 samples)...");
    CliOptions snapOpts = CliOptions.Parse(["--count", "3", "--interval-seconds", "1"]);
    exitCode = await RunSnapshotLogAsync(repo, snapOpts).ConfigureAwait(false);
    if(exitCode != 0) return exitCode;

    Console.WriteLine("\n[2/3] apply-readback-batch...");
    CliOptions batchOpts = CliOptions.Parse(["--delays", "0,1,3,5"]);
    exitCode = await RunApplyReadbackBatchAsync(repo, batchOpts).ConfigureAwait(false);
    if(exitCode != 0) return exitCode;

    Console.WriteLine("\n[3/3] curve-watch (30s)...");
    CliOptions curveOpts = CliOptions.Parse(["--duration-seconds", "30", "--poll-seconds", "5"]);
    exitCode = await RunCurveWatchAsync(repo, curveOpts).ConfigureAwait(false);

    Console.WriteLine(exitCode == 0 ? "\n✓ smoke passed" : "\n✗ smoke failed");
    return exitCode;
}

void PrintUsage()
{
    Console.WriteLine("""
Usage:
  omenctl.Diagnostics smoke
  omenctl.Diagnostics snapshot-log [--count N] [--interval-seconds N] [--output path]
  omenctl.Diagnostics apply-readback-batch [--delays 0,1,3,5,15] [--output path]
  omenctl.Diagnostics curve-watch [--duration-seconds N] [--poll-seconds N] [--curve-interval-seconds N] [--hysteresis-c N] [--output path]
""");
}

sealed class CliOptions
{
    private readonly Dictionary<string, string> values;

    private CliOptions(Dictionary<string, string> values)
    {
        this.values = values;
    }

    public static CliOptions Parse(string[] args)
    {
        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
        for(int index = 0; index < args.Length; index++)
        {
            string arg = args[index];
            if(!arg.StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Unexpected argument: {arg}");

            string key = arg[2..];
            string value = index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[++index]
                : "true";
            values[key] = value;
        }

        return new CliOptions(values);
    }

    public int GetInt(string key, int fallback, int min, int max)
    {
        if(!values.TryGetValue(key, out string? raw))
            return fallback;
        if(!int.TryParse(raw, out int value))
            throw new ArgumentException($"Option --{key} expects an integer.");
        return Math.Clamp(value, min, max);
    }

    public int[] GetIntList(string key, int[] fallback, int min, int max)
    {
        if(!values.TryGetValue(key, out string? raw))
            return fallback;

        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item =>
            {
                if(!int.TryParse(item, out int value))
                    throw new ArgumentException($"Option --{key} expects a comma separated list of integers.");
                return Math.Clamp(value, min, max);
            })
            .ToArray();
    }

    public string? GetString(string key) =>
        values.TryGetValue(key, out string? value) ? value : null;
}

sealed class RepoContext
{
    public string RootPath { get; }
    public string DotnetPath { get; }
    public string AgentDllPath { get; }

    private RepoContext(string rootPath, string dotnetPath, string agentDllPath)
    {
        RootPath = rootPath;
        DotnetPath = dotnetPath;
        AgentDllPath = agentDllPath;
    }

    public static RepoContext Discover()
    {
        string root = FindRepoRoot(AppContext.BaseDirectory);
        string scoopDotnet = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "scoop", "apps", "dotnet-sdk", "current", "dotnet.exe");
        string dotnet = File.Exists(scoopDotnet) ? scoopDotnet : "dotnet";
        string agentDll = Path.Combine(root, "src", "omenctl.Agent", "bin", "x64", "Release", "net10.0-windows", "omenctl.dll");
        if(!File.Exists(agentDll))
            throw new FileNotFoundException("Could not find built omenctl agent DLL. Run .\\make.cmd build first.", agentDll);

        return new RepoContext(root, dotnet, agentDll);
    }

    private static string FindRepoRoot(string startPath)
    {
        DirectoryInfo? directory = new(startPath);
        while(directory is not null)
        {
            if(File.Exists(Path.Combine(directory.FullName, "omenctl.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repo root containing omenctl.sln.");
    }
}

sealed class OutputTargets
{
    public string JsonlPath { get; }
    public string SummaryPath { get; }

    private OutputTargets(string jsonlPath, string summaryPath)
    {
        JsonlPath = jsonlPath;
        SummaryPath = summaryPath;
    }

    public static OutputTargets Create(RepoContext repo, string product, string commandName, string? outputOverride)
    {
        if(!string.IsNullOrWhiteSpace(outputOverride))
        {
            string fullOutput = Path.GetFullPath(outputOverride, repo.RootPath);
            string? dir = Path.GetDirectoryName(fullOutput);
            if(!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);
            return new OutputTargets(fullOutput, Path.ChangeExtension(fullOutput, ".summary.json"));
        }

        string targetDir = Path.Combine(repo.RootPath, "diagnostics", product);
        Directory.CreateDirectory(targetDir);
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string jsonl = Path.Combine(targetDir, $"{commandName}-{stamp}.jsonl");
        string summary = Path.Combine(targetDir, $"{commandName}-{stamp}.summary.json");
        return new OutputTargets(jsonl, summary);
    }

    public Task WriteSummaryAsync(object summary, JsonSerializerOptions options) =>
        File.WriteAllTextAsync(SummaryPath, JsonSerializer.Serialize(summary, options));
}

sealed class JsonlWriter : IAsyncDisposable
{
    private readonly StreamWriter writer;
    private readonly JsonSerializerOptions options;

    public JsonlWriter(string path, JsonSerializerOptions options)
    {
        writer = new StreamWriter(path, append: false);
        this.options = options;
    }

    public async Task WriteAsync(object payload)
    {
        await writer.WriteLineAsync(JsonSerializer.Serialize(payload, options)).ConfigureAwait(false);
        await writer.FlushAsync().ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => writer.DisposeAsync();
}

sealed class AgentSession : IAsyncDisposable
{
    private readonly Process process;
    private readonly Task stderrPump;

    private AgentSession(Process process, Task stderrPump)
    {
        this.process = process;
        this.stderrPump = stderrPump;
    }

    public static AgentSession Start(RepoContext repo)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = repo.DotnetPath,
            Arguments = $"\"{repo.AgentDllPath}\"",
            WorkingDirectory = repo.RootPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start omenctl agent process.");

        Task stderrPump = Task.Run(async () =>
        {
            while(await process.StandardError.ReadLineAsync().ConfigureAwait(false) is not null)
            {
            }
        });

        return new AgentSession(process, stderrPump);
    }

    public static Task<AgentSession> StartAsync(RepoContext repo) =>
        Task.FromResult(Start(repo));

    public async Task<AgentEnvelope> SendAsync(object command)
    {
        string line = JsonSerializer.Serialize(command);
        await process.StandardInput.WriteLineAsync(line).ConfigureAwait(false);
        await process.StandardInput.FlushAsync().ConfigureAwait(false);

        string? output = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false);
        if(string.IsNullOrWhiteSpace(output))
            throw new InvalidOperationException("omenctl agent returned an empty response.");

        AgentEnvelope? envelope = JsonSerializer.Deserialize<AgentEnvelope>(output);
        return envelope ?? throw new InvalidOperationException("omenctl agent response could not be parsed.");
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            process.StandardInput.Close();
        }
        catch
        {
        }

        if(!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().ConfigureAwait(false);
        }

        await stderrPump.ConfigureAwait(false);
        process.Dispose();
    }
}

sealed record AgentEnvelope(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("data")] JsonElement Data,
    [property: JsonPropertyName("error")] AgentErrorPayload? Error)
{
    public string RequireProduct()
    {
        if(!TryGetDataProperty("product", out JsonElement product) || product.ValueKind != JsonValueKind.String)
            return "unknown";
        return product.GetString() ?? "unknown";
    }

    public bool TryGetDataProperty(string propertyName, out JsonElement value)
    {
        if(Data.ValueKind == JsonValueKind.Object && Data.TryGetProperty(propertyName, out value))
            return true;

        value = default;
        return false;
    }

    public bool TryGetNestedString(string[] path, out string? value) =>
        JsonElementHelpers.TryGetNestedString(Data, path, out value);

    public bool TryGetNestedArray(string[] path, out JsonElement value) =>
        JsonElementHelpers.TryGetNestedArray(Data, path, out value);

    public bool TryGetNestedNumber(string[] path, out double value) =>
        JsonElementHelpers.TryGetNestedNumber(Data, path, out value);
}

sealed record AgentErrorPayload(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message);

sealed record BatchCommand(string Label, AgentCommand Command);

static class JsonElementHelpers
{
    public static bool TryGetNestedString(JsonElement current, string[] path, out string? value)
    {
        value = null;
        foreach(string segment in path)
        {
            if(current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
                return false;
        }

        if(current.ValueKind != JsonValueKind.String)
            return false;
        value = current.GetString();
        return true;
    }

    public static bool TryGetNestedArray(JsonElement current, string[] path, out JsonElement value)
    {
        foreach(string segment in path)
        {
            if(current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
            {
                value = default;
                return false;
            }
        }

        value = current;
        return current.ValueKind == JsonValueKind.Array;
    }

    public static bool TryGetNestedNumber(JsonElement current, string[] path, out double value)
    {
        value = default;
        foreach(string segment in path)
        {
            if(current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
                return false;
        }

        if(current.ValueKind != JsonValueKind.Number)
            return false;
        value = current.GetDouble();
        return true;
    }
}
