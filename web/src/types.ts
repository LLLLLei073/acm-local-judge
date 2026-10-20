export type Mode = 'Standard' | 'Special' | 'Interactive' | 'Communication' | 'Multiprocess';
export interface Limits { cpuMs: number; wallMs: number; memoryMiB: number; outputKiB: number }
export interface TestData { name: string; input: string; answer: string }
export interface FlowData { source: 'literal' | 'input' | 'answer' | 'message'; text: string; stepId: string; startLine: number; lineCount: number }
export interface FlowStep { id: string; kind: 'send' | 'receive' | 'close'; from: number; to: number; lines: number; data: FlowData; additionalData: FlowData[]; check: boolean; expected: FlowData; sample: string }
export interface CommunicationFlow { version: number; enabled: boolean; members: string[]; steps: FlowStep[] }
export interface PublicCommunicationSample { members: string[]; steps: { id: string; kind: FlowStep['kind']; from: number; to: number; content: string }[] }
export interface Problem { id: string; title: string; statement: string; mode: Mode; limits: Limits; judgeLimits: Limits; checker: string; interactor: string; manager: string; managedCommunication: boolean; firstMarker: string; secondMarker: string; processCount: number; protocol: string; revealExpected: boolean; tests: TestData[]; exampleSource: string; flow?: CommunicationFlow | null; communicationSample?: PublicCommunicationSample | null; contestTitle?: string; published: boolean; rating: number }
export interface ProblemSummary { id: string; title: string; mode: Mode; limits: Limits; testCount: number; contestTitle: string; published: boolean; rating: number; solvedBy?: number }
export interface Submission { id: string; problemId: string; title: string; mode: Mode; status: string; createdAt: string; userId?: string | null; userName?: string }
export interface SubmissionContext { title: string; mode: Mode; limits: Limits; testCount: number }
export interface TrainingData { solvedProblemIds: string[]; attemptedProblemIds: string[]; submissionCount: number; acceptedSubmissionCount: number; runningCount: number; recent: Submission[]; activity: { date: string; submissions: number; accepted: number }[] }
export interface UserInfo { id: string; studentId: string; name: string; email: string; cfHandle: string; cfRating: number | null; cfRank: string; ratingUpdatedAt: string | null; siteRating: number | null; anonymous: boolean; createdAt: string }
export type ContestPhase = 'open' | 'upcoming' | 'running' | 'finished';
export interface ContestInfo { id: string; title: string; count: number; problemIds: string[]; startsAt: string | null; endsAt: string | null; rated: boolean; settled: boolean; phase: ContestPhase }
export interface StandingsProblem { problemId: string; letter: string; title: string; rating: number }
export interface StandingsCell { attempts: number; accepted: boolean; acMinutes: number; pending: boolean }
export interface StandingsRow { rank: number; userId: string; name: string; studentId: string; anonymous: boolean; solved: number; penalty: number; lastAcAt: string | null; cells: StandingsCell[]; delta?: number | null }
export interface StandingsData { contestId: string; title: string; phase: ContestPhase; rated: boolean; settled: boolean; startsAt: string | null; endsAt: string | null; problems: StandingsProblem[]; rows: StandingsRow[] }
export interface LeaderboardEntry { rank: number; userId: string; name: string; studentId: string; anonymous: boolean; cfHandle: string; cfRating: number | null; cfRank: string; ratingUpdatedAt: string | null; solvedCount: number; submissionCount: number; lastAcAt: string | null }
export interface SiteLeaderboardEntry { rank: number; userId: string; siteRating: number; contestsPlayed: number; name: string; studentId: string; anonymous: boolean }
export const phaseLabel = (phase: ContestPhase) => ({ open: '长期开放', upcoming: '未开始', running: '进行中', finished: '已结束' }[phase]);
export const cfRankColor = (rank: string): string => ({ newbie: '#808080', pupil: '#008000', specialist: '#03a89d', expert: '#0000ff', 'candidate master': '#aa00aa', master: '#ff8c00', 'international master': '#ff8c00', grandmaster: '#ff0000', 'international grandmaster': '#ff0000', 'legendary grandmaster': '#ff0000' } as Record<string, string>)[rank] ?? '#829087';
export interface Run { verdict: string; message: string; exitCode: number; usage: { cpuMs: number; wallMs: number; memoryBytes: number }; stdout: string; stderr: string }
export interface Case { name: string; verdict: string; message: string; cpuMs: number; memoryBytes: number; instances: { id: number; round: number; run: Run }[]; trace: { ms: number; from: string; to: string; dataBase64: string }[] }
export interface Result { status: string; message: string; compilerVersion: string; compileArgs: string; compileLog: string; cases: Case[] }
export const modes: { value: Mode; label: string; short: string; color: string; description: string }[] = [
  { value: 'Standard', label: '标准评测', short: 'STD', color: '#3267cd', description: '按空白拆分 token，逐个精确比较。不提供浮点容差。' },
  { value: 'Special', label: '特判评测', short: 'SPJ', color: '#b76219', description: '由 checker 判断答案，支持浮点容差、多解和格式检查。' },
  { value: 'Interactive', label: '交互评测', short: 'INT', color: '#17836d', description: '与 interactor 实时问答。每次输出后 flush，不要等待 EOF。' },
  { value: 'Communication', label: '通信评测', short: 'COM', color: '#8556ba', description: '同一程序依次启动两次。新进程、新目录，两轮资源取最大值。' },
  { value: 'Multiprocess', label: '多进程评测', short: 'MUL', color: '#bb4878', description: '同一程序同时运行多个角色，argv[1] 为角色编号。调度器分发各自输入、实时转发消息并判定；每步输出需 flush。分别限额，资源取最大值。' },
];
export const modeInfo = (m: Mode) => modes.find(x => x.value === m)!;
export const active = (status: string) => ['Queued', 'Compiling', 'Running'].includes(status);
export const statusLabel = (s: string) => ({ Queued: '等待评测', Compiling: '正在编译', Running: '正在运行', AC: '全部通过', WA: '答案错误', CE: '编译错误', RE: '运行错误', TLE: '时间超限', MLE: '内存超限', OLE: '输出超限', PE: '格式错误', 'Judge Error': '判题程序错误', Cancelled: '已取消', Interrupted: '已中断' }[s] ?? s);
export const memory = (bytes: number) => (bytes / 1048576).toFixed(1) + ' MiB';
// CF 配色分档的难度 rating；0 = 未评级返回灰色。
export const ratingColor = (rating: number): string => rating < 100 ? '#a2ada2' : rating < 1200 ? '#808080' : rating < 1400 ? '#008000' : rating < 1600 ? '#03a89d' : rating < 1900 ? '#0000ff' : rating < 2100 ? '#aa00aa' : rating < 2400 ? '#ff8c00' : '#ff0000';
export const blankProblem = (): Problem => ({ id: '', title: '', statement: '## 题目描述\n\n## 输入格式\n\n## 输出格式\n', mode: 'Standard', limits: { cpuMs: 2000, wallMs: 10000, memoryMiB: 256, outputKiB: 16384 }, judgeLimits: { cpuMs: 10000, wallMs: 60000, memoryMiB: 512, outputKiB: 16384 }, checker: '', interactor: '', manager: '', managedCommunication: false, firstMarker: 'first', secondMarker: 'second', processCount: 2, protocol: '', revealExpected: false, tests: [], exampleSource: '', published: false, rating: 0 });
