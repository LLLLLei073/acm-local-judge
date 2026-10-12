namespace Judge.Core;

public static class Examples
{
    public const string Sum = "#include <iostream>\nint main(){long long a,b;std::cin>>a>>b;std::cout<<a+b<<'\\n';}\n";
    public const string Wrong = "#include <iostream>\nint main(){std::cout<<0<<'\\n';}\n";
    public static List<Problem> All() =>
    [
        new() { Title = "A + B · 从这里开始", Mode = JudgeMode.Standard, Statement = "## 题目描述\n读入两个整数，输出它们的和。\n\n### 输入\n一行两个整数 a、b。\n\n### 输出\n输出 a + b。\n\n```text\n输入：2 3\n输出：5\n```", Tests = [new("01", "2 3\n", "5\n"), new("02", "-7 10\n", "3\n")], ExampleSource = Sum, WrongSource = Wrong },
        new() { Title = "平方根 · 浮点特判", Mode = JudgeMode.Special, Statement = "## 浮点误差\n输入非负整数 n，输出它的平方根。绝对或相对误差不超过 1e-6 即可。", Tests = [new("01", "2\n", "1.4142135623730951\n")], ExampleSource = "#include <iostream>\n#include <cmath>\n#include <iomanip>\nint main(){double n;std::cin>>n;std::cout<<std::setprecision(12)<<sqrt(n);}", WrongSource = Wrong,
          Checker = "#include \"testlib.h\"\n#include <cmath>\nint main(int argc,char**argv){registerTestlibCmd(argc,argv);double expected=ans.readDouble();double actual=ouf.readDouble();if(!ouf.seekEof())quitf(_pe,\"extra output\");if(std::abs(actual-expected)<=1e-6*std::max(1.0,std::abs(expected)))quitf(_ok,\"within tolerance\");quitf(_wa,\"outside tolerance\");}" },
        new() { Title = "即时加倍 · 交互握手", Mode = JudgeMode.Interactive, Statement = "## 交互协议\n评测器依次给出 3 个整数。每读入一个整数 x，立即输出 2x 并 flush。\n\n不要等待 EOF 再输出。", Protocol = "三次问答：读入 x → 输出 2*x → flush。",
          Tests = [new("01", "3 7 11\n", "")],
          ExampleSource = "#include <iostream>\nint main(){for(int i=0;i<3;i++){int x;std::cin>>x;std::cout<<2*x<<std::endl;}}",
          WrongSource = "#include <iostream>\nint main(){std::cin.tie(nullptr);for(int i=0;i<3;i++){int x;std::cin>>x;std::cout<<2*x<<'\\n';}}",
          Interactor = "#include \"testlib.h\"\n#include <iostream>\nint main(int argc,char**argv){registerInteraction(argc,argv);for(int i=0;i<3;i++){int x=inf.readInt();std::cout<<x<<std::endl;int y=ouf.readInt();if(y!=2*x)quitf(_wa,\"expected double\");}quitf(_ok,\"three rounds accepted\");}" },
        new() { Title = "传递整数 · 双轮通信", Mode = JudgeMode.Communication, Statement = "## 两次独立启动\n第一轮读入 `first` 与一个整数 x，输出 x+1。第二轮读入 `second` 与评测器转发的中间值，输出中间值的两倍。\n\n两轮变量和工作目录不会保留。", Protocol = "first：x → x+1；second：y → 2*y。",
          Tests = [new("01", "6\n", "14\n")], ExampleSource = "#include <iostream>\n#include <string>\nint main(){std::string phase;long long x;std::cin>>phase>>x;if(phase==\"first\")std::cout<<x+1<<std::endl;else std::cout<<x*2<<std::endl;}", WrongSource = Wrong },
        new() { Title = "并行求和 · 多进程协作", Mode = JudgeMode.Multiprocess, Statement = "## 两个选手进程\n每个进程读入一个整数，输出它的平方并 flush，然后退出。管理器把两个结果相加并检查。\n\n启动参数 `argv[1]` 是进程编号，`argv[2]` 是进程总数。资源按各进程最大值汇总。", Protocol = "manager 向 0/1 号进程分别发送 a/b，接收平方并求和。", Tests = [new("01", "3 4\n", "25\n")], ExampleSource = "#include <iostream>\nint main(int argc,char**argv){long long x;std::cin>>x;std::cout<<x*x<<std::endl;}", WrongSource = Wrong,
          Manager = "#include \"acm_manager.hpp\"\n#include <fstream>\n#include <sstream>\nint main(){auto init=acm::receive();std::ifstream in(init[\"inputPath\"].get<std::string>()),ans(init[\"answerPath\"].get<std::string>());long long a,b,expected;in>>a>>b;ans>>expected;acm::send(0,std::to_string(a)+\"\\n\");acm::send(1,std::to_string(b)+\"\\n\");acm::close(0);acm::close(1);std::string out[2];int exited=0;while(exited<2){auto e=acm::receive();if(e[\"type\"]==\"eof\")return 3;if(e[\"type\"]==\"output\")out[e[\"id\"].get<int>()]+=acm::decode(e[\"data\"]);if(e[\"type\"]==\"exit\")exited++;}long long x=0,y=0;std::istringstream(out[0])>>x;std::istringstream(out[1])>>y;acm::verdict(x+y==expected?\"AC\":\"WA\",\"sum of squares\");}" }
    ];
}
