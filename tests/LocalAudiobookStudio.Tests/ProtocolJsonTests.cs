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
    public void FusedGenerateCarriesItsParts()
    {
        var json = WorkersJson.Serialize(new WorkerMessage
        {
            Type = MessageTypes.Generate,
            Id = "g2",
            Settings = new JsonObject { ["cfgScale"] = 1.3 },
            OutPath = @"C:\x\fused.wav",
            Parts =
            [
                new GeneratePart { Role = PartRoles.Context, Speaker = "s2", Text = "Where were you?", AudioPath = @"C:\x\take.wav" },
                new GeneratePart { Speaker = "s1", Text = "Out.", Voice = "tom.wav", Settings = new JsonObject { ["refText"] = "Hi." } },
            ],
        });
        Assert.Contains("\"parts\":[{\"role\":\"context\",\"speaker\":\"s2\",\"text\":\"Where were you?\",\"audioPath\":", json);
        Assert.Contains("{\"role\":\"generate\",\"speaker\":\"s1\",\"text\":\"Out.\",\"voice\":\"tom.wav\",\"settings\":{\"refText\":\"Hi.\"}}", json);
        Assert.DoesNotContain("\"text\":null", json);
    }

    [Fact]
    public void ParsesSpansAndErrorCodes()
    {
        var done = WorkersJson.Deserialize<WorkerMessage>(
            "{\"type\": \"done\", \"id\": \"g2\", \"spans\": [{\"startMs\": 0, \"endMs\": 900}, null]}");
        Assert.Equal(2, done.Spans!.Count);
        Assert.Equal(new PartSpan(0, 900), done.Spans[0]);
        Assert.Null(done.Spans[1]);

        var error = WorkersJson.Deserialize<WorkerMessage>(
            "{\"type\": \"error\", \"id\": \"g2\", \"message\": \"too many voices\", \"fatal\": false, \"code\": \"unfusable\"}");
        Assert.True(new WorkerErrorException(error.Message!, false, null, error.Code).IsUnfusable);
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
