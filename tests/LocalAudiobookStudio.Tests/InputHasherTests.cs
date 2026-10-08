using System.Text.Json.Nodes;
using LocalAudiobookStudio.Core.Hashing;
using LocalAudiobookStudio.Core.Model;

namespace LocalAudiobookStudio.Tests;

public class InputHasherTests
{
    private static Speaker Narrator() => new() { Id = "narrator", Worker = "kokoro", Voice = "af_heart", Settings = new JsonObject { ["speed"] = 1.0 } };

    [Fact]
    public void HashChangesWithTextSpeakerSettingsAndModel()
    {
        var item = new TimelineItem { Text = "Hello", Speaker = "narrator" };
        var s = Narrator();
        var h1 = InputHasher.Compute(item, s, "v1.0");
        Assert.StartsWith("sha256:", h1);
        Assert.Equal(h1, InputHasher.Compute(item, Narrator(), "v1.0"));

        item.Text = "Hello!";
        Assert.NotEqual(h1, InputHasher.Compute(item, s, "v1.0"));
        item.Text = "Hello";

        s.Settings["speed"] = 1.1;
        Assert.NotEqual(h1, InputHasher.Compute(item, s, "v1.0"));
        s.Settings["speed"] = 1.0;

        Assert.NotEqual(h1, InputHasher.Compute(item, s, "v1.1"));
        item.SettingsOverride = new JsonObject { ["seed"] = 5 };
        Assert.NotEqual(h1, InputHasher.Compute(item, s, "v1.0"));
    }

    [Fact]
    public void KeyOrderDoesNotMatter()
    {
        var a = new JsonObject { ["b"] = 1, ["a"] = new JsonObject { ["y"] = 2, ["x"] = 3 } };
        var b = new JsonObject { ["a"] = new JsonObject { ["x"] = 3, ["y"] = 2 }, ["b"] = 1 };
        Assert.Equal(InputHasher.Canonicalize(a), InputHasher.Canonicalize(b));
    }

    [Fact]
    public void GainAndGapDoNotAffectHash()
    {
        var item = new TimelineItem { Text = "Hi", Speaker = "narrator" };
        var h = InputHasher.Compute(item, Narrator(), "1");
        item.GainDb = 3; item.GapAfterMs = -200; item.TrimStartMs = 50;
        Assert.Equal(h, InputHasher.Compute(item, Narrator(), "1"));
    }
}
