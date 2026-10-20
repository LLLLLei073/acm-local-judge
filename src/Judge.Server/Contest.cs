namespace Judge.Server;

// A contest groups problems for the problemset browsing UI. The contest owns its problem list;
// deleting a problem removes it from every contest, deleting a contest never touches problems.
// A contest without a start time is an always-open practice set (CF "gym"-like); with a window it
// behaves like an AtCoder round: problems stay hidden until the start, and a rated contest settles
// an in-site Elo rating for its participants once it finishes.
public record Contest
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public List<string> ProblemIds { get; set; } = [];
    public DateTimeOffset? StartsAt { get; set; }
    public DateTimeOffset? EndsAt { get; set; }
    public bool Rated { get; set; }
    public bool Settled { get; set; }

    public void Validate(IReadOnlySet<string> knownProblemIds)
    {
        if (string.IsNullOrWhiteSpace(Title) || Title.Length > 100) throw new InvalidDataException("比赛标题应为 1–100 字符");
        if (ProblemIds.Count > 1000) throw new InvalidDataException("一场比赛最多 1000 道题");
        if (ProblemIds.Distinct().Count() != ProblemIds.Count) throw new InvalidDataException("比赛内题目重复");
        if (ProblemIds.Any(id => !knownProblemIds.Contains(id))) throw new InvalidDataException("比赛包含不存在的题目");
        if (EndsAt != null && StartsAt == null) throw new InvalidDataException("设置了结束时间但没有开始时间");
        if (StartsAt != null && EndsAt != null && EndsAt <= StartsAt) throw new InvalidDataException("结束时间必须晚于开始时间");
    }

    public string Phase(DateTimeOffset now) => StartsAt == null ? "open" : now < StartsAt ? "upcoming" : EndsAt != null && now >= EndsAt ? "finished" : "running";

    // Standing / rating computations only consider submissions inside this window.
    public (DateTimeOffset From, DateTimeOffset To) Window()
    {
        var from = StartsAt ?? DateTimeOffset.MinValue;
        var to = EndsAt ?? DateTimeOffset.MaxValue;
        return (from, to);
    }
}
