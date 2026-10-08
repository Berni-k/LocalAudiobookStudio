using System.Text.Json.Nodes;
using LocalAudiobookStudio.Workers;
using LocalAudiobookStudio.Workers.Protocol;

namespace LocalAudiobookStudio.Tests;

public class ProtocolJsonTests
{
    [Fact]
    public void GenerateMessageIsOneCompactLine()
    {
        var json = WorkersJson.Serialize(new WorkerMessage
        {
            Type = MessageTypes.Generate,
            Id = "g1",
            Text = "Hello",
            Settings = new JsonObject { ["speed"] = 1.0 },
            Seed = 7,
            OutPath = @"C:\x\take-0001.wav",
        });
        Assert.DoesNotContain("\n", json);
        Assert.Contains("\"type\":\"generate\"", json);
    }

    [Fact]
    public void ParsesDoneMessage()
    {
        var m = WorkersJson.Deserialize<WorkerMessage>(
            "{\"type\": \"done\", \"id\": \"g1\", \"path\": \"C:\\\\t1.wav\", \"durationMs\": 320, \"seed\": 7, \"sampleRate\": 24000}");
        Assert.Equal("done", m.Type);
        Assert.Equal(320, m.DurationMs);
    }
}
