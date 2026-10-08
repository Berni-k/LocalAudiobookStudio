using System.Text.Json.Nodes;
using LocalAudiobookStudio.Core.IO;
using LocalAudiobookStudio.Core.Model;
using LocalAudiobookStudio.Core.Serialization;

namespace LocalAudiobookStudio.Tests;

public class ProjectStoreTests
{
    [Fact]
    public void CreateSaveAndReload()
    {
        using var tmp = new TempDir();
        var root = Path.Combine(tmp.Path, "Book");
        var p = ProjectStore.Create(root, "My Book");
        var ch = new Chapter { Title = "One" };
        ch.Items.Add(new TimelineItem { Text = "Hello.", Speaker = "narrator", SettingsOverride = new JsonObject { ["seed"] = 1234 } });
        ch.Items.Add(new TimelineItem { Kind = ItemKind.Pause, DurationMs = 1500 });
        p.Chapters.Add(ch);
        ProjectStore.SaveAll(p);

        var q = ProjectStore.Load(root);
        Assert.Equal("My Book", q.File.Title);
        Assert.Single(q.Chapters);
        Assert.Equal(ch.Id, q.Chapters[0].Id);
        Assert.Equal("Hello.", q.Chapters[0].Items[0].Text);
        Assert.Equal(1234, q.Chapters[0].Items[0].SettingsOverride!["seed"]!.GetValue<int>());
        Assert.Equal(ItemKind.Pause, q.Chapters[0].Items[1].Kind);
        Assert.True(q.Speakers.ContainsKey("narrator"));
    }

    [Fact]
    public void JsonUsesCamelCaseAndStringKinds()
    {
        var json = CoreJson.Serialize(new TimelineItem { Kind = ItemKind.Clip, ClipFile = "assets/rain.opus", GapAfterMs = -3000 });
        Assert.Contains("\"kind\": \"clip\"", json);
        Assert.Contains("\"gapAfterMs\": -3000", json);
        Assert.DoesNotContain("\"text\"", json); // nulls omitted
    }

    [Fact]
    public void RefusesNewerFormat()
    {
        using var tmp = new TempDir();
        File.WriteAllText(Path.Combine(tmp.Path, ProjectFile.FileName), "{\"app\": \"LocalAudiobookStudio\", \"formatVersion\": 99, \"title\": \"x\"}");
        var ex = Assert.Throws<ProjectFormatException>(() => ProjectStore.Load(tmp.Path));
        Assert.Contains("newer", ex.Message);
    }

    [Fact]
    public void RefusesForeignProjectJson()
    {
        using var tmp = new TempDir();
        File.WriteAllText(Path.Combine(tmp.Path, ProjectFile.FileName), "{\"formatVersion\": 1, \"title\": \"x\"}");
        Assert.False(ProjectStore.IsProjectFolder(tmp.Path));
        Assert.Throws<ProjectFormatException>(() => ProjectStore.Load(tmp.Path));
    }

    [Fact]
    public void AtomicWriteLeavesNoTempFile()
    {
        using var tmp = new TempDir();
        var f = Path.Combine(tmp.Path, "a.json");
        AtomicFile.WriteAllText(f, "1");
        AtomicFile.WriteAllText(f, "2");
        Assert.Equal("2", File.ReadAllText(f));
        Assert.False(File.Exists(f + ".tmp"));
    }

    [Fact]
    public async Task SaveQueueDebouncesAndWritesLatest()
    {
        using var tmp = new TempDir();
        var f = Path.Combine(tmp.Path, "q.json");
        var calls = 0;
        await using (var q = new SaveQueue(TimeSpan.FromMilliseconds(100)))
        {
            for (int i = 0; i < 20; i++)
            {
                var v = i;
                q.Enqueue(f, () => { Interlocked.Increment(ref calls); return System.Text.Encoding.UTF8.GetBytes(v.ToString()); });
            }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!File.Exists(f) && sw.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(20);
            Assert.Equal("19", File.ReadAllText(f));
            Assert.Equal(1, calls);
        }
    }
}
