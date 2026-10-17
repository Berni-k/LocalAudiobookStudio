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
    public void TranscriptsCarryWhoSaidEachWord()
    {
        var request = WorkersJson.Serialize(new WorkerMessage
        {
            Type = MessageTypes.TranscribeAlign,
            Id = "t1",
            AudioPath = @"C:\x\a.wav",
            Speakers = ["Alice", "Bob"],
            SpeakerCount = 2,
        });
        Assert.Contains("\"type\":\"transcribeAlign\"", request);
        Assert.Contains("\"speakers\":[\"Alice\",\"Bob\"],\"speakerCount\":2", request);

        var reply = WorkersJson.Deserialize<WorkerMessage>(
            "{\"type\": \"transcript\", \"id\": \"t1\", \"text\": \"hi there\", \"words\": [" +
            "{\"text\": \"hi\", \"startMs\": 0, \"endMs\": 300, \"speaker\": \"S0\"}, {\"text\": \"there\", \"startMs\": 400, \"endMs\": 700}]}");
        Assert.Equal("S0", reply.Words![0].Speaker);
        Assert.Null(reply.Words[1].Speaker);
    }

    [Fact]
    public void TranscriptsCanCarryTimedBlocksByVoice()
    {
        var request = WorkersJson.Serialize(new WorkerMessage { Type = MessageTypes.TranscribeDiarize, Id = "t2", AudioPath = @"C:\x\a.wav" });
        Assert.Contains("\"type\":\"transcribeDiarize\"", request);

        var reply = WorkersJson.Deserialize<WorkerMessage>(
            "{\"type\": \"transcript\", \"id\": \"t2\", \"text\": \"hi there fine\", \"words\": [], \"segments\": [" +
            "{\"text\": \"hi there\", \"startMs\": 0, \"endMs\": 900, \"speaker\": \"S01\"}, {\"text\": \"fine\", \"startMs\": 1200, \"endMs\": 1500}]}");
        Assert.Equal(2, reply.Segments!.Count);
        Assert.Equal(new TranscriptSegment("hi there", 0, 900, "S01"), reply.Segments[0]);
        Assert.Null(reply.Segments[1].Speaker);
    }

    [Fact]
    public void VoiceComparisonListsItsFilesWithRoles()
    {
        var request = WorkersJson.Serialize(new WorkerMessage
        {
            Type = MessageTypes.CompareVoices,
            Id = "v1",
            Files =
            [
                new VoiceFile { Path = @"C:\x\clip.wav", Role = VoiceRoles.Reference },
                new VoiceFile { Path = @"C:\x\take.wav", Role = VoiceRoles.Candidate },
            ],
            Settings = new JsonObject { ["model"] = "campplus" },
        });
        Assert.DoesNotContain("\n", request);
        Assert.Contains("\"type\":\"compareVoices\"", request);
        Assert.Contains("\"files\":[{\"path\":", request);
        Assert.Contains("\"role\":\"reference\"", request);
        Assert.Contains("\"role\":\"candidate\"", request);
        Assert.Contains("\"settings\":{\"model\":\"campplus\"}", request);
        Assert.DoesNotContain("\"audio\":", request); // that field carries base64 WAV bytes
    }

    [Fact]
    public void VoiceScoresReplyHasScoresMatrixSpeechAndModel()
    {
        var reply = WorkersJson.Deserialize<WorkerMessage>(
            "{\"type\": \"voiceScores\", \"id\": \"v1\", \"scores\": [0.8123], \"matrix\": [[1, 0.8123], [0.8123, 1]], " +
            "\"speechMs\": [9000, 4200], \"model\": \"eres2netv2\"}");
        Assert.Equal(MessageTypes.VoiceScores, reply.Type);
        Assert.Equal([0.8123], reply.Scores);
        Assert.Equal(0.8123, reply.Matrix![1][0]);
        Assert.Equal([9000, 4200], reply.SpeechMs);
        Assert.Equal("eres2netv2", reply.Model);
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
