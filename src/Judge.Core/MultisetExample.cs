namespace Judge.Core;

// Contestants use plain text. JSON/Base64 is only the judge-side control channel.
public static class MultisetExample
{
    public static Problem Create() => new()
    {
        Title = "双泊对照 · 双进程交互", Mode = JudgeMode.Multiprocess, ProcessCount = 2,
        Published = true, Rating = 2100,
        Statement = """
            ## 题目描述
            两份货物清单各有 n 个整数。判断它们是否为同一个多重集：忽略顺序后，每个数值的出现次数相同。

            同一份程序**同时运行两个独立进程**。通过命令行参数 argv[1] 获取角色编号 0 或 1。
            两个进程只通过标准输入、标准输出与调度进程通信；每次输出后必须 flush。
            两个进程都要输出最终的 0 或 1，且答案必须相同并正确。

            ## 交互过程
            1. 调度器向角色 0 发送两行：n，以及 a 的 n 个整数；向角色 1 发送两行：n，以及 b 的 n 个整数。
            2. 角色 0 输出一行 n，下一行输出将 a 排成非降序的 n 个整数。调度器把这两行**原样转发**给角色 1。
            3. 角色 1 读取转发的序列，与自己的 b 比较：多重集相同输出 1，否则输出 0。调度器将这一行原样转发给角色 0。
            4. 角色 0 读取结果，再输出同一个整数。两个角色正常退出。

            不要等 EOF 才输出，也不要把完整测试文件当成每个角色的 stdin。角色 0 最初只能读到 a，角色 1 最初只能读到 b。

            ## 数据范围
            1 ≤ n ≤ 200000，0 ≤ aᵢ, bᵢ ≤ 10¹⁸。使用 64 位整数。

            ## 样例 1 · 按通信顺序
            | 步骤 | 发送方 → 接收方 | 内容（斜线表示换行） |
            |---|---|---|
            | 1 | 调度器 → 角色 0 | 3 / 1 2 2 |
            | 2 | 调度器 → 角色 1 | 3 / 2 1 2 |
            | 3 | 角色 0 → 调度器 → 角色 1 | 3 / 1 2 2 |
            | 4 | 角色 1 → 调度器 → 角色 0 | 1 |
            | 5 | 角色 0 → 调度器 | 1 |

            ## 建题用测试数据
            下方测试数据只供调度器读取：三行分别为 n、a、b。参考判定为 0 或 1。
            这不是任何单个角色完整的输入；调度器负责拆分数据和后续转发。
            CPU 与内存对每个角色分别限制，最终分别取两个角色的最大值。
            """,
        Protocol = "argv[1] 为角色 0/1。分别读取 n+a / n+b；角色 0 发出 n+排序后的 a → 角色 1 回答 0/1 → 角色 0 回答同值。每步 flush，双方最终答案都要检查。",
        Tests = [new("01", "3\n1 2 2\n2 1 2\n", "1\n"),
            new("02", "3\n1 1 2\n1 2 2\n", "0\n"),
            new("03", "3\n1000000000000000000 0 1000000000000000000\n0 1000000000000000000 1000000000000000000\n", "1\n")],
        ExampleSource = Solution,
        Manager = Manager,
        // Includes Base64 framing for two initial arrays plus the relayed array.
        JudgeLimits = new() { CpuMs = 10000, WallMs = 60000, MemoryMiB = 512, OutputKiB = 32768 }
    };

    public const string Solution = """
        #include <algorithm>
        #include <cstdlib>
        #include <iostream>
        #include <vector>
        int main(int argc, char** argv) {
            if (argc < 2) return 1;
            int role = std::atoi(argv[1]);
            std::ios::sync_with_stdio(false);
            std::cin.tie(nullptr);
            int n;
            if (!(std::cin >> n)) return 1;
            std::vector<unsigned long long> values(n);
            for (auto& x : values) if (!(std::cin >> x)) return 1;
            std::sort(values.begin(), values.end());
            if (role == 0) {
                std::cout << n << '\n';
                for (int i = 0; i < n; ++i) std::cout << values[i] << (i + 1 == n ? '\n' : ' ');
                std::cout << std::flush;
                int answer;
                if (!(std::cin >> answer)) return 1;
                std::cout << answer << '\n' << std::flush; // role 0 final
            } else {
                int m;
                if (!(std::cin >> m)) return 1;
                std::vector<unsigned long long> other(m);
                for (auto& x : other) if (!(std::cin >> x)) return 1;
                std::cout << (values == other ? 1 : 0) << '\n' << std::flush;
            }
        }
        """;

    public const string Manager = """
        #include "acm_manager.hpp"
        #include <algorithm>
        #include <fstream>
        #include <sstream>
        #include <vector>
        using Value = unsigned long long;
        bool integer(const std::string& text, long long& value) {
            std::istringstream in(text);
            if (!(in >> value)) return false;
            in >> std::ws;
            return in.eof();
        }
        std::string array_line(const std::vector<Value>& values) {
            std::ostringstream out;
            for (size_t i = 0; i < values.size(); ++i) out << values[i] << (i + 1 == values.size() ? '\n' : ' ');
            return out.str();
        }
        int main() {
            auto init = acm::receive();
            if (init.value("processCount", 0) != 2) { acm::verdict("Judge Error", "this problem requires two roles"); return 0; }
            std::ifstream in(init["inputPath"].get<std::string>());
            int n;
            if (!(in >> n) || n < 1 || n > 200000) { acm::verdict("Judge Error", "invalid input n"); return 0; }
            std::vector<Value> a(n), b(n);
            for (auto* values : {&a, &b}) for (auto& x : *values)
                if (!(in >> x) || x > 1000000000000000000ULL) { acm::verdict("Judge Error", "invalid input value"); return 0; }
            // Keep stdin open: each role must wait for messages from its peer.
            acm::send(0, std::to_string(n) + "\n" + array_line(a));
            acm::send(1, std::to_string(n) + "\n" + array_line(b));
            std::sort(a.begin(), a.end());
            std::sort(b.begin(), b.end());
            const int expected = a == b ? 1 : 0;
            std::string pending[2], header;
            size_t scanned[2] = {};
            bool exited[2] = {};
            // 0: role 0 header; 1: sorted array; 2: role 1 result; 3: role 0 result; 4: exits.
            int stage = 0;
            while (!exited[0] || !exited[1]) {
                auto event = acm::receive();
                const auto type = event.value("type", "");
                if (type == "eof") return 3;
                int id = event.at("id").get<int>();
                if (type == "output") {
                    pending[id] += acm::decode(event.at("data").get<std::string>());
                    for (;;) {
                        auto end = pending[id].find('\n', scanned[id]);
                        if (end == std::string::npos) { scanned[id] = pending[id].size(); break; }
                        std::string line = pending[id].substr(0, end + 1);
                        pending[id].erase(0, end + 1); scanned[id] = 0;
                        long long value;
                        if (id == 0 && stage == 0) {
                            if (!integer(line, value) || value != n) { acm::verdict("WA", "role 0: wrong n"); return 0; }
                            header = line; stage = 1;
                        } else if (id == 0 && stage == 1) {
                            std::istringstream values(line);
                            for (auto x : a) {
                                Value actual;
                                if (!(values >> actual) || actual != x) { acm::verdict("WA", "role 0: expected sorted original multiset"); return 0; }
                            }
                            values >> std::ws;
                            if (!values.eof()) { acm::verdict("WA", "role 0: extra array tokens"); return 0; }
                            // Forward original bytes, including whitespace and CRLF.
                            acm::send(1, header + line); stage = 2;
                        } else if (id == 1 && stage == 2) {
                            if (!integer(line, value) || value != expected) { acm::verdict("WA", "role 1: incorrect final answer"); return 0; }
                            acm::send(0, line); acm::close(1); stage = 3;
                        } else if (id == 0 && stage == 3) {
                            if (!integer(line, value) || value != expected) { acm::verdict("WA", "role 0: final answer must agree with role 1"); return 0; }
                            acm::close(0); stage = 4;
                        } else { acm::verdict("WA", "unexpected output or extra output"); return 0; }
                    }
                } else if (type == "exit") {
                    // The engine owns RE/TLE/MLE/OLE, rather than this protocol checker.
                    if (event.value("verdict", "") != "AC") return 0;
                    if (!pending[id].empty() || (id == 0 ? stage < 4 : stage < 3)) {
                        acm::verdict("WA", "role exited before completing its protocol"); return 0;
                    }
                    exited[id] = true;
                }
            }
            acm::verdict("AC", "both roles completed the relay and agreed on the correct answer");
        }
        """;
}
