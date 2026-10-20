using Judge.Core;

namespace Judge.Server;

public static class WorkspaceViews
{
    public static void MapWorkspaceEndpoints(this WebApplication app)
    {
        app.MapGet("/api/training", (Store s, HttpContext ctx) =>
        {
            var user = ctx.Items["acmUser"] as User;
            var rows = user == null ? [] : s.QuerySubmissions(userId: user.Id, limit: null);
            var now = DateTimeOffset.UtcNow;
            var hidden = s.Contests().Where(c => c.Phase(now) == "upcoming").SelectMany(c => c.ProblemIds).ToHashSet();
            var visible = s.Problems().Where(p => p.Published && !hidden.Contains(p.Id)).Select(p => p.Id).ToHashSet();
            var solved = rows.Where(x => x.Status == "AC" && visible.Contains(x.ProblemId)).Select(x => x.ProblemId).Distinct().ToArray();
            var attempted = rows.Where(x => visible.Contains(x.ProblemId)).Select(x => x.ProblemId).Distinct().Except(solved).ToArray();
            return Results.Ok(new
            {
                solvedProblemIds = solved, attemptedProblemIds = attempted,
                submissionCount = rows.Count, acceptedSubmissionCount = rows.Count(x => x.Status == "AC"),
                runningCount = rows.Count(x => JudgeQueue.IsActive(x.Status)),
                recent = rows.Where(x => visible.Contains(x.ProblemId)).Take(12),
                activity = Enumerable.Range(0, 7).Select(offset =>
                {
                    var day = now.UtcDateTime.Date.AddDays(offset - 6);
                    var daily = rows.Where(x => x.CreatedAt.UtcDateTime.Date == day).ToList();
                    return new { date = day.ToString("yyyy-MM-dd"), submissions = daily.Count, accepted = daily.Count(x => x.Status == "AC") };
                })
            });
        });
    }
}
