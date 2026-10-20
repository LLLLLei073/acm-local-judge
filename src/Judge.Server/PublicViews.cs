using Judge.Core;

namespace Judge.Server;

// Contestant-facing problem view: the first test stays visible as the sample pair, everything else
// that embeds answers or judge logic (other tests, judge-tool sources, flow expectations) is stripped.
public static class PublicViews
{
    public static object Problem(Problem p) => new
    {
        p.Id, p.Title, p.Statement, p.Mode, p.Limits, p.JudgeLimits, p.Rating, p.Published,
        checker = "", interactor = "", manager = "", exampleSource = Starter(p.Mode),
        p.ManagedCommunication, p.FirstMarker, p.SecondMarker, p.ProcessCount, p.Protocol,
        revealExpected = false,
        tests = p.Tests.Select((t,i)=>i==0?t:new TestData(t.Name,"","")),
        flow = (object?)null,
        communicationSample = Sample(p)
    };

    public static CommunicationSample? Sample(Problem p)
    {
        if(p.Flow==null) return null;
        var rows=new List<CommunicationSampleStep>();
        var messages=new Dictionary<string,byte[]>();
        var sample=p.Tests.FirstOrDefault()??new TestData("sample","","");
        foreach(var step in p.Flow.Steps)
        {
            string content=step.Kind=="close"?"EOF":step.Sample;
            if(string.IsNullOrEmpty(content)&&step.Kind=="send")
            {
                try { content=System.Text.Encoding.UTF8.GetString(new[]{step.Data}.Concat(step.AdditionalData).SelectMany(d=>d.Resolve(sample,messages)).ToArray()); }
                catch(InvalidDataException) { content="（见题面协议）"; }
            }
            if(string.IsNullOrEmpty(content)) content="（见题面协议）";
            if(step.Kind=="receive") messages[step.Id]=System.Text.Encoding.UTF8.GetBytes(content);
            rows.Add(new(step.Id,step.Kind,step.From,step.To,content));
        }
        return new([.. p.Flow.Members],rows);
    }

    // A training session starts from a protocol scaffold. Reference solutions stay in admin views.
    public static string Starter(JudgeMode mode) => "#include <bits/stdc++.h>\nusing namespace std;\n\n" + (mode switch
    {
        JudgeMode.Multiprocess => "int main(int argc, char** argv) {\n    if (argc < 3) return 1;\n    const int id = stoi(argv[1]), count = stoi(argv[2]);\n    // 按题面协议读取本角色输入，输出后使用 cout << flush。\n    return 0;\n}\n",
        JudgeMode.Communication => "int main() {\n    string round;\n    if (!(cin >> round)) return 0;\n    // 按题面标记区分轮次；每轮均为新进程，不共享变量和文件。\n    return 0;\n}\n",
        JudgeMode.Interactive => "int main() {\n    // 按题面协议逐次读取输入；每次输出后 cout << flush。\n    // 不要等待 EOF 再一次性输出。\n    return 0;\n}\n",
        _ => "int main() {\n    ios::sync_with_stdio(false);\n    cin.tie(nullptr);\n    // 在这里实现你的解法。\n    return 0;\n}\n"
    });

    // The password hash (and nothing else sensitive) never leaves the server; leaderboard rows are
    // masked separately at the leaderboard endpoint.
    public static object? User(User? user) => user == null ? null : new
    {
        user.Id,
        user.StudentId,
        user.Name,
        user.Email,
        cfHandle = user.CodeforcesHandle,
        cfRating = user.CodeforcesRating,
        cfRank = user.CodeforcesRank,
        ratingUpdatedAt = user.RatingUpdatedAt,
        siteRating = user.SiteRating,
        user.Anonymous,
        user.CreatedAt
    };
}
public record CommunicationSample(List<string> Members,List<CommunicationSampleStep> Steps);
public record CommunicationSampleStep(string Id,string Kind,int From,int To,string Content);
