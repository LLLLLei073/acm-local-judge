using Judge.Core;

namespace Judge.Server;

// One settled rated contest for one participant: the resulting in-site rating and its delta.
public record RatingEntry(string ContestId, string ContestTitle, int Rating, int Delta, DateTimeOffset At);

public record StandingsProblem(string ProblemId, string Letter, string Title, int Rating);
public record StandingsCell(int Attempts, bool Accepted, int AcMinutes, bool Pending);
public record StandingsRow(int Rank, string UserId, string Name, string StudentId, bool Anonymous, int Solved, int Penalty, DateTimeOffset? LastAcAt, List<StandingsCell> Cells);
public record StandingsResponse(string ContestId, string Title, string Phase, bool Rated, bool Settled, DateTimeOffset? StartsAt, DateTimeOffset? EndsAt, List<StandingsProblem> Problems, List<StandingsRow> Rows);

// ICPC-flavoured standings (accepted count + penalty, 20 minutes per wrong submission) computed
// from the submission log within the contest window, plus the in-site Elo settlement that runs
// once a rated contest finishes. Both are pure functions of stored data, so a rejudge simply
// shows up the next time the standings are requested (before settle) and never after.
public static class Standings
{
    public const int PenaltyPerWrong = 20;
    public const int InitialRating = 1500;
    public const int MaxDelta = 350;

    public static StandingsResponse Build(Contest contest, List<Problem> problems, List<User> users, List<Submission> submissions)
    {
        var (from, to) = contest.Window();
        var byId = problems.ToDictionary(p => p.Id);
        var ordered = contest.ProblemIds.Where(byId.ContainsKey).ToList();
        var meta = ordered.Select((id, i) => new StandingsProblem(id, ((char)('A' + i)).ToString(), byId[id].Title, byId[id].Rating)).ToList();
        // (user, problem) → submission log, restricted to attributed submissions inside the window.
        var log = new Dictionary<(string UserId, string ProblemId), List<Submission>>();
        foreach (var s in submissions)
        {
            if (s.UserId == null || s.CreatedAt < from || s.CreatedAt > to || !ordered.Contains(s.ProblemId)) continue;
            if (!log.TryGetValue((s.UserId, s.ProblemId), out var list)) log[(s.UserId, s.ProblemId)] = list = [];
            list.Add(s);
        }
        var raw = new List<(string UserId, int Solved, int Penalty, DateTimeOffset? LastAc, List<StandingsCell> Cells)>();
        foreach (var userId in log.Keys.Select(k => k.UserId).Distinct())
        {
            var cells = new List<StandingsCell>(ordered.Count);
            int solved = 0, penalty = 0;
            DateTimeOffset? lastAc = null;
            foreach (var problemId in ordered)
            {
                var list = log.TryGetValue((userId, problemId), out var l) ? l : [];
                list.Sort((a, b) => a.CreatedAt.CompareTo(b.CreatedAt));
                var firstAc = list.FirstOrDefault(s => s.Status == "AC");
                int wrong = firstAc == null ? list.Count : list.TakeWhile(s => s.Status != "AC").Count();
                int acMinutes = firstAc == null ? 0 : Math.Max(0, (int)Math.Floor((firstAc.CreatedAt - from).TotalMinutes));
                if (firstAc != null) { solved++; penalty += acMinutes + PenaltyPerWrong * wrong; lastAc = lastAc == null || firstAc.CreatedAt > lastAc ? firstAc.CreatedAt : lastAc; }
                cells.Add(new StandingsCell(list.Count, firstAc != null, acMinutes, list.Any(s => JudgeQueue.IsActive(s.Status))));
            }
            raw.Add((userId, solved, penalty, lastAc, cells));
        }
        raw.Sort((a, b) => b.Solved != a.Solved ? b.Solved.CompareTo(a.Solved) : a.Penalty != b.Penalty ? a.Penalty.CompareTo(b.Penalty) : (a.LastAc ?? DateTimeOffset.MaxValue).CompareTo(b.LastAc ?? DateTimeOffset.MaxValue));
        var rows = new List<StandingsRow>();
        var userById = users.ToDictionary(u => u.Id);
        foreach (var entry in raw)
        {
            var user = userById.TryGetValue(entry.UserId, out var u) ? u : null;
            rows.Add(new StandingsRow(0, entry.UserId, user?.Anonymous != true ? user?.Name ?? "" : "", user?.Anonymous != true ? user?.StudentId ?? "" : "", user?.Anonymous == true, entry.Solved, entry.Penalty, entry.LastAc, entry.Cells));
        }
        // Shared ranks for identical (solved, penalty) pairs, CF style.
        for (int i = 0; i < rows.Count; i++)
            rows[i] = rows[i] with { Rank = i > 0 && rows[i - 1].Solved == rows[i].Solved && rows[i - 1].Penalty == rows[i].Penalty ? rows[i - 1].Rank : i + 1 };
        return new StandingsResponse(contest.Id, contest.Title, contest.Phase(DateTimeOffset.UtcNow), contest.Rated, contest.Settled, contest.StartsAt, contest.EndsAt, meta, rows);
    }

    // Multi-player Elo over the final ranks: expected win fraction vs actual win fraction,
    // K = 400, deltas clamped to ±350 and ratings floored at 100 (Codeforces-shaped, school-sized).
    public static List<(string UserId, int NewRating, int Delta)> Elo(IReadOnlyList<(string UserId, int Seed, double ActualScore)> players)
    {
        var result = new List<(string, int, int)>(players.Count);
        for (var i = 0; i < players.Count; i++)
        {
            double expected = 0;
            for (var j = 0; j < players.Count; j++)
            {
                if (j == i) continue;
                expected += 1.0 / (1.0 + Math.Pow(10, (players[j].Seed - players[i].Seed) / 400.0));
            }
            var opponents = Math.Max(1, players.Count - 1);
            var delta = (int)Math.Round(400 * (players[i].ActualScore / opponents - expected / opponents));
            if (delta > MaxDelta) delta = MaxDelta;
            if (delta < -MaxDelta) delta = -MaxDelta;
            var rating = Math.Max(100, players[i].Seed + delta);
            result.Add((players[i].UserId, rating, rating - players[i].Seed));
        }
        return result;
    }

    // Average rank for ties: everyone with identical (solved, penalty) shares the mean position,
    // which is what the Elo "opponents actually beaten" score is built from.
    public static double[] AverageRanks(IReadOnlyList<(int Solved, int Penalty)> ordered)
    {
        var ranks = new double[ordered.Count];
        int i = 0;
        while (i < ordered.Count)
        {
            int j = i;
            while (j + 1 < ordered.Count && ordered[j + 1].Solved == ordered[i].Solved && ordered[j + 1].Penalty == ordered[i].Penalty) j++;
            var average = (i + 1 + j + 1) / 2.0;
            for (var k = i; k <= j; k++) ranks[k] = average;
            i = j + 1;
        }
        return ranks;
    }

    // Settles a finished rated contest exactly once. Returns the applied changes (empty when there
    // was nothing to do or fewer than two participants) so the endpoint can surface them.
    public static List<(string UserId, int NewRating, int Delta)> SettleIfDue(Contest contest, Store store)
    {
        if (!contest.Rated || contest.Settled || contest.Phase(DateTimeOffset.UtcNow) != "finished") return [];
        lock (settleGate)
        {
            var fresh = store.Contest(contest.Id);
            if (fresh == null || fresh.Settled) return [];
            var (from, to) = fresh.Window();
            var submissions = store.AllSubmissions().Where(s => s.UserId != null && s.CreatedAt >= from && s.CreatedAt <= to && fresh.ProblemIds.Contains(s.ProblemId)).ToList();
            var participants = submissions.Select(s => s.UserId!).Distinct().ToList();
            var changes = new List<(string, int, int)>();
            if (participants.Count >= 2)
            {
                var response = Build(fresh, store.Problems(), store.Users(), store.AllSubmissions());
                // Build() returns rows already ordered by final standing; Elo works off that order.
                var ordered = response.Rows.Select(r => (r.Solved, r.Penalty)).ToList();
                var average = AverageRanks(ordered);
                var players = new List<(string, int, double)>();
                for (var i = 0; i < response.Rows.Count; i++)
                {
                    var user = store.User(response.Rows[i].UserId);
                    if (user == null) continue;
                    players.Add((user.Id, user.SiteRating ?? InitialRating, participants.Count - average[i]));
                }
                changes = Elo(players);
                foreach (var (userId, rating, delta) in changes)
                {
                    var user = store.User(userId);
                    if (user == null) continue;
                    user.SiteRating = rating;
                    store.SaveUser(user, false);
                    store.AddRating(userId, new RatingEntry(fresh.Id, fresh.Title, rating, delta, DateTimeOffset.UtcNow));
                }
            }
            fresh.Settled = true;
            store.SaveContest(fresh, false);
            return changes;
        }
    }
    private static readonly object settleGate = new();
}
