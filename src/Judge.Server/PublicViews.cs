using Judge.Core;

namespace Judge.Server;

// Contestant-facing problem view: the first test stays visible as the sample pair, everything else
// that embeds answers or judge logic (other tests, judge-tool sources, flow expectations) is stripped.
public static class PublicViews
{
    public static Problem Problem(Problem p) => p with
    {
        Checker = "",
        Interactor = "",
        Manager = "",
        WrongSource = "",
        Tests = [.. p.Tests.Select((t, i) => i == 0 ? t : new TestData(t.Name, "", ""))],
        Flow = null
    };
}
