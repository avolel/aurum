namespace Aurum.App.Infrastructure.AI.Ollama.Answers;

/// <summary>
/// What the Write job answers for one price move (BR-04, FR-2.3). A draft: item 6 may change it.
/// </summary>
/// <param name="Summary">The 2 to 4 sentence explanation.</param>
/// <param name="MainCause">
/// Its own field, not a flag on each cause, so the shape itself allows exactly one main cause.
/// </param>
/// <param name="OtherCauses">The contributing causes, possibly none.</param>
/// <param name="ArticleNumbers">
/// The numbers the prompt gave each news article. The app looks up the links, so the model can't
/// invent a URL.
/// </param>
public sealed record ExplanationAnswer(
    string Summary,
    CauseAnswer MainCause,
    IReadOnlyList<CauseAnswer> OtherCauses,
    IReadOnlyList<int> ArticleNumbers);

/// <param name="Confidence">A percentage, 0 to 100.</param>
public sealed record CauseAnswer(Cause Cause, int Confidence);
