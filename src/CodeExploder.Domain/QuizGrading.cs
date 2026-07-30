using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeExploder.Domain;

public static class QuizQuestionType
{
    public const string SingleChoice = "single";
    public const string MultiChoice = "multi";
    public const string Boolean = "boolean";
    public const string ShortAnswer = "short";
}

public sealed record QuizChoice(string Key, string Text);

public sealed record RubricKeyPoint(string Id, string Text, bool Required);

/// <summary>quiz_questions.data payload — server-only (keys/rubrics never reach clients).</summary>
public sealed record QuizQuestionData(
    List<QuizChoice>? Choices,
    List<string>? CorrectKeys,
    bool? Correct,
    string? ExplanationMd,
    QuizRubric? Rubric);

public sealed record QuizRubric(List<RubricKeyPoint> KeyPoints, int MaxWords);

public sealed record QuizAnswer(Guid QuestionId, List<string>? ChoiceKeys, string? Text);

public sealed record QuestionResult(
    Guid QuestionId,
    bool? Correct,     // null = short answer pending LLM grading, or excluded
    bool Excluded,     // ungradable/blank short answer — not counted in the score
    string? FeedbackMd,
    [property: JsonIgnore] double Points,
    [property: JsonIgnore] bool NeedsLlm);

/// <summary>
/// Deterministic grading (docs/05 §Quizzes): single/multi/boolean grade by set
/// comparison — zero LLM risk. A short answer either goes to the LLM for binary
/// key-point coverage or, when blank, is excluded from the score (never silently wrong).
/// </summary>
public static class QuizGrading
{
    public const int CompleteThresholdPct = 75;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public static QuizQuestionData ParseData(string dataJson) =>
        JsonSerializer.Deserialize<QuizQuestionData>(dataJson, JsonOpts)!;

    public static List<QuestionResult> GradeDeterministic(
        IReadOnlyList<(Guid Id, string Type, string DataJson)> questions,
        IReadOnlyList<QuizAnswer> answers)
    {
        var byQuestion = answers.ToDictionary(a => a.QuestionId);
        var results = new List<QuestionResult>();
        foreach (var (id, type, dataJson) in questions)
        {
            var data = ParseData(dataJson);
            byQuestion.TryGetValue(id, out var answer);
            results.Add(type switch
            {
                QuizQuestionType.SingleChoice or QuizQuestionType.MultiChoice => GradeChoice(id, data, answer),
                QuizQuestionType.Boolean => GradeBoolean(id, data, answer),
                QuizQuestionType.ShortAnswer => PrepareShort(id, answer),
                _ => new QuestionResult(id, null, Excluded: true, "Unknown question type", 0, NeedsLlm: false),
            });
        }

        return results;
    }

    /// <summary>Folds LLM key-point coverage into a short question's result.</summary>
    public static QuestionResult ResolveShort(
        Guid questionId, QuizQuestionData data, IReadOnlyDictionary<string, bool>? coverage)
    {
        var rubric = data.Rubric!;
        if (coverage is null)
        {
            return new QuestionResult(questionId, null, Excluded: true,
                "This answer couldn't be graded automatically — it isn't counted toward your score.", 0, false);
        }

        var required = rubric.KeyPoints.Where(k => k.Required).ToList();
        var requiredCovered = required.Count(k => coverage.GetValueOrDefault(k.Id));
        var missing = rubric.KeyPoints
            .Where(k => !coverage.GetValueOrDefault(k.Id))
            .Select(k => k.Text).ToList();

        var points = required.Count == 0 ? 1 : (double)requiredCovered / required.Count;
        var feedback = missing.Count == 0
            ? "Covers the key points."
            : "Your answer didn't mention: " + string.Join("; ", missing) + ".";
        return new QuestionResult(questionId, points >= 0.999, Excluded: false, feedback, points, false);
    }

    /// <summary>Percent over non-excluded questions; null when nothing was gradable.</summary>
    public static int? ComputeScore(IReadOnlyList<QuestionResult> results)
    {
        var counted = results.Where(r => !r.Excluded).ToList();
        return counted.Count == 0
            ? null
            : (int)Math.Round(100.0 * counted.Sum(r => r.Points) / counted.Count);
    }

    private static QuestionResult GradeChoice(Guid id, QuizQuestionData data, QuizAnswer? answer)
    {
        var chosen = (answer?.ChoiceKeys ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var correctSet = (data.CorrectKeys ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var correct = chosen.SetEquals(correctSet) && correctSet.Count > 0;
        return new QuestionResult(id, correct, false, data.ExplanationMd, correct ? 1 : 0, false);
    }

    private static QuestionResult GradeBoolean(Guid id, QuizQuestionData data, QuizAnswer? answer)
    {
        var chosen = answer?.ChoiceKeys?.FirstOrDefault();
        var correct = chosen is not null
            && bool.TryParse(chosen, out var value)
            && value == data.Correct;
        return new QuestionResult(id, correct, false, data.ExplanationMd, correct ? 1 : 0, false);
    }

    private static QuestionResult PrepareShort(Guid id, QuizAnswer? answer) =>
        string.IsNullOrWhiteSpace(answer?.Text)
            ? new QuestionResult(id, null, Excluded: true, "No answer given — not counted.", 0, false)
            : new QuestionResult(id, null, Excluded: false, null, 0, NeedsLlm: true);
}
