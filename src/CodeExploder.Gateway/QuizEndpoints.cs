using System.Text.Json;
using CodeExploder.Domain;
using CodeExploder.Gateway.Contracts;
using CodeExploder.Storage;

namespace CodeExploder.Gateway;

/// <summary>
/// Quizzes (docs/04-api.md): questions served without answer keys; deterministic
/// grading is instant; a short answer enqueues an interactive-priority grade job whose
/// result arrives via the QuizGraded event. Auto-completes the section at ≥75 %.
/// </summary>
public static class QuizEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/quizzes/{quizId:guid}", async (
                Guid quizId, HttpContext http, SessionStore sessions, QuizStore store, CancellationToken ct) =>
            {
                var userId = await ResolveUserAsync(http, sessions, ct);
                var quiz = await store.GetForUserAsync(quizId, userId, ct);
                return quiz is null ? Results.NotFound() : Results.Ok(Strip(quiz));
            })
            .RequireAuthorization()
            .Produces<QuizDto>()
            .Produces(StatusCodes.Status404NotFound);

        app.MapPost("/api/quizzes/{quizId:guid}/attempts", async (
                Guid quizId, QuizAttemptRequest body, HttpContext http,
                SessionStore sessions, QuizStore store, JobQueue queue, ISessionEventBus bus,
                CancellationToken ct) =>
            {
                if (body.Answers is not { Count: > 0 })
                {
                    return Results.BadRequest(new ErrorResponse("answers are required"));
                }

                var userId = await ResolveUserAsync(http, sessions, ct);
                var quiz = await store.GetForUserAsync(quizId, userId, ct);
                if (quiz is null)
                {
                    return Results.NotFound();
                }

                var answers = body.Answers
                    .Select(a => new QuizAnswer(a.QuestionId, a.ChoiceKeys, a.Text)).ToList();
                var questions = quiz.Questions.Select(q => (q.Id, q.Type, q.DataJson)).ToList();
                var results = QuizGrading.GradeDeterministic(questions, answers);
                var answersJson = JsonSerializer.Serialize(answers, JsonOpts);
                var feedbackJson = JsonSerializer.Serialize(results, JsonOpts);

                Guid attemptId;
                if (results.Any(r => r.NeedsLlm))
                {
                    attemptId = await store.CreateAttemptAsync(
                        quizId, userId, answersJson, "grading", null, feedbackJson, ct);
                    await queue.EnqueueAsync(
                        LlmJobTypes.GradeQuiz,
                        JsonSerializer.Serialize(new { attemptId, quizId }, JsonOpts),
                        priority: LlmJobTypes.InteractivePriority,
                        ct: ct);
                    return Results.Created(
                        $"/api/quizzes/{quizId}/attempts",
                        ToDto(attemptId, DateTimeOffset.UtcNow, "grading", null, results));
                }

                var score = QuizGrading.ComputeScore(results) ?? 0;
                attemptId = await store.CreateAttemptAsync(
                    quizId, userId, answersJson, "graded", score, feedbackJson, ct);
                await store.RecordScoreAsync(quizId, userId, score, QuizGrading.CompleteThresholdPct, ct);
                if (await store.GetSessionForQuizAsync(quizId, ct) is { } route)
                {
                    bus.Publish(route.SessionId, SessionEventKinds.QuizGraded,
                        new { attemptId, quizId, scorePct = score, sectionId = route.SectionId });
                }

                return Results.Created(
                    $"/api/quizzes/{quizId}/attempts",
                    ToDto(attemptId, DateTimeOffset.UtcNow, "graded", score, results));
            })
            .RequireAuthorization()
            .Produces<QuizAttemptDto>(StatusCodes.Status201Created)
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);

        app.MapGet("/api/quizzes/{quizId:guid}/attempts", async (
                Guid quizId, HttpContext http, SessionStore sessions, QuizStore store, CancellationToken ct) =>
            {
                var userId = await ResolveUserAsync(http, sessions, ct);
                if (await store.GetForUserAsync(quizId, userId, ct) is null)
                {
                    return Results.NotFound();
                }

                var attempts = await store.GetAttemptsAsync(quizId, userId, ct);
                return Results.Ok(attempts.Select(a => ToDto(
                    a.Id, a.SubmittedAt, a.Status, a.ScorePct,
                    a.FeedbackJson is null
                        ? []
                        : JsonSerializer.Deserialize<List<QuestionResult>>(a.FeedbackJson, JsonOpts) ?? []))
                    .ToList());
            })
            .RequireAuthorization()
            .Produces<IReadOnlyList<QuizAttemptDto>>()
            .Produces(StatusCodes.Status404NotFound);
    }

    private static Task<Guid> ResolveUserAsync(HttpContext http, SessionStore sessions, CancellationToken ct) =>
        sessions.GetOrCreateUserAsync(CurrentUser.SubjectOf(http.User), CurrentUser.NameOf(http.User), ct);

    /// <summary>Client shape: never leaks correctKeys/correct/rubric texts.</summary>
    private static QuizDto Strip(QuizRow quiz) => new(
        quiz.Id, quiz.SectionId, quiz.Title,
        quiz.Questions.Select(q =>
        {
            var data = QuizGrading.ParseData(q.DataJson);
            return new QuizQuestionDto(
                q.Id, q.Ord, q.Type, q.Prompt,
                q.Type is QuizQuestionType.SingleChoice or QuizQuestionType.MultiChoice
                    ? data.Choices?.Select(c => new QuizChoiceDto(c.Key, c.Text)).ToList()
                    : null,
                q.Type is QuizQuestionType.ShortAnswer ? data.Rubric?.MaxWords : null);
        }).ToList());

    private static QuizAttemptDto ToDto(
        Guid id, DateTimeOffset submittedAt, string status, int? scorePct,
        IReadOnlyList<QuestionResult> results) => new(
        id, submittedAt, status, scorePct,
        results.Select(r =>
        {
            // While the LLM grades, the short answer's row shows as pending, not wrong.
            // (Detected structurally — NeedsLlm is JsonIgnore'd and doesn't survive the
            // stored feedback round-trip on the GET path.)
            var pending = status == "grading" && r.Correct is null && !r.Excluded;
            return new PerQuestionResult(
                r.QuestionId, pending ? null : r.Correct, r.Excluded, pending ? null : r.FeedbackMd);
        }).ToList());
}
