namespace Aurum.App.Infrastructure.AI.Ollama.Answers;

/// <summary>
/// What the Check job answers about one explanation (FR-2.4). A draft: item 6 sets the pass mark.
/// </summary>
/// <param name="Score">
/// 0 to 100: is it backed by the facts given, do the sources support it, does it make sense, is it
/// recent.
/// </param>
/// <param name="GivesAdvice">
/// Separate from the score, so advice fails the check however well-sourced the rest is (§13).
/// </param>
/// <param name="Reason">Why the score is what it is, kept for reading a low score later.</param>
public sealed record CheckAnswer(int Score, bool GivesAdvice, string Reason);
