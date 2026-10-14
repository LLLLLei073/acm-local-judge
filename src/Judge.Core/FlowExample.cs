namespace Judge.Core;

public static class FlowExample
{
    public static Problem Create() => new()
    {
        Title = "双向接力 · 可视化通信流程",
        Mode = JudgeMode.Multiprocess,
        Statement = "## 双向接力\n角色 0 读取 a，角色 1 读取 b。角色 0 将 a 发送给角色 1；角色 1 计算 a+b 并发回；角色 0 输出收到的和。\n\n每条消息以换行结尾并 flush。通过 argv[1] 区分角色。\n\n测试文件两行分别是 a 和 b，参考答案是 a+b。可在「编辑题目 → 成员与通信」调整此题全部步骤。",
        Tests = [new("01", "3\n4\n", "7\n"), new("02", "10\n-2\n", "8\n")],
        ExampleSource = Source,
        WrongSource = Source.Replace("a + b", "a - b"),
        Flow = new()
        {
            Enabled = true, Members = ["角色 0", "角色 1"], Steps =
            [
                new() { Id = "give-a", To = 0, Data = new() { Source = "input", StartLine = 1, LineCount = 1 } },
                new() { Id = "give-b", To = 1, Data = new() { Source = "input", StartLine = 2, LineCount = 1 } },
                new() { Id = "relay-a", Kind = "receive", From = 0, To = 1, Check = true, Expected = new() { Source = "input", StartLine = 1, LineCount = 1 }, Sample = "3\n" },
                new() { Id = "relay-sum", Kind = "receive", From = 1, To = 0, Check = true, Expected = new() { Source = "answer" }, Sample = "7\n" },
                new() { Id = "final", Kind = "receive", From = 0, To = -1, Check = true, Expected = new() { Source = "message", StepId = "relay-sum" }, Sample = "7\n" }
            ]
        }
    };
    public const string Source = """
        #include <iostream>
        #include <cstdlib>
        int main(int argc, char** argv) {
            if (argc < 2) return 1;
            std::ios::sync_with_stdio(false);
            std::cin.tie(nullptr);
            int role = std::atoi(argv[1]);
            long long a, b;
            if (role == 0) {
                if (!(std::cin >> a)) return 1;
                std::cout << a << std::endl;
                if (!(std::cin >> b)) return 1;
                std::cout << b << std::endl;
            } else {
                if (!(std::cin >> b >> a)) return 1;
                std::cout << a + b << std::endl;
            }
        }
        """;
}
