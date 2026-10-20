using Judge.Core;

namespace Judge.Server;

public record SubmissionContext(string Title, JudgeMode Mode, Limits Limits, int TestCount);

public static class SubmissionViews
{
    // Only public metadata leaves the immutable request; never return judge sources or hidden tests.
    public static SubmissionContext? Context(Store store, string id)
    {
        var path=Path.Combine(store.SubmissionDir(id),"request.json");
        if(!File.Exists(path)) return null;
        var request=Json.Read<JudgeRequest>(File.ReadAllText(path));
        var p=request.Problem;
        return new(p.Title,p.Mode,p.Limits,p.Tests.Count);
    }
}
