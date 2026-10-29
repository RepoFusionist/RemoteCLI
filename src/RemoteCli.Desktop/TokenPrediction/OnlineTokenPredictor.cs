using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RemoteCli.Desktop.TokenPrediction;

// The model learns the remaining total for a user turn from numeric snapshots only.
internal sealed class OnlineTokenPredictor
{
    private const int FeatureCount = 8;
    private const int MaximumSnapshots = 12;
    private const double InitialOutputTokens = 512;
    private static readonly SemaphoreSlim StoreGate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly string? _directory;
    private readonly string _group;
    private readonly TokenModel _model;
    private readonly List<TokenSnapshot> _snapshots = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _initialContextChars = -1;
    private long _contextChars;
    private long _inputTokens;
    private long _reasoningChars;
    private long _outputChars;
    private long _confirmedTokens;
    private long _unconfirmedTokenEstimate;
    private int _completedRounds;
    private int _toolCalls;
    private bool _hasIncompleteRound;
    private double _visibleTokens;
    private long _lastEmitMs = -1000;
    private long _charsSinceEmit;

    private OnlineTokenPredictor(string? directory, string group, TokenModel model, string? warning)
    {
        _directory = directory;
        _group = group;
        _model = model;
        Warning = warning;
    }

    internal string? Warning { get; }
    internal int SampleCount => _model.SampleCount;

    internal static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteCLI", "TokenPrediction");

    internal static string ResolveDirectory(string? configuredPath)
    {
        var path = string.IsNullOrWhiteSpace(configuredPath) ? DefaultDirectory : Environment.ExpandEnvironmentVariables(configuredPath.Trim());
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Training directory must be an absolute path.");
        return Path.GetFullPath(path);
    }

    internal static async Task<OnlineTokenPredictor> CreateAsync(string? configuredPath, string groupKey)
    {
        var group = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(groupKey)));
        try
        {
            var directory = ResolveDirectory(configuredPath);
            var data = await Task.Run(() => ReadData(directory)).ConfigureAwait(false);
            var model = data.Models.TryGetValue(group, out var existing) ? existing : TokenModel.Create();
            return new OnlineTokenPredictor(directory, group, model, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            return new OnlineTokenPredictor(null, group, TokenModel.Create(), "无法读取 Token 训练数据，请检查设置中的目录。当前使用冷启动估算。");
        }
    }

    internal long BeginRound(long contextChars, long estimatedInputTokens)
    {
        _contextChars = Math.Max(0, contextChars);
        if (_initialContextChars < 0) _initialContextChars = _contextChars;
        _inputTokens = Math.Max(0, estimatedInputTokens);
        _reasoningChars = 0;
        _outputChars = 0;
        _visibleTokens = 0;
        _charsSinceEmit = 0;
        return Capture();
    }

    // Returns a new forecast periodically, without persisting or retaining output text.
    internal long? Observe(string kind, string text)
    {
        if (kind == "reasoning") _reasoningChars += text.Length;
        else _outputChars += text.Length;
        foreach (var ch in text)
        {
            _visibleTokens += ch is >= '\u3400' and <= '\u9fff' or >= '\u3040' and <= '\u30ff' or >= '\uac00' and <= '\ud7af'
                ? 1.0 / 1.7 : 1.0 / 3.3;
        }
        _charsSinceEmit += text.Length;
        if (_clock.ElapsedMilliseconds - _lastEmitMs < 250 && _charsSinceEmit < 64) return null;
        _lastEmitMs = _clock.ElapsedMilliseconds;
        _charsSinceEmit = 0;
        return Capture();
    }

    internal long Capture()
    {
        var snapshot = new TokenSnapshot
        {
            InitialContextChars = Math.Max(0, _initialContextChars),
            ContextChars = _contextChars,
            InputTokens = _inputTokens,
            ReasoningChars = _reasoningChars,
            OutputChars = _outputChars,
            ConfirmedTokens = _confirmedTokens,
            UnconfirmedTokenEstimate = _unconfirmedTokenEstimate,
            CompletedRounds = _completedRounds,
            ToolCalls = _toolCalls,
            VisibleTokenEstimate = _visibleTokens
        };
        snapshot.PredictedTotal = _model.Predict(snapshot);
        if (_snapshots.Count < MaximumSnapshots) _snapshots.Add(snapshot);
        else _snapshots[^1] = snapshot;
        return snapshot.PredictedTotal;
    }

    internal void CompleteRound(long? actualTokens, int toolCalls)
    {
        if (actualTokens is >= 0) _confirmedTokens += actualTokens.Value;
        else
        {
            _hasIncompleteRound = true;
            _unconfirmedTokenEstimate += (long)Math.Round(_inputTokens + InitialOutputTokens + _visibleTokens * 0.5);
        }
        _completedRounds++;
        _toolCalls += Math.Max(0, toolCalls);
    }

    internal async Task<string?> LearnAsync(long actualTotal)
    {
        if (_directory is null) return Warning;
        if (actualTotal < 0 || _hasIncompleteRound || _completedRounds == 0 || _snapshots.Count == 0 || _snapshots.Any(s => s.ConfirmedTokens > actualTotal)) return null;
        await StoreGate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(() => SaveSample(_directory, _group, _snapshots, actualTotal)).ConfigureAwait(false);
        }
        finally { StoreGate.Release(); }
    }

    private static string? SaveSample(string directory, string group, List<TokenSnapshot> snapshots, long actualTotal)
    {
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(directory);
            // Avoid silently overwriting another process's training update.
            using var fileLock = new FileStream(Path.Combine(directory, "token-prediction.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var data = ReadData(directory);
            if (!data.Models.TryGetValue(group, out var model)) data.Models[group] = model = TokenModel.Create();
            model.Learn(snapshots, actualTotal);
            data.Samples.Add(new TokenTrainingSample { AtUtc = DateTimeOffset.UtcNow, Group = group, ActualTotal = actualTotal, Snapshots = snapshots.ToArray() });
            var target = Path.Combine(directory, "token-prediction-data.json");
            temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(data, JsonOptions), new UTF8Encoding(false));
            File.Move(temporary, target, true);
            temporary = null;
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            Trace.TraceWarning("Token prediction training data save failed: {0}", exception);
            return "无法保存 Token 训练数据，请检查设置中的目录或稍后重试。";
        }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static TokenTrainingData ReadData(string directory)
    {
        var path = Path.Combine(directory, "token-prediction-data.json");
        if (!File.Exists(path)) return new TokenTrainingData();
        var data = JsonSerializer.Deserialize<TokenTrainingData>(File.ReadAllText(path, Encoding.UTF8), JsonOptions)
            ?? throw new JsonException("Empty prediction data.");
        if (data.Version != 1 || data.Models is null || data.Samples is null || data.Models.Values.Any(model => model is null || !model.IsValid()))
            throw new JsonException("Invalid prediction data.");
        return data;
    }

    private sealed class TokenTrainingData
    {
        public TokenTrainingData() { }
        public int Version { get; set; } = 1;
        public Dictionary<string, TokenModel> Models { get; set; } = new(StringComparer.Ordinal);
        public List<TokenTrainingSample> Samples { get; set; } = [];
    }

    private sealed class TokenTrainingSample
    {
        public TokenTrainingSample() { }
        public DateTimeOffset AtUtc { get; set; }
        public string Group { get; set; } = "";
        public long ActualTotal { get; set; }
        public TokenSnapshot[] Snapshots { get; set; } = [];
    }

    private sealed class TokenSnapshot
    {
        public TokenSnapshot() { }
        public long InitialContextChars { get; set; }
        public long ContextChars { get; set; }
        public long InputTokens { get; set; }
        public long ReasoningChars { get; set; }
        public long OutputChars { get; set; }
        public long ConfirmedTokens { get; set; }
        public long UnconfirmedTokenEstimate { get; set; }
        public long PredictedTotal { get; set; }
        public int CompletedRounds { get; set; }
        public int ToolCalls { get; set; }
        public double VisibleTokenEstimate { get; set; }

        public double[] Features() =>
        [
            1, Scale(InitialContextChars), Scale(ContextChars), Scale(InputTokens),
            Scale(ReasoningChars), Scale(OutputChars), Math.Min(CompletedRounds, 30) / 30.0, Math.Min(ToolCalls, 20) / 20.0
        ];

        private static double Scale(long value) => Math.Min(2, Math.Log(1 + Math.Max(0, (double)value)) / 10);
    }

    private sealed class TokenModel
    {
        public TokenModel() { }
        public int SampleCount { get; set; }
        public double[] Weights { get; set; } = [];

        public static TokenModel Create()
        {
            var weights = new double[FeatureCount];
            weights[0] = Math.Log(InitialOutputTokens + 1);
            return new TokenModel { Weights = weights };
        }

        public bool IsValid() => SampleCount >= 0 && Weights is { Length: FeatureCount } && Weights.All(double.IsFinite);

        public long Predict(TokenSnapshot snapshot)
        {
            var features = snapshot.Features();
            var logPrediction = 0.0;
            for (var i = 0; i < FeatureCount; i++) logPrediction += Weights[i] * features[i];
            var learned = Math.Exp(Math.Clamp(logPrediction, 0, 20)) - 1;
            var prior = snapshot.InputTokens + InitialOutputTokens + snapshot.VisibleTokenEstimate * 0.5;
            var blend = SampleCount / (SampleCount + 20.0);
            var total = snapshot.ConfirmedTokens + snapshot.UnconfirmedTokenEstimate + (1 - blend) * prior + blend * learned;
            return (long)Math.Clamp(Math.Round(total), 0, 1_000_000_000_000d);
        }

        public void Learn(IReadOnlyList<TokenSnapshot> snapshots, long actualTotal)
        {
            foreach (var snapshot in snapshots)
            {
                var features = snapshot.Features();
                var prediction = 0.0;
                for (var i = 0; i < FeatureCount; i++) prediction += Weights[i] * features[i];
                var target = Math.Log(1 + (double)(actualTotal - snapshot.ConfirmedTokens));
                var error = Math.Clamp(prediction - target, -2, 2);
                for (var i = 0; i < FeatureCount; i++)
                {
                    var regularization = i == 0 ? 0 : Weights[i] * 0.0001;
                    Weights[i] -= 0.08 / snapshots.Count * (error * features[i] + regularization);
                }
            }
            if (SampleCount < int.MaxValue) SampleCount++;
        }
    }
}
