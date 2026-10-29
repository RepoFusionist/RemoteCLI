using RemoteCli.Desktop.TokenPrediction;

namespace RemoteCli.UnitTests;

public sealed class OnlineTokenPredictorTests
{
    [Fact]
    public async Task ColdStartUpdatesDuringOutputAndLearnsWithoutSavingText()
    {
        var directory = NewDirectory();
        try
        {
            var predictor = await OnlineTokenPredictor.CreateAsync(directory, "provider-model");
            Assert.Null(predictor.Warning);
            Assert.Equal(0, predictor.SampleCount);
            var initial = predictor.BeginRound(33, 10);
            Assert.Equal(522, initial);
            var forecast = predictor.Observe("reasoning", "private-marker-" + new string('a', 128));
            Assert.NotNull(forecast);
            Assert.True(forecast > initial);
            predictor.CompleteRound(936, 0);
            var warning = await predictor.LearnAsync(936);
            Assert.True(warning is null, warning);

            var persisted = await File.ReadAllTextAsync(Path.Combine(directory, "token-prediction-data.json"));
            Assert.DoesNotContain("private-marker", persisted);
            Assert.DoesNotContain("provider-model", persisted);
            var reloaded = await OnlineTokenPredictor.CreateAsync(directory, "provider-model");
            Assert.Equal(1, reloaded.SampleCount);
            Assert.True(reloaded.BeginRound(33, 10) > initial);
            var otherModel = await OnlineTokenPredictor.CreateAsync(directory, "another-model");
            Assert.Equal(0, otherModel.SampleCount);
        }
        finally { DeleteDirectory(directory); }
    }

    [Fact]
    public async Task CompletedRoundsAreIncludedAndImpossibleLabelsAreNotSaved()
    {
        var directory = NewDirectory();
        try
        {
            var predictor = await OnlineTokenPredictor.CreateAsync(directory, "model");
            predictor.BeginRound(40, 20);
            predictor.CompleteRound(700, 1);
            var next = predictor.BeginRound(200, 80);
            Assert.True(next >= 700 + 80);
            Assert.Null(await predictor.LearnAsync(699));
            Assert.False(File.Exists(Path.Combine(directory, "token-prediction-data.json")));

            var incomplete = await OnlineTokenPredictor.CreateAsync(directory, "model");
            incomplete.BeginRound(40, 20);
            incomplete.CompleteRound(null, 1);
            Assert.True(incomplete.BeginRound(200, 80) > 600);
            incomplete.CompleteRound(300, 0);
            Assert.Null(await incomplete.LearnAsync(900));
            Assert.False(File.Exists(Path.Combine(directory, "token-prediction-data.json")));
        }
        finally { DeleteDirectory(directory); }
    }

    [Fact]
    public async Task EarlierAuxiliaryRoundIsIncludedInPredictionAndTraining()
    {
        var directory = NewDirectory();
        try
        {
            var predictor = await OnlineTokenPredictor.CreateAsync(directory, "model");
            predictor.CompleteRound(125, 0);
            Assert.True(predictor.BeginRound(200, 10) >= 125 + 10 + 512);
            predictor.CompleteRound(400, 0);
            Assert.Null(await predictor.LearnAsync(525));
            var reloaded = await OnlineTokenPredictor.CreateAsync(directory, "model");
            Assert.Equal(1, reloaded.SampleCount);
        }
        finally { DeleteDirectory(directory); }
    }

    [Fact]
    public async Task ConcurrentSessionsKeepBothUpdatesAndCorruptDataIsPreserved()
    {
        var directory = NewDirectory();
        try
        {
            var first = await OnlineTokenPredictor.CreateAsync(directory, "model");
            var second = await OnlineTokenPredictor.CreateAsync(directory, "model");
            first.BeginRound(10, 3);
            second.BeginRound(20, 5);
            first.CompleteRound(300, 0);
            second.CompleteRound(600, 0);
            var results = await Task.WhenAll(first.LearnAsync(300), second.LearnAsync(600));
            Assert.All(results, Assert.Null);
            Assert.Equal(2, (await OnlineTokenPredictor.CreateAsync(directory, "model")).SampleCount);

            var file = Path.Combine(directory, "token-prediction-data.json");
            await File.WriteAllTextAsync(file, "broken data");
            var invalid = await OnlineTokenPredictor.CreateAsync(directory, "model");
            Assert.NotNull(invalid.Warning);
            invalid.BeginRound(10, 3);
            invalid.CompleteRound(300, 0);
            Assert.NotNull(await invalid.LearnAsync(300));
            Assert.Equal("broken data", await File.ReadAllTextAsync(file));
        }
        finally { DeleteDirectory(directory); }
    }

    [Fact]
    public async Task RelativeTrainingDirectoryIsRejected()
    {
        var predictor = await OnlineTokenPredictor.CreateAsync("relative-path", "model");
        Assert.NotNull(predictor.Warning);
        Assert.True(predictor.BeginRound(3, 1) > 1);
    }

    private static string NewDirectory() => Path.Combine(AppContext.BaseDirectory, "RemoteCliPredictionTest-" + Guid.NewGuid().ToString("N"));

    private static void DeleteDirectory(string directory)
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
