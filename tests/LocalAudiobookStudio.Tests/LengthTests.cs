using LocalAudiobookStudio.Engine;
using LocalAudiobookStudio.Core.Timeline;
using LocalAudiobookStudio.Workers;
using LocalAudiobookStudio.Workers.Manifest;

namespace LocalAudiobookStudio.Tests;

public class LengthTests
{
    [Theory]
    [InlineData("distant thunder, 4s", 4.0)]
    [InlineData("rain on a tin roof for 2.5 seconds", 2.5)]
    [InlineData("crowd noise, 1 min", 60.0)]
    [InlineData("a click, 300ms", 0.3)]
    [InlineData("door slam 3 sec then silence", 3.0)]
    [InlineData("first 2s then 6s", 6.0)]
    [InlineData("1,5 s whoosh", 1.5)]
    public void FindsLengthNamedInText(string text, double expected) =>
        Assert.Equal(expected, LengthGuess.NamedIn(text)!.Value, 3);

    [Theory]
    [InlineData("heavy rain")]
    [InlineData("a 1990s radio")]
    [InlineData("v2.5s model")]
    [InlineData("")]
    public void NoLengthNamedInText(string text) => Assert.Null(LengthGuess.NamedIn(text));

    [Theory]
    [InlineData("rain on a roof, 4s", 4.0)]
    [InlineData("rain on a roof", 5.0)]      // nothing named: the default
    [InlineData("a 45 seconds drone", 30.0)] // within the worker's range
    [InlineData("tick, 300ms", 1.0)]
    [InlineData("whoosh, 2.34s", 2.3)]       // tenths of a second
    public void The_guessed_length_is_named_default_clamped_and_rounded(string text, double expected)
    {
        var worker = new WorkerManifest { Length = new LengthSupport { Mode = "required", Min = 1, Max = 30 } };
        Assert.Equal(expected, OneShot.GuessLength(worker, text, null), 3);
    }

    [Fact]
    public void A_worker_takes_no_length_unless_its_manifest_says_so() =>
        Assert.False(new WorkerManifest().SupportsLength);

    [Fact]
    public void ManifestLengthIsReadAndClamped()
    {
        var m = WorkersJson.Deserialize<WorkerManifest>("""
            { "id": "x", "length": { "mode": "Optional", "min": 0.5, "max": 30 } }
            """);
        Assert.True(m.SupportsLength);
        Assert.False(m.RequiresLength);
        Assert.Equal(30, m.ClampLength(45));
        Assert.Equal(0.5, m.ClampLength(0.1));
    }

    [Fact]
    public void MaximumFollowsTheModelSetting()
    {
        var m = WorkersJson.Deserialize<WorkerManifest>("""
            { "id": "sa3", "kind": "sfx",
              "length": { "mode": "required", "min": 0.5, "max": 120, "maxBy": { "setting": "model", "values": { "medium": 380 } } },
              "settings": [ { "key": "model", "type": "enum", "default": "small-sfx", "options": ["small-sfx", "medium"] } ] }
            """);
        Assert.Equal(120, m.MaxLengthSecFor(null));                                           // the default model
        Assert.Equal(380, m.MaxLengthSecFor(new System.Text.Json.Nodes.JsonObject { ["model"] = "medium" }));
        Assert.Equal(120, m.MaxLengthSecFor(new System.Text.Json.Nodes.JsonObject { ["model"] = "small-sfx" }));
        Assert.Equal(300, m.ClampLength(300, new System.Text.Json.Nodes.JsonObject { ["model"] = "medium" }));
        Assert.Equal(120, m.ClampLength(300));
    }
}
