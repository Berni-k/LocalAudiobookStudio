using LocalAudiobookStudio.Engine.Projects;

namespace LocalAudiobookStudio.Tests;

public class ContextShorteningTests
{
    // Six words of five letters: a word starts every sixth character.
    private const string Text = "aaaaa bbbbb ccccc ddddd eeeee fffff";

    [Fact]
    public void Keeps_the_start_up_to_a_whole_word()
    {
        // 27 characters would end inside "eeeee": the cut goes back to the space before it.
        Assert.Equal("aaaaa bbbbb ccccc ddddd", ProjectEngine.Shorten(Text, 27, keepEnd: false));
    }

    [Fact]
    public void Keeps_the_end_from_a_whole_word()
    {
        // The last 27 characters start inside "bbbbb": the cut goes forward to the next word.
        Assert.Equal("ccccc ddddd eeeee fffff", ProjectEngine.Shorten(Text, 27, keepEnd: true));
    }

    [Fact]
    public void Keeps_a_word_that_ends_exactly_at_the_limit()
    {
        Assert.Equal("aaaaa bbbbb ccccc ddddd eeeee", ProjectEngine.Shorten(Text, 29, keepEnd: false));
        Assert.Equal("bbbbb ccccc ddddd eeeee fffff", ProjectEngine.Shorten(Text, 29, keepEnd: true));
    }

    [Fact]
    public void Gives_up_when_too_little_is_left()
    {
        Assert.Null(ProjectEngine.Shorten(Text, 10, keepEnd: false));
        Assert.Null(ProjectEngine.Shorten("Supercalifragilisticexpialidocious and more", 25, keepEnd: false));
    }
}
