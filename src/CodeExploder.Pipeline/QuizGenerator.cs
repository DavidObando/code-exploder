using System.Text.Json;
using CodeExploder.Domain;
using CodeExploder.Llm;
using Microsoft.Extensions.Logging;

namespace CodeExploder.Pipeline;

/// <summary>
/// S7: one ready section's markdown → 3-5 questions (docs/01 §S7, docs/05 §Quizzes).
/// Structural validation only; answer-key correctness is mitigated by always showing
/// explanations after answering.
/// </summary>
public sealed class QuizGenerator(ILlmClient llm, ILogger<QuizGenerator> logger)
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public sealed record Result(string Title, IReadOnlyList<(string Type, string Prompt, string DataJson)> Questions);

    private sealed record Response(string? Title, List<QuestionDto>? Questions);

    private sealed record QuestionDto(
        string? Type, string? Prompt, List<QuizChoice>? Choices, List<string>? CorrectKeys,
        bool? Correct, string? ExplanationMd, QuizRubric? Rubric);

    public async Task<Result> GenerateAsync(string sectionTitle, string sectionMarkdown, CancellationToken ct)
    {
        var material = $"# Section: {sectionTitle}\n\n{sectionMarkdown}";
        var call = new JsonLlmCall(llm, logger);
        var response = await call.CallAsync<Response>(
            PromptLibrary.Load(PromptLibrary.Quiz), material, Validate, maxOutputTokens: 2_000, ct);

        var questions = response.Questions!.Select(q =>
        {
            var data = new QuizQuestionData(
                q.Type is QuizQuestionType.SingleChoice or QuizQuestionType.MultiChoice ? q.Choices : null,
                q.Type is QuizQuestionType.SingleChoice or QuizQuestionType.MultiChoice ? q.CorrectKeys : null,
                q.Type is QuizQuestionType.Boolean ? q.Correct : null,
                q.ExplanationMd,
                q.Type is QuizQuestionType.ShortAnswer ? q.Rubric : null);
            return (q.Type!, q.Prompt!, JsonSerializer.Serialize(data, JsonOpts));
        }).ToList();

        return new Result(
            string.IsNullOrWhiteSpace(response.Title) ? "Check your understanding" : response.Title!,
            questions);
    }

    private static List<string> Validate(Response r)
    {
        var errors = new List<string>();
        if (r.Questions is not { Count: >= 3 and <= 5 })
        {
            errors.Add("questions must contain 3-5 items");
            return errors;
        }

        var shortCount = 0;
        var deterministicCount = 0;
        for (var i = 0; i < r.Questions.Count; i++)
        {
            var q = r.Questions[i];
            if (string.IsNullOrWhiteSpace(q.Prompt))
            {
                errors.Add($"question {i}: prompt is required");
            }

            switch (q.Type)
            {
                case QuizQuestionType.SingleChoice or QuizQuestionType.MultiChoice:
                    deterministicCount++;
                    var keys = (q.Choices ?? []).Select(c => c.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    if (q.Choices is not { Count: >= 2 and <= 5 } || keys.Count != q.Choices.Count)
                    {
                        errors.Add($"question {i}: needs 2-5 choices with unique keys");
                    }

                    if (q.CorrectKeys is not { Count: > 0 } || !q.CorrectKeys.All(keys.Contains))
                    {
                        errors.Add($"question {i}: correctKeys must be non-empty and reference existing choice keys");
                    }
                    else if (q.Type == QuizQuestionType.SingleChoice && q.CorrectKeys.Count != 1)
                    {
                        errors.Add($"question {i}: single questions need exactly one correctKey");
                    }

                    break;

                case QuizQuestionType.Boolean:
                    deterministicCount++;
                    if (q.Correct is null)
                    {
                        errors.Add($"question {i}: boolean questions need 'correct'");
                    }

                    break;

                case QuizQuestionType.ShortAnswer:
                    shortCount++;
                    if (q.Rubric is not { KeyPoints.Count: >= 1 and <= 4 } || !q.Rubric.KeyPoints.Any(k => k.Required))
                    {
                        errors.Add($"question {i}: short questions need 1-4 keyPoints with at least one required");
                    }

                    break;

                default:
                    errors.Add($"question {i}: unknown type '{q.Type}'");
                    break;
            }
        }

        if (deterministicCount < 2)
        {
            errors.Add("at least 2 auto-gradable questions required");
        }

        if (shortCount > 1)
        {
            errors.Add("at most one short question allowed");
        }

        return errors;
    }
}

/// <summary>Grades one short answer as binary key-point coverage (docs/05 §Quizzes).</summary>
public sealed class AnswerGrader(ILlmClient llm, ILogger<AnswerGrader> logger)
{
    public async Task<IReadOnlyDictionary<string, bool>?> GradeAsync(
        string questionPrompt, QuizRubric rubric, string answerText, CancellationToken ct)
    {
        var material = $"""
            ## Question
            {questionPrompt}

            ## Key points
            {string.Join("\n", rubric.KeyPoints.Select(k => $"- {k.Id}: {k.Text}"))}

            ## Student answer
            {answerText}
            """;
        try
        {
            var call = new JsonLlmCall(llm, logger);
            var coverage = await call.CallAsync<Dictionary<string, bool>>(
                PromptLibrary.Load(PromptLibrary.Grade),
                material,
                c => rubric.KeyPoints.Where(k => !c.ContainsKey(k.Id))
                    .Select(k => $"missing key point '{k.Id}'").ToList(),
                maxOutputTokens: 300,
                ct);
            return coverage;
        }
        catch (GenerationException ex)
        {
            // Ungradable ≠ wrong: the answer is excluded from the score (docs/05).
            logger.LogWarning(ex, "Short-answer grading failed twice; marking ungradable");
            return null;
        }
    }
}
