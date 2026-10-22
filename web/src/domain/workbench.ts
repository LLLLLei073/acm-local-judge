import type { Case, Limits, Problem, ProblemSummary, Submission, TestData } from '../types';

export const isRunning = (status: string) => ['Queued', 'Compiling', 'Running'].includes(status);
export function solvedIds(history: Submission[]): Set<string> {
  return new Set(history.filter(s => s.status === 'AC').map(s => s.problemId));
}
export function latestByProblem(history: Submission[]) {
  const rows = new Map<string, Submission>();
  for (const row of history) if (!rows.has(row.problemId) || rows.get(row.problemId)!.createdAt < row.createdAt) rows.set(row.problemId, row);
  return rows;
}
export function trainingSummary(problems: ProblemSummary[], history: Submission[]) {
  const solved = solvedIds(history), available = new Set(problems.map(p => p.id));
  const attempted = new Set(history.filter(s => available.has(s.problemId)).map(s => s.problemId));
  return { solved: [...solved].filter(id => available.has(id)).length, attempted: [...attempted].filter(id => !solved.has(id)).length, running: history.filter(s => isRunning(s.status)).length };
}
export function resourcePercent(used: number, limit: number): number {
  return limit > 0 && Number.isFinite(used) ? Math.min(100, Math.max(0, used / limit * 100)) : 0;
}
export function verdictAdvice(status: string): string {
  return ({ AC: '全部测试点通过。可以继续挑战更高难度，或回顾实现的边界处理。', WA: '先检查首个失败点的判题消息、输入格式与边界条件；标准评测按 token 精确比较。', CE: '打开编译日志，先处理第一条编译错误；本平台使用 C++23。', RE: '查看退出码与 stderr，检查越界访问、栈溢出、断言和提前退出。', TLE: '区分 CPU 与墙钟用量：CPU 接近上限通常与复杂度有关；墙钟超限也可能是未 flush 或等待输入。', MLE: '检查容器规模、递归深度与分配；多轮、多进程的内存按实例最大值展示。', OLE: 'stdout 与 stderr 合计受到输出限额约束，检查循环输出和调试日志。', PE: '判题程序报告格式错误，请按题面约定检查输出结构。', 'Judge Error': '评测基础设施或判题程序失败，需要管理员检查工具日志后重新评测。', Cancelled: '本次评测已取消，可修改代码后重新提交。', Interrupted: '服务中断使评测结束，可重新提交；原始记录已保留。' } as Record<string, string>)[status] ?? '编译和测试点结果会持续更新，可以继续阅读题面。';
}
export interface ReadinessCheck { id: string; label: string; ok: boolean; detail: string }
export function readiness(p: Problem): ReadinessCheck[] {
  const validLimits = (l: Limits) => [l.cpuMs, l.wallMs, l.memoryMiB, l.outputKiB].every(Number.isInteger) && l.cpuMs>=50 && l.cpuMs<=300000 && l.wallMs>=l.cpuMs && l.wallMs<=600000 && l.memoryMiB>=8 && l.memoryMiB<=8192 && l.outputKiB>=1 && l.outputKiB<=65536;
  const names = p.tests.map(t => t.name.trim().toLowerCase());
  const required = p.mode === 'Special' ? ['checker'] : p.mode === 'Interactive' ? ['interactor'] : p.mode === 'Multiprocess' && !p.flow?.enabled || p.mode === 'Communication' && p.managedCommunication ? ['manager'] : [];
  return [
    { id: 'statement', label: '题面', ok: !!p.title.trim() && p.statement.trim().length > 20, detail: '标题、输入输出格式与数据范围' },
    { id: 'rating', label: '难度', ok: Number.isInteger(p.rating) && p.rating >= 100 && p.rating <= 3500, detail: '100–3500，发布与加入比赛必需' },
    { id: 'tests', label: '测试数据', ok: names.length > 0 && names.every(Boolean) && new Set(names).size === names.length, detail: '至少一个测试点，名称唯一；空答案可以合法' },
    { id: 'limits', label: '资源限额', ok: validLimits(p.limits) && validLimits(p.judgeLimits), detail: '选手与判题工具限额均为正整数' },
    { id: 'tools', label: '判题协议', ok: required.every(key => !!p[key as 'checker' | 'interactor' | 'manager'].trim()) && (p.mode !== 'Multiprocess' || !p.flow?.enabled || p.flow.steps.some(s => s.kind === 'receive' && s.check)), detail: required.length ? `需要 ${required.join('、')}.cpp` : p.flow?.enabled ? '流程至少包含一个接收校验' : '引擎按该类型的输入输出约定判定' },
    { id: 'solution', label: '标准答案', ok: !!p.exampleSource.trim(), detail: '用于实际验题；有源码不代表已经验证通过' },
  ];
}

/** Parse the whole upload before modifying any existing tests. Empty answer files are valid. */
export function pairTestFiles(files: { name: string; text: string }[], mode: string, existing: TestData[] = []) {
  const map = new Map<string, { name: string; text: string }>();
  for (const f of files) {
    if (!/\.(in|ans)$/i.test(f.name)) throw new Error(`不支持的文件：${f.name}`);
    const key = f.name.toLowerCase();
    if (map.has(key)) throw new Error(`文件重名：${f.name}`);
    map.set(key, f);
  }
  const tests = existing.map(t => ({ ...t }));
  let added = 0, replaced = 0;
  for (const [key, f] of map) {
    if (key.endsWith('.ans') && !map.has(key.slice(0, -4) + '.in')) throw new Error(`答案缺少输入：${f.name}`);
    if (!key.endsWith('.in')) continue;
    const name = f.name.slice(0, -3), answer = map.get(key.slice(0, -3) + '.ans');
    if (!answer && ['Standard', 'Special', 'Communication'].includes(mode)) throw new Error(`缺少 ${name}.ans`);
    const row = { name, input: f.text, answer: answer?.text ?? '' }, index = tests.findIndex(t => t.name.toLowerCase() === name.toLowerCase());
    if (index < 0) { tests.push(row); added++; } else { tests[index] = row; replaced++; }
  }
  return { tests, added, replaced };
}

export function decodeTrace(value: string): string {
  try { return new TextDecoder().decode(Uint8Array.from(atob(value), c => c.charCodeAt(0))); }
  catch { return '（无法解码的消息）'; }
}
export function filterTrace(trace: Case['trace'], participant: string, query: string) {
  const term = query.trim().toLowerCase();
  return trace.filter(t => (!participant || t.from === participant || t.to === participant) && (!term || `${t.from} ${t.to} ${decodeTrace(t.dataBase64)}`.toLowerCase().includes(term)));
}
export const draftKey = (userId: string, problemId: string) => `acm:draft:v2:${encodeURIComponent(userId)}:${encodeURIComponent(problemId)}`;
export function saveLocal(storage: Pick<Storage, 'setItem'>, key: string, value: string): boolean {
  try { storage.setItem(key, value); return true; } catch { return false; }
}

/** Search only text the board actually displays, including the explicit self marker. */
export function filterLeaderboardRows<T extends { userId: string; anonymous: boolean; name?: string; studentId?: string; cfHandle?: string | null }>(rows: T[], query: string, currentUserId?: string): T[] {
  const term = query.trim().toLowerCase();
  return term ? rows.filter(row => {
    const identity = row.anonymous ? '已匿名' : [row.name, row.studentId, row.cfHandle].filter(Boolean).join(' ');
    return `${identity}${row.userId === currentUserId ? ' 我' : ''}`.toLowerCase().includes(term);
  }) : rows;
}
