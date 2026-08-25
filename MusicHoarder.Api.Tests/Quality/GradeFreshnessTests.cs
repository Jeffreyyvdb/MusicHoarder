using MusicHoarder.Api.Quality;

namespace MusicHoarder.Api.Tests.Quality;

public class GradeFreshnessTests
{
    private const string CurrentModel = "openai/gpt-4o-mini";

    [Fact]
    public void SongGrade_CurrentPromptAndModel_IsNotOutdated()
    {
        Assert.False(GradeFreshness.IsSongGradeOutdated(QualityGradingPrompt.Version, CurrentModel, CurrentModel));
    }

    [Fact]
    public void SongGrade_AgesAgainstTheSongPromptVersion_NotTheAlbumOne()
    {
        // The song rule must pair with QualityGradingPrompt — a grade stamped with the *album*
        // prompt's version number is only current if the two constants happen to coincide.
        Assert.True(GradeFreshness.IsSongGradeOutdated(QualityGradingPrompt.Version - 1, CurrentModel, CurrentModel));
        Assert.False(GradeFreshness.IsSongGradeOutdated(QualityGradingPrompt.Version, CurrentModel, CurrentModel));
    }

    [Fact]
    public void AlbumGrade_AgesAgainstTheAlbumPromptVersion()
    {
        Assert.True(GradeFreshness.IsAlbumGradeOutdated(AlbumGradingPrompt.Version - 1, CurrentModel, CurrentModel));
        Assert.False(GradeFreshness.IsAlbumGradeOutdated(AlbumGradingPrompt.Version, CurrentModel, CurrentModel));
    }

    [Theory]
    [InlineData("some/other-model")]
    [InlineData(null)]
    public void ChangedOrMissingModel_MakesTheGradeOutdated(string? gradedModel)
    {
        Assert.True(GradeFreshness.IsSongGradeOutdated(QualityGradingPrompt.Version, gradedModel, CurrentModel));
        Assert.True(GradeFreshness.IsAlbumGradeOutdated(AlbumGradingPrompt.Version, gradedModel, CurrentModel));
    }

    [Fact]
    public void ModelComparison_IsCaseSensitive()
    {
        // Ordinal comparison: a re-cased model id counts as a different model.
        Assert.True(GradeFreshness.IsSongGradeOutdated(QualityGradingPrompt.Version, "OpenAI/GPT-4o-mini", CurrentModel));
    }
}
