<script setup lang="ts">
import { computed, defineAsyncComponent, onMounted, onBeforeUnmount, ref, watch } from 'vue';
import { Search, ChevronRight, ChevronLeft, Code2, BookOpen, History, Check, X, Terminal, ArrowUpRight, RefreshCw, Play, Square, Upload, Layers, AlertCircle, ChevronDown, Trophy, Filter, Medal, Settings, LogOut, Clock, Copy, BarChart3 } from 'lucide-vue-next';
import { marked } from 'marked';
import DOMPurify from 'dompurify';
import { api, currentUser, needsAuth, loadMe, registerUser, userLogin, userLogout } from './client';
import { modes, modeInfo, statusLabel, active, blankProblem, cfRankColor, ratingColor, phaseLabel, type Problem, type ProblemSummary, type Submission, type Result, type UserInfo, type LeaderboardEntry, type SiteLeaderboardEntry, type ContestInfo, type StandingsData, type StandingsCell } from './types';
import JudgeResult from './components/JudgeResult.vue';
import CaseInspector from './components/CaseInspector.vue';
import PublicFlowPreview from './components/PublicFlowPreview.vue';
import TrainingOverview from './components/TrainingOverview.vue';
import EnvironmentStatus from './components/EnvironmentStatus.vue';
import { draftKey, saveLocal } from './domain/workbench';
import type { TrainingData } from './types';
import type { SubmissionContext } from './types';
import { useSubmissionFeed } from './composables/useSubmissionFeed';
import { useModalAccessibility } from './composables/useModalAccessibility';
const CodeEditor = defineAsyncComponent(() => import('./CodeEditor.vue'));

// 选手 OJ 端：题库（CF 式紧凑表格）/ 比赛 / 排行榜 / 提交 / 结果。全站需注册登录；管理功能在独立的 /admin。
const problems = ref<ProblemSummary[]>([]), selected = ref<Problem>(), search = ref('');
const filter = ref('all'), contestFilter = ref('all'), statusFilter = ref('all');
const source = ref('// C++23\n#include <bits/stdc++.h>\nusing namespace std;\n\nint main() {\n    ios::sync_with_stdio(false);\n    cin.tie(nullptr);\n\n    return 0;\n}\n');
const tab = ref('statement'), history = ref<Submission[]>([]), submission = ref<Submission>(), result = ref<Result>();
const submissionContext=ref<SubmissionContext>();
const busy = ref(false), error = ref(''), notice = ref(''), expanded = ref<string>();
const snapshotSource = ref(''), showSnapshot = ref(false), canViewSource = ref(false);
const training = ref<TrainingData>();
const sortBy = ref('default'), contestPhase = ref('all'), leaderboardSearch = ref('');
const draftSaved = ref('尚未编辑');
const nav = ref<'set' | 'contests' | 'history' | 'leaderboard'>('set');
const contests = ref<ContestInfo[]>([]);
const standings = ref<StandingsData>();
// 每秒走动的时钟，给比赛卡片提供「距开赛 / 剩余时间」倒计时。
const nowMs = ref(Date.now());
const copiedSample = ref('');
const environment = ref<{ os: string; memoryMetric: string; workerReady: boolean; parallelism: number; judging: number; queued: number }>();
const { connect, close: closeFeed, connection } = useSubmissionFeed(submission, result, () => { loadHistory().catch(() => {}); loadMyHistory().catch(() => {}); loadTraining().catch(() => {}); }, submissionContext);
const info = computed(() => selected.value ? modeInfo(selected.value.mode) : modes[0]);
const rendered = computed(() => DOMPurify.sanitize(marked.parse(selected.value?.statement || '') as string));
const running = computed(() => submission.value && active(submission.value.status));
const latest = computed(() => selected.value ? lastOf.value.get(selected.value.id) : undefined);
const currentCase = computed(() => result.value?.cases.find(c => c.name === expanded.value));
const platformTag = computed(() => environment.value ? environment.value.os.split(' ')[0] : '在线评测');
// 题库状态列只看「我的」最近提交（CF 语义）；未登录的管理员令牌访问则不显示状态。
const myHistory = ref<Submission[]>([]);
async function loadTraining() { training.value = await api('/training'); }
async function loadMyHistory() { if (me.value) myHistory.value = await api('/submissions?mine=1'); else myHistory.value = []; }
const lastOf = computed(() => { const m = new Map<string, Submission>(); for (const s of myHistory.value) if (!m.has(s.problemId)) m.set(s.problemId, s); return m; });
const statusOf = (p: ProblemSummary): string => training.value?.solvedProblemIds.includes(p.id) ? 'AC' : lastOf.value.get(p.id)?.status ?? (training.value?.attemptedProblemIds.includes(p.id) ? 'WA' : '');
const statusMatch = (p: ProblemSummary) => statusFilter.value === 'all' || (statusFilter.value === 'solved' ? statusOf(p) === 'AC' : statusFilter.value === 'attempted' ? !!statusOf(p) && statusOf(p)!=='AC' : statusOf(p)!=='AC');
// CF 配色分档的难度区间筛选；bands 与 ratingColor 的阈值保持一致。
const ratingBands = [
  { value: 'all', label: '全部难度' },
  { value: '0', label: '未评级' },
  { value: '1-1199', label: '入门 · <1200' },
  { value: '1200-1599', label: '简单 · 1200–1599' },
  { value: '1600-1899', label: '基础 · 1600–1899' },
  { value: '1900-2399', label: '进阶 · 1900–2399' },
  { value: '2400-3500', label: '困难 · 2400+' },
];
const ratingBand = ref('all');
const bandMatch = (p: ProblemSummary) => {
  if (ratingBand.value === 'all') return true;
  if (ratingBand.value === '0') return !p.rating;
  const [min, max] = ratingBand.value.split('-').map(Number);
  return p.rating >= min && p.rating <= max;
};
const setRows = computed(() => problems.value.filter(p =>
  (p.title+' '+p.id).toLowerCase().includes(search.value.trim().toLowerCase()) &&
  (filter.value === 'all' || p.mode === filter.value) &&
  (contestFilter.value === 'all' || contestFilter.value === 'none' && !p.contestTitle || p.contestTitle === contestFilter.value) &&
  statusMatch(p) && bandMatch(p)).sort((a,b) => sortBy.value==='rating-asc' ? a.rating-b.rating : sortBy.value==='rating-desc' ? b.rating-a.rating : sortBy.value==='popular' ? (b.solvedBy??0)-(a.solvedBy??0) : 0));
const shownContests = computed(() => contests.value.filter(c => contestPhase.value==='all'||c.phase===contestPhase.value));
const ungrouped = computed(() => problems.value.filter(p => !p.contestTitle).length);
const me = computed(() => currentUser.value);
const showAuth = computed(() => !me.value || needsAuth.value);
// —— 登录 / 注册 ——
const authTab = ref<'login' | 'register'>('login');
const authForm = ref({ studentId: '', name: '', email: '', password: '', confirm: '' });
const authError = ref(''), authBusy = ref(false);
// —— 排行榜 ——
const leaderboard = ref<{ cf: LeaderboardEntry[]; solved: LeaderboardEntry[]; site: SiteLeaderboardEntry[] }>();
const lbTab = ref<'cf' | 'solved' | 'site'>('cf');
// 三个榜的行形状取并集（site 榜缺 CF/解题字段，其余缺 rating 字段），模板按当前 tab 访问。
type LbRow = LeaderboardEntry & Partial<SiteLeaderboardEntry>;
const lbRows = computed<LbRow[]>(() => (leaderboard.value ? (lbTab.value === 'cf' ? leaderboard.value.cf : lbTab.value === 'solved' ? leaderboard.value.solved : leaderboard.value.site) as LbRow[] : []).filter(row => !leaderboardSearch.value || row.userId===me.value?.id || !row.anonymous && (row.name+' '+row.studentId+' '+row.cfHandle).toLowerCase().includes(leaderboardSearch.value.toLowerCase())));
// —— 个人设置 ——
const showProfile = ref(false), menuOpen = ref(false), profileSection=ref('account');
const profileForm = ref({ name: '', email: '', anonymous: false });
const passwordForm = ref({ current: '', next: '', confirm: '' });
const cfHandleInput = ref(''), cfChallenge = ref(''), cfChallengeCode = ref(''), cfMinutes = ref(15);
let pendingOperations=0;
async function perform<T>(fn: () => Promise<T>):Promise<T|undefined> { error.value=''; pendingOperations++; busy.value=true; try { return await fn(); } catch(e) { error.value=e instanceof Error?e.message:String(e); } finally { busy.value=--pendingOperations>0; } }
async function loadList() { problems.value = await api('/problems'); }
// 提交记录的 CF 式筛选：范围（全部/我的）× 判定 × 题目，由服务端完成过滤。
const historyScope = ref<'all' | 'mine'>('all'), historyStatus = ref('all'), historyProblem = ref('all');
const statusOptions = ['AC', 'WA', 'TLE', 'MLE', 'RE', 'CE', 'OLE', 'PE', 'Judge Error', 'Cancelled', 'Interrupted'];
async function loadHistory() {
  const params = new URLSearchParams();
  if (historyScope.value === 'mine') params.set('mine', '1');
  if (historyStatus.value !== 'all') params.set('status', historyStatus.value);
  if (historyProblem.value !== 'all') params.set('problemId', historyProblem.value);
  history.value = await api('/submissions' + (params.size ? '?' + params.toString() : ''));
}
async function loadContests() { contests.value = await api('/contests'); }
async function loadLeaderboard() { leaderboard.value = await api('/leaderboard'); }
// —— 比赛排行榜 ——
async function openStandings(id: string) {
  await perform(async () => { standings.value = await api<StandingsData>('/contests/' + id + '/standings'); });
}
function closeStandings() { standings.value = undefined; }
const pad2 = (n: number) => String(n).padStart(2, '0');
const formatDuration = (ms: number) => { const s = Math.max(0, Math.floor(ms / 1000)); return `${pad2(Math.floor(s / 3600))}:${pad2(Math.floor(s % 3600 / 60))}:${pad2(s % 60)}`; };
const untilText = (iso: string) => { const ms = new Date(iso).getTime() - nowMs.value; return ms <= 0 ? '即将开始' : formatDuration(ms); };
const fmtTime = (iso: string) => new Date(iso).toLocaleString('zh-CN', { month: 'numeric', day: 'numeric', hour: '2-digit', minute: '2-digit' });
const cellText = (c: StandingsCell) => c.pending && !c.accepted ? '…' : c.accepted ? (c.attempts > 1 ? '+' + c.attempts : '+') : c.attempts > 0 ? '-' + c.attempts : '';
async function copySample(text: string, key: string) {
  try { await navigator.clipboard.writeText(text); copiedSample.value = key; setTimeout(() => copiedSample.value = '', 1200); }
  catch { error.value = '复制失败：浏览器未授权剪贴板访问'; }
}
async function boot() {
  try { await Promise.all([loadList(), loadHistory(), loadContests(), loadLeaderboard(), loadMyHistory(), loadTraining(), api<typeof environment.value>('/environment').then(x => environment.value = x)]); } catch { /* 401 已由 api() 切到登录界面 */ }
}
async function submitAuth() {
  authError.value = '';
  if (authTab.value === 'register' && authForm.value.password !== authForm.value.confirm) { authError.value = '两次输入的密码不一致'; return; }
  authBusy.value = true;
  try {
    if (authTab.value === 'login') await userLogin(authForm.value.studentId.trim(), authForm.value.password);
    else await registerUser({ studentId: authForm.value.studentId.trim(), name: authForm.value.name.trim(), email: authForm.value.email.trim(), password: authForm.value.password });
    notice.value = '登录成功，评测机已就绪';
    await boot();
  } catch (e) { authError.value = e instanceof Error ? e.message : String(e); } finally { authBusy.value = false; }
}
async function doLogout() {
  menuOpen.value = false;
  try { await userLogout(); } catch(e) { error.value=e instanceof Error?e.message:String(e); return; }
  nav.value = 'set'; selected.value = undefined; closeFeed();
  leaderboard.value = undefined; myHistory.value = []; training.value = undefined; history.value = []; source.value = ''; notice.value = ''; error.value = '';
}
function openProfile() {
  if (!me.value) return;
  error.value = '';
  profileForm.value = { name: me.value.name, email: me.value.email, anonymous: me.value.anonymous };
  passwordForm.value = { current: '', next: '', confirm: '' };
  cfHandleInput.value = ''; resetChallenge();
  profileSection.value='account'; showProfile.value = true;
}
async function saveProfile() {
  await perform(async () => {
    const data = await api<{ user: UserInfo }>('/users/me', { method: 'PUT', body: JSON.stringify(profileForm.value) });
    currentUser.value = data.user; showProfile.value = false; notice.value = '个人资料已保存';
  });
}
async function savePassword() {
  if (passwordForm.value.next !== passwordForm.value.confirm) { error.value = '两次输入的新密码不一致'; return; }
  await perform(async () => {
    await api('/users/me/password', { method: 'POST', body: JSON.stringify({ currentPassword: passwordForm.value.current, newPassword: passwordForm.value.next }) });
    passwordForm.value = { current: '', next: '', confirm: '' }; notice.value = '密码已修改'; showProfile.value = false;
  });
}
function resetChallenge() { cfChallenge.value = ''; cfChallengeCode.value = ''; }
async function issueChallenge() {
  await perform(async () => {
    const data = await api<{ code: string; minutes: number }>('/users/me/codeforces', { method: 'POST', body: JSON.stringify({ handle: cfHandleInput.value.trim() }) });
    cfChallenge.value = cfHandleInput.value.trim(); cfChallengeCode.value = data.code; cfMinutes.value = data.minutes;
  });
}
async function verifyChallenge() {
  await perform(async () => {
    const data = await api<{ user: UserInfo }>('/users/me/codeforces/verify', { method: 'POST', body: JSON.stringify({ code: cfChallengeCode.value }) });
    currentUser.value = data.user; resetChallenge(); showProfile.value = false;
    notice.value = 'Codeforces 绑定成功'; loadLeaderboard().catch(() => {});
  });
}
async function unbindCf() {
  await perform(async () => {
    const data = await api<{ user: UserInfo }>('/users/me/codeforces', { method: 'DELETE' });
    currentUser.value = data.user; showProfile.value = false;
    notice.value = '已解除 Codeforces 绑定'; loadLeaderboard().catch(() => {});
  });
}
function openContest(title: string) { contestFilter.value = title; nav.value = 'set'; standings.value = undefined; }
// 题库导航与「返回题库」共用：无论当前在哪个页（含排行榜/比赛/提交记录）都回到题库视图。
function back() { selected.value = undefined; closeFeed(); nav.value = 'set'; standings.value = undefined; }
async function select(id: string) {
  await perform(async () => {
    const p = await api<Problem>('/problems/' + id); selected.value = p;
    restoreSource(p);
    result.value = undefined; submission.value = undefined; submissionContext.value=undefined; expanded.value = undefined; closeFeed(); tab.value = 'statement';
  });
}
function restoreSource(p: Problem) {
  let saved: { source?: string; updatedAt?: string } = {};
  try { saved = JSON.parse(localStorage.getItem(draftKey(me.value?.id ?? 'token',p.id)) || '{}'); } catch { /* A damaged local draft must not prevent opening the problem. */ }
  source.value = typeof saved.source==='string' ? saved.source : p.exampleSource || '// C++23\n#include <bits/stdc++.h>\nusing namespace std;\nint main() {\n    return 0;\n}\n';
  draftSaved.value = saved.updatedAt ? '已恢复本账号草稿 · '+new Date(saved.updatedAt).toLocaleTimeString('zh-CN') : '新草稿';
}
watch(source, value => {
  if (!selected.value || !me.value) return;
  const ok = saveLocal(localStorage,draftKey(me.value.id,selected.value.id),JSON.stringify({source:value,updatedAt:new Date().toISOString()}));
  draftSaved.value = ok ? '草稿已保存 · '+new Date().toLocaleTimeString('zh-CN') : '本机存储已满，请下载代码备份';
});
watch([selected, source], () => { notice.value = ''; });
function protect(e: BeforeUnloadEvent) { if (running.value && canViewSource.value) { e.preventDefault(); e.returnValue = ''; } }
async function submit() {
  if (!selected.value || busy.value || running.value && canViewSource.value) return;
  await perform(async () => {
    submission.value = await api<Submission>('/submissions', { method: 'POST', body: JSON.stringify({ problemId: selected.value!.id, source: source.value }) });
    submissionContext.value={title:selected.value!.title,mode:selected.value!.mode,limits:{...selected.value!.limits},testCount:selected.value!.tests.length};
    snapshotSource.value = source.value; canViewSource.value = true;
    result.value = { status: 'Queued', message: '', compilerVersion: '', compileArgs: '', compileLog: '', cases: [] }; expanded.value = undefined;
    connect(submission.value.id); await loadHistory();
  });
}
async function openSubmission(id: string) {
  await perform(async () => {
    const data = await api<{submission: Submission; result: Result; context?: SubmissionContext; source: string; userName?: string; canViewSource?: boolean}>('/submissions/' + id);
    if (!selected.value || selected.value.id !== data.submission.problemId)
      selected.value = problems.value.find(p => p.id === data.submission.problemId) ? await api<Problem>('/problems/' + data.submission.problemId) : blankProblem();
    if (selected.value?.id===data.submission.problemId) restoreSource(selected.value);
    snapshotSource.value = data.source;
    canViewSource.value = data.canViewSource ?? !!data.source;
    submissionContext.value=data.context; submission.value = data.submission; result.value = data.result; expanded.value = undefined;
    if (active(data.submission.status)) connect(id); else closeFeed();
  });
}
async function cancel() { if (submission.value) await perform(async () => { await api('/submissions/' + submission.value!.id + '/cancel', { method: 'POST' }); await loadHistory(); }); }
async function uploadCode(event: Event) { const input=event.target as HTMLInputElement; const file=input.files?.[0]; try { if(file) await perform(async()=>{if(file.size>1048576)throw new Error('源码超过 1 MiB，请精简后载入');source.value=await file.text();}); } finally {input.value='';} }
function loadExample() { if (selected.value && confirm('用本题读写模板替换当前编辑器内容？')) source.value = selected.value.exampleSource; }
let clock: ReturnType<typeof setInterval> | undefined, environmentClock: ReturnType<typeof setInterval> | undefined;
useModalAccessibility(computed(()=>[showProfile.value,showSnapshot.value,currentCase.value].filter(Boolean).length));
onMounted(async () => {
  addEventListener('beforeunload', protect);
  clock = setInterval(() => nowMs.value = Date.now(), 1000);
  environmentClock=setInterval(()=>{ if(me.value&&!showAuth.value) api<typeof environment.value>('/environment').then(data=>environment.value=data).catch(()=>{}); },15000);
  await loadMe();
  if (me.value) await boot();
});
onBeforeUnmount(() => { clearInterval(clock); clearInterval(environmentClock); closeFeed(); removeEventListener('beforeunload', protect); });
</script>

<template>
  <div class="app-shell">
    <main v-if="showAuth" class="auth-page">
      <div class="auth-story"><span class="eyebrow">COMPILE / RUN / UNDERSTAND</span><h1>下一次 AC，<br />始于这一次尝试。</h1><p>把思路写成程序，让真实的运行证据<br />帮助你找到下一步。</p><ol><li><b>01</b>五类评测，覆盖不同竞赛协议</li><li><b>02</b>逐测试点定位时间、内存与输出</li><li><b>03</b>在比赛与日常训练中持续进步</li></ol></div><section class="auth-card">
        <a class="brand auth-brand"><span class="brand-mark"><Terminal :size="21" /></span><span>ACM<span class="brand-sub">ONLINE JUDGE</span></span></a>
        <p class="auth-school">浙江科技大学 · 算法竞赛在线评测</p>
        <div class="auth-tabs"><button type="button" :class="{active: authTab==='login'}" @click="authTab='login'; authError=''">登录</button><button type="button" :class="{active: authTab==='register'}" @click="authTab='register'; authError=''">注册</button></div>
        <form @submit.prevent="submitAuth">
          <label>学号<input v-model="authForm.studentId" autocomplete="username" placeholder="浙江科技大学学号（4–12 位数字）" required /></label>
          <template v-if="authTab==='register'">
            <label>真实姓名<input v-model="authForm.name" placeholder="用于排行榜与提交记录展示" required /></label>
            <label>邮箱<input v-model="authForm.email" type="email" placeholder="用于唯一标识账号" required /></label>
          </template>
          <label>密码<input v-model="authForm.password" type="password" :autocomplete="authTab==='login' ? 'current-password' : 'new-password'" placeholder="至少 6 位" required /></label>
          <label v-if="authTab==='register'">确认密码<input v-model="authForm.confirm" type="password" autocomplete="new-password" required /></label>
          <p v-if="authError" class="auth-error">{{ authError }}</p>
          <button class="button primary auth-submit" :disabled="authBusy">{{ authTab==='login' ? '登录' : '注册并登录' }}</button>
        </form>
        <p class="auth-hint">注册需绑定浙江科技大学学号，一个学号只能注册一个账号。真实姓名与学号会出现在校内排行榜和提交记录中，可在个人设置中选择匿名。</p>
      </section>
    </main>

    <template v-else>
      <aside class="sidebar">
        <a class="brand" href="/" aria-label="ACM OJ 首页"><span class="brand-mark"><Terminal :size="21" /></span><span>ACM<span class="brand-sub">ONLINE JUDGE</span></span><span class="version">01</span></a>
        <div class="side-label">在线评测<span class="local-dot"></span></div>
        <button class="nav-item" :class="{selected: nav==='set' && !selected && !standings}" @click="back()"><Layers :size="17" /> 题库 <span>{{ problems.length }}</span></button>
        <button class="nav-item" :class="{selected: nav==='contests'}" @click="nav='contests'; selected=undefined; standings=undefined; loadContests()"><Trophy :size="16" /> 比赛 <span>{{ contests.length }}</span></button>
        <button class="nav-item" :class="{selected: nav==='leaderboard'}" @click="nav='leaderboard'; selected=undefined; standings=undefined; loadLeaderboard()"><Medal :size="16" /> 排行榜</button>
        <button class="nav-item" :class="{selected: nav==='history'}" @click="nav='history'; selected=undefined; standings=undefined; loadHistory()"><History :size="17" /> 提交记录 <ArrowUpRight :size="14" class="push" /></button>
        <div class="library-title"><span>快速切换</span></div>
        <div class="search-box"><Search :size="15" /><input v-model="search" placeholder="搜索题目…" aria-label="搜索题目" /></div>
        <div class="problem-list">
          <button v-for="(p,i) in problems.filter(p=>p.title.toLowerCase().includes(search.toLowerCase()))" :key="p.id" class="problem-link" :class="{current:selected?.id===p.id}" @click="select(p.id)">
            <span class="problem-index">{{ String(i+1).padStart(2,'0') }}</span><span class="problem-link-content"><strong>{{ p.title }}</strong><span><i :style="{background:modeInfo(p.mode).color}"></i>{{ modeInfo(p.mode).label }}<span class="test-count">{{ p.testCount }} 点</span></span></span><ChevronRight v-if="selected?.id===p.id" :size="14" />
          </button>
          <p v-if="!problems.length" class="muted empty-small">没有匹配的题目</p>
        </div>
        <div class="sidebar-bottom"><p><span class="local-dot"></span> 提交即表示同意仅用于学习训练</p></div>
      </aside>

      <div class="main-shell">
        <header class="topbar">
          <div class="breadcrumb">在线评测<ChevronRight :size="12" /><b>{{ standings ? standings.title + ' · 排行榜' : selected ? selected.title : nav==='contests' ? '比赛' : nav==='history' ? '提交记录' : nav==='leaderboard' ? '排行榜' : '题库' }}</b></div>
          <div class="topbar-right"><EnvironmentStatus :value="environment" />
            <div class="runtime-tag"><span class="local-dot"></span>{{ platformTag }}<span class="separator">/</span><strong>C++23</strong></div>
            <div class="user-chip">
              <button class="user-button" @click="menuOpen=!menuOpen"><span class="user-avatar">{{ me?.name.slice(0,1) }}</span><span class="user-meta"><strong>{{ me?.name }}</strong><small>{{ me?.studentId }}</small></span><ChevronDown :size="14" /></button>
              <div v-if="menuOpen" class="menu-backdrop" @click="menuOpen=false"></div>
              <div v-if="menuOpen" class="user-menu">
                <button @click="menuOpen=false; openProfile()"><Settings :size="15" />个人设置</button>
                <button @click="doLogout"><LogOut :size="15" />退出登录</button>
              </div>
            </div>
          </div>
        </header>
        <div v-if="error" class="alert" role="alert"><AlertCircle :size="17" />{{ error }}<button class="icon-btn push" aria-label="关闭错误" @click="error='' "><X :size="16" /></button></div>
        <div v-if="notice" class="notice" role="status"><Check :size="16" />{{ notice }}</div>

        <main v-if="standings" class="history-page"><div class="page-heading"><div><div class="eyebrow">STANDINGS</div><h1>{{ standings.title }} · 排行榜</h1><p class="muted">{{ phaseLabel(standings.phase) }}<template v-if="standings.startsAt"> · {{ fmtTime(standings.startsAt) }}<template v-if="standings.endsAt"> – {{ fmtTime(standings.endsAt) }}</template></template> · ICPC 赛制：通过数优先，罚时 = 用时（分钟）+ 每次错误提交 20 分钟<template v-if="standings.rated"> · Rated<template v-if="standings.settled">，已结算校内 rating</template></template></p></div><div class="heading-actions"><button class="button secondary" @click="openStandings(standings.contestId)"><RefreshCw :size="15" />刷新</button><button class="button secondary" @click="standings=undefined; nav='contests'"><ChevronLeft :size="15" />返回比赛</button></div></div>
          <div class="table-wrap standings-wrap"><table class="standings-table"><thead><tr><th>#</th><th>选手</th><th v-for="p in standings.problems" :key="p.problemId" class="cell-head" :title="p.title + ' · ' + p.rating">{{ p.letter }}</th><th>通过</th><th>罚时</th><th v-if="standings.rated && standings.settled">Rating</th></tr></thead>
            <tbody>
              <tr v-for="r in standings.rows" :key="r.userId" :class="{'own-row':r.userId===me?.id}">
                <td><span class="problem-index">{{ r.rank }}</span></td>
                <td><strong v-if="!r.anonymous">{{ r.name }}</strong><span v-else class="muted">已匿名</span></td>
                <td v-for="(c, i) in r.cells" :key="i" class="standings-cell" :class="c.accepted ? 'ac' : c.attempts ? 'wa' : ''"><span>{{ cellText(c) }}</span><small v-if="c.accepted">{{ c.acMinutes }}</small></td>
                <td><strong>{{ r.solved }}</strong></td>
                <td class="muted">{{ r.penalty }}</td>
                <td v-if="standings.rated && standings.settled"><span :class="(r.delta ?? 0) >= 0 ? 'success-text' : 'danger-text'"><b>{{ (r.delta ?? 0) >= 0 ? '+' : '' }}{{ r.delta }}</b></span></td>
              </tr>
            </tbody>
          </table><div v-if="!standings.rows.length" class="empty-state">{{ standings.phase === 'running' ? '比赛进行中，还没有选手提交。' : '本场还没有有效提交。' }}</div></div>
        </main>

        <main v-else-if="nav==='leaderboard' && !selected" class="history-page"><div class="page-heading"><div><div class="eyebrow">LEADERBOARD</div><h1>校内排行榜</h1><p class="muted">校内 rating 榜由 rated 比赛结算；Codeforces 榜基于绑定的 CF 账号；解题榜统计在本机通过的题目数。可在个人设置中选择匿名。</p></div><button class="button secondary" @click="loadLeaderboard"><RefreshCw :size="15" />刷新</button></div>
          <div class="set-bar"><div class="search-box"><Search :size="15" /><input v-model="leaderboardSearch" placeholder="搜索选手、学号或 CF 账号" aria-label="搜索排行榜" /></div><span class="muted">你的记录会高亮显示 · 匿名账号不会被姓名搜索命中</span></div><div class="panel-tabs lb-tabs">
            <button :class="{active:lbTab==='site'}" @click="lbTab='site'"><BarChart3 :size="16" />校内 rating</button>
            <button :class="{active:lbTab==='cf'}" @click="lbTab='cf'"><Trophy :size="16" />Codeforces rating</button>
            <button :class="{active:lbTab==='solved'}" @click="lbTab='solved'"><Check :size="16" />校内解题</button>
          </div>
          <div class="table-wrap"><table><thead><tr><th>名次</th><th>姓名</th><th>学号</th><th v-if="lbTab==='cf'">CF 账号</th><th v-if="lbTab==='cf'">Rating</th><th v-if="lbTab==='site'">校内 Rating</th><th v-if="lbTab==='site'">参赛场次</th><template v-if="lbTab!=='site'"><th>解题数</th><th>提交数</th></template></tr></thead>
            <tbody>
              <tr v-for="e in lbRows" :key="e.userId" :class="{'own-row':e.userId===me?.id}">
                <td><span class="problem-index">{{ e.rank }}</span></td>
                <td><strong v-if="!e.anonymous">{{ e.name }}</strong><span v-else class="muted">已匿名</span></td>
                <td class="muted">{{ e.anonymous ? '已匿名' : e.studentId }}</td>
                <td v-if="lbTab==='cf'">{{ e.anonymous ? '已匿名' : e.cfHandle }}</td>
                <td v-if="lbTab==='cf'"><strong :style="{color: cfRankColor(e.cfRank)}">{{ e.cfRating ?? '—' }}</strong> <span class="muted cf-rank-label">{{ e.cfRank }}</span></td>
                <td v-if="lbTab==='site'"><strong :style="{color: ratingColor(e.siteRating ?? 0)}">{{ e.siteRating }}</strong></td>
                <td v-if="lbTab==='site'" class="muted">{{ e.contestsPlayed }}</td>
                <td v-if="lbTab!=='site'"><strong>{{ e.solvedCount }}</strong></td>
                <td v-if="lbTab!=='site'" class="muted">{{ e.submissionCount }}</td>
              </tr>
            </tbody>
          </table><div v-if="!lbRows.length" class="empty-state">{{ lbTab==='site' ? '还没有已结算的 rated 比赛。比赛结束后访问其排行榜即自动结算。' : lbTab==='cf' ? '还没有人绑定 Codeforces 账号。在个人设置中绑定后即可上榜。' : '还没有用户在本机通过题目。' }}</div></div>
        </main>

        <main v-else-if="nav==='history' && !selected" class="history-page"><div class="page-heading"><div><div class="eyebrow">SUBMISSIONS</div><h1>提交记录</h1><p class="muted">保留逐测试点评测结果；源码仅本人可见。</p></div><button class="button secondary" @click="loadHistory"><RefreshCw :size="15" />刷新</button></div>
          <div class="set-bar">
            <select v-model="historyScope" aria-label="范围筛选" @change="loadHistory()"><option value="all">全部选手</option><option value="mine">我的提交</option></select>
            <select v-model="historyStatus" aria-label="状态筛选" @change="loadHistory()"><option value="all">全部状态</option><option v-for="s in statusOptions" :value="s">{{ s }} · {{ statusLabel(s) }}</option></select>
            <select v-model="historyProblem" aria-label="题目筛选" @change="loadHistory()"><option value="all">全部题目</option><option v-for="p in problems" :value="p.id">{{ p.title }}</option></select>
            <button v-if="historyScope!=='all' || historyStatus!=='all' || historyProblem!=='all'" class="button secondary" @click="historyScope='all'; historyStatus='all'; historyProblem='all'; loadHistory()"><Filter :size="14" />清除筛选</button>
          </div>
          <div class="table-wrap"><table><thead><tr><th>状态</th><th>题目</th><th>提交者</th><th>类型</th><th>提交时间</th><th></th></tr></thead><tbody><tr v-for="s in history" :key="s.id" tabindex="0" @keydown.enter="openSubmission(s.id)" @click="openSubmission(s.id)"><td><span class="verdict" :class="s.status==='AC'?'success':active(s.status)?'pending':'failed'">{{ s.status }}</span></td><td>{{ s.title }}</td><td>{{ s.userName || '—' }}</td><td>{{ modeInfo(s.mode).label }}</td><td class="muted">{{ new Date(s.createdAt).toLocaleString('zh-CN') }}</td><td><ArrowUpRight :size="16" /></td></tr></tbody></table><div v-if="!history.length" class="empty-state">没有符合条件的提交记录。</div></div></main>

        <main v-else-if="nav==='contests' && !selected" class="history-page"><div class="page-heading"><div><div class="eyebrow">CONTESTS</div><h1>比赛</h1><p class="muted">AtCoder 式时间窗：未开赛的题目对选手隐藏，开赛后自动公布；rated 比赛结束后访问排行榜即结算校内 rating。</p></div><button class="button secondary" @click="loadContests(); loadList()"><RefreshCw :size="15" />刷新</button></div>
          <div class="set-bar"><label>比赛阶段<select v-model="contestPhase"><option value="all">全部比赛</option><option value="running">进行中</option><option value="upcoming">未开始</option><option value="finished">已结束</option><option value="open">长期开放</option></select></label><span class="muted">{{ shownContests.length }} 场比赛</span></div><div class="contest-list">
            <div v-for="c in shownContests" :key="c.id" class="contest-row" :class="{upcoming: c.phase==='upcoming'}">
              <button class="contest-main" @click="c.phase==='upcoming' ? (notice='「'+c.title+'」尚未开始，开赛后再来') : openContest(c.title)">
                <span class="contest-title-line"><Trophy :size="16" class="success-text" /><strong>{{ c.title }}</strong><span v-if="c.rated" class="rated-chip" title="结束后结算校内 rating">RATED</span><span class="phase-badge" :class="c.phase">{{ phaseLabel(c.phase) }}</span></span>
                <span class="contest-info">
                  <span class="muted">{{ c.phase==='upcoming' ? '题目开赛后公布' : c.count + ' 道题 · 点击进入题库' }}</span>
                  <span v-if="c.startsAt" class="muted contest-time"><Clock :size="13" />{{ fmtTime(c.startsAt) }}<template v-if="c.endsAt"> – {{ fmtTime(c.endsAt) }}</template></span>
                  <span v-if="c.phase==='upcoming'" class="countdown-text">{{ untilText(c.startsAt!) }} 后开赛</span>
                  <span v-else-if="c.phase==='running' && c.endsAt" class="countdown-text live"><span class="local-dot"></span>{{ untilText(c.endsAt) }} 后结束</span>
                </span>
              </button>
              <button v-if="c.phase!=='upcoming'" class="button secondary" @click="openStandings(c.id)"><BarChart3 :size="14" />排行榜</button>
            </div>
            <div class="contest-row">
              <button class="contest-main" @click="openContest('none')"><span class="contest-title-line"><Layers :size="16" class="muted" /><strong>未分组</strong><span class="phase-badge open">长期开放</span></span><span class="contest-info"><span class="muted">{{ ungrouped }} 道题 · 点击进入题库</span></span></button>
            </div>
          </div>
          <div v-if="!contests.length" class="empty-state">还没有比赛。管理员可在 /admin 的「比赛管理」中创建。</div>
        </main>

        <main v-else-if="!selected" class="history-page"><TrainingOverview :data="training" :problems="problems" :name="me?.name || '选手'" @open="select" @history="nav='history'; historyScope='mine'; loadHistory()" /><div class="page-heading"><div><div class="eyebrow">PROBLEM SET <span class="tiny-rule"></span> {{ setRows.length }} / {{ problems.length }}</div><h1>题库</h1><p class="muted">点击任意一行进入题目；支持按关键词、难度、类型、比赛与做题状态筛选。</p></div></div>
          <div class="set-bar">
            <div class="search-box"><Search :size="15" /><input v-model="search" placeholder="搜索题名…" aria-label="搜索题名" /></div>
            <select v-model="sortBy" aria-label="题库排序"><option value="default">默认顺序</option><option value="rating-asc">难度从低到高</option><option value="rating-desc">难度从高到低</option><option value="popular">通过人数最多</option></select><select v-model="filter" aria-label="类型筛选"><option value="all">全部类型</option><option v-for="m in modes" :value="m.value">{{ m.label }}</option></select>
            <select v-model="ratingBand" aria-label="难度筛选"><option v-for="b in ratingBands" :value="b.value">{{ b.label }}</option></select>
            <select v-model="contestFilter" aria-label="比赛筛选"><option value="all">全部比赛</option><option v-for="c in contests" :value="c.title">{{ c.title }}</option><option value="none">未分组</option></select>
            <select v-model="statusFilter" aria-label="状态筛选"><option value="all">全部状态</option><option value="solved">已解决</option><option value="attempted">尝试过</option><option value="unsolved">未解决</option></select>
            <button v-if="search||filter!=='all'||contestFilter!=='all'||statusFilter!=='all'||ratingBand!=='all'" class="button secondary" @click="search=''; filter='all'; contestFilter='all'; statusFilter='all'; ratingBand='all'"><Filter :size="14" />清除筛选</button>
          </div>
          <div class="table-wrap compact"><table><thead><tr><th>编号</th><th>题目</th><th>比赛</th><th>难度</th><th>类型</th><th>测试点</th><th>通过</th><th>状态</th></tr></thead>
            <tbody>
              <tr v-for="(p,i) in setRows" :key="p.id" tabindex="0" @keydown.enter="select(p.id)" @click="select(p.id)">
                <td><span class="problem-index">{{ String(i+1).padStart(2,'0') }}</span></td>
                <td class="set-title"><strong>{{ p.title }}</strong></td>
                <td class="muted">{{ p.contestTitle || '未分组' }}</td>
                <td><b class="rating-cell" :style="{color:ratingColor(p.rating)}">{{ p.rating || '—' }}</b></td>
                <td><span class="mode-badge" :style="{color:modeInfo(p.mode).color,background:modeInfo(p.mode).color+'12'}">{{ modeInfo(p.mode).label }}</span></td>
                <td class="muted">{{ p.testCount }}</td>
                <td class="muted">{{ p.solvedBy ?? 0 }} 人</td>
                <td><span v-if="statusOf(p)" class="verdict" :class="statusOf(p)==='AC'?'success':active(statusOf(p))?'pending':'failed'">{{ statusOf(p)==='AC'?'已解决':statusOf(p) }}</span><span v-else class="muted">—</span></td>
              </tr>
            </tbody>
          </table><div v-if="!setRows.length" class="empty-state">没有符合条件的题目。</div></div>
        </main>

        <main v-else-if="selected" class="workspace">
          <div class="page-heading"><div><div class="eyebrow">PROBLEM <span class="tiny-rule"></span> {{ info.short }}</div><h1>{{ selected.title }}</h1><div class="problem-meta"><span class="mode-badge" :style="{color:info.color,background:info.color+'12'}">{{ info.label }}</span><span>{{ selected.limits.cpuMs }} ms</span><span>{{ selected.limits.memoryMiB }} MiB</span><span>{{ selected.tests.length }} 个测试点</span><span v-if="selected.contestTitle" class="muted">{{ selected.contestTitle }}</span></div></div><div class="heading-actions"><button class="button secondary" @click="back"><ChevronLeft :size="15" />返回题库</button></div></div>
          <div class="work-grid">
            <section class="work-panel">
              <div class="panel-tabs"><button :class="{active:tab==='statement'}" @click="tab='statement'"><BookOpen :size="16" />题目说明</button><button :class="{active:tab==='code'}" @click="tab='code'"><Code2 :size="17" />编写代码</button><span class="push file-label">{{ tab==='code'?'main.cpp':'README.md' }}</span></div>
              <div v-if="tab==='statement'" class="statement-area"><div class="mode-callout" :style="{'--mode':info.color}"><span class="callout-icon"><Layers :size="19" /></span><div><strong>{{ info.label }}</strong><p>{{ info.description }}</p></div></div><article class="markdown" v-html="rendered"></article><PublicFlowPreview v-if="selected.communicationSample" :sample="selected.communicationSample" /><div v-if="selected.protocol" class="protocol-note"><strong>读写约定</strong><p>{{ selected.protocol }}</p></div><div class="sample-grid" v-if="selected.tests[0] && (selected.tests[0].input || selected.tests[0].answer)"><div><div class="sample-label">{{ selected.mode === 'Multiprocess' ? '测试数据（仅调度器读取）' : '示例输入' }}<button class="copy-btn" v-if="selected.tests[0].input" @click="copySample(selected.tests[0].input, 'in')"><Copy :size="12" />{{ copiedSample==='in' ? '已复制' : '复制' }}</button></div><pre>{{ selected.tests[0].input || '（由评测程序提供）' }}</pre></div><div><div class="sample-label">{{ selected.mode === 'Multiprocess' ? '参考判定（仅调度器读取）' : '参考答案' }}<button class="copy-btn" v-if="selected.tests[0].answer" @click="copySample(selected.tests[0].answer, 'ans')"><Copy :size="12" />{{ copiedSample==='ans' ? '已复制' : '复制' }}</button></div><pre>{{ selected.tests[0].answer || '（由判题程序决定）' }}</pre></div></div><div class="statement-footer"><span>{{ latest ? '最近提交：'+statusLabel(latest.status) : '准备好后，切换到代码编辑器开始。' }}</span><button class="button primary" @click="tab='code'">开始编写<ChevronRight :size="16" /></button></div></div>
              <template v-else><div class="editor-toolbar"><span><span class="file-dot"></span>C++23 <span class="muted">/ GNU G++</span></span><div class="toolbar-actions"><button @click="loadExample()">读写模板</button><label><Upload :size="13" />载入 .cpp<input hidden type="file" accept=".cpp,.cc,.cxx" @change="uploadCode" /></label></div></div><div class="editor-keymap"><CodeEditor v-model="source" @submit="submit" /></div><div class="editor-footer"><span><span class="local-dot"></span>{{ draftSaved }} · <kbd>Ctrl</kbd>+<kbd>Enter</kbd> 提交</span><kbd>UTF-8</kbd></div></template>
              <div class="submit-bar"><div><span class="language-pill">C++23</span><span class="muted">-O2 · 标准输入输出</span></div><button v-if="running && canViewSource" class="button secondary" @click="cancel"><Square :size="14" />取消评测</button><button v-else class="button primary" :disabled="busy || !source.trim() || !selected.tests.length" @click="submit"><Play :size="15" fill="currentColor" />提交评测</button></div>
            </section>
            <JudgeResult :result="result" :submission="submission" :limits="submissionContext?.limits" :total="submissionContext?.testCount ?? result?.cases.length ?? 0" :mode="submissionContext?.mode ?? submission?.mode ?? selected?.mode" :connection="connection" :memory-metric="environment?.memoryMetric" :source-visible="canViewSource" title="评测结果" @inspect="expanded=$event" @source="showSnapshot=true" @refresh="submission && openSubmission(submission.id)"></JudgeResult>
          </div>
          <footer class="workspace-footer"><span>ACM ONLINE JUDGE</span><span>五种评测模式，一个在线工作台。</span><span>仅用于可信代码训练</span></footer>
        </main>
      </div>

      <CaseInspector :value="currentCase" @close="expanded=undefined" />

      <div v-if="showSnapshot" class="modal-backdrop"><section class="modal" role="dialog" aria-modal="true" aria-label="提交源码"><header class="modal-header"><h2>本次提交源码 · {{ submission?.title }}</h2><button class="icon-btn" aria-label="关闭源码" @click="showSnapshot=false"><X :size="20" /></button></header><CodeEditor v-model="snapshotSource" read-only /></section></div>

      <div v-if="showProfile" class="modal-backdrop"><section class="modal" role="dialog" aria-modal="true" aria-label="个人设置"><header class="modal-header"><div><span class="eyebrow">PROFILE</span><h2>个人设置</h2></div><button class="icon-btn" aria-label="关闭设置" @click="showProfile=false"><X :size="20" /></button></header>
        <div class="modal-body profile-body">
          <div v-if="error" class="alert"><AlertCircle :size="17" />{{ error }}<button class="icon-btn push" aria-label="关闭错误" @click="error=''"><X :size="16" /></button></div>
          <div class="panel-tabs profile-tabs"><button :class="{active:profileSection==='account'}" @click="profileSection='account'">资料与隐私</button><button :class="{active:profileSection==='security'}" @click="profileSection='security'">账号安全</button><button :class="{active:profileSection==='cf'}" @click="profileSection='cf'">Codeforces</button></div>
          <div v-if="profileSection==='account'">          <h3 class="section-label">基本资料</h3>
          <p class="site-rating-line">校内 Rating <b :style="{color: ratingColor(me?.siteRating ?? 0)}">{{ me?.siteRating ?? '—' }}</b><span class="muted">{{ me?.siteRating == null ? '参加 rated 比赛并在结束后查看排行榜即可自动定级' : '随 rated 比赛结算更新' }}</span></p>
          <div class="form-grid">
            <label>姓名<input v-model="profileForm.name" /></label>
            <label>邮箱<input v-model="profileForm.email" type="email" /></label>
          </div>
          <label class="checkbox"><input type="checkbox" v-model="profileForm.anonymous" /> 在排行榜中匿名（保留名次，隐藏姓名 / 学号 / CF 账号）</label>
          <div class="privacy-preview">榜单展示预览：{{ profileForm.anonymous ? '已匿名 · 姓名、学号与 CF 账号隐藏' : profileForm.name+' · '+me?.studentId }}</div><div class="profile-actions"><button class="button secondary" :disabled="busy" @click="saveProfile">保存资料</button></div>

</div><div v-else-if="profileSection==='security'">          <h3 class="section-label">修改密码</h3>
          <div class="form-grid">
            <label>当前密码<input v-model="passwordForm.current" type="password" autocomplete="current-password" /></label>
            <label>新密码<input v-model="passwordForm.next" type="password" autocomplete="new-password" placeholder="至少 6 位" /></label>
            <label>确认新密码<input v-model="passwordForm.confirm" type="password" autocomplete="new-password" /></label>
          </div>
          <div class="profile-actions"><button class="button secondary" :disabled="busy || !passwordForm.current || !passwordForm.next" @click="savePassword">修改密码</button></div>

</div><div v-else>          <h3 class="section-label">Codeforces 绑定</h3>
          <template v-if="!me?.cfHandle">
            <p class="muted bind-note">绑定后你的 Codeforces rating 将出现在校内排行榜。为防止冒用，绑定需要验证账号归属。</p>
            <div class="cf-bind">
              <label>CF handle<input v-model="cfHandleInput" placeholder="如 tourist" /></label>
              <button v-if="!cfChallenge" class="button secondary" :disabled="busy || !cfHandleInput.trim()" @click="issueChallenge">获取验证码</button>
            </div>
            <div v-if="cfChallenge" class="cf-challenge">
              <p>1. 登录 Codeforces → Settings → Social，把 <b>Last name</b> 临时改为：</p>
              <p><code class="cf-code">{{ cfChallengeCode }}</code> <span class="muted">（{{ cfMinutes }} 分钟内有效）</span></p>
              <p>2. 保存后点击下方按钮验证；验证成功后可将 Last name 改回原值。</p>
              <div class="profile-actions"><button class="button primary" :disabled="busy" @click="verifyChallenge">我已修改，立即验证</button><button class="text-link" @click="resetChallenge">取消</button></div>
            </div>
          </template>
          <template v-else>
            <div class="cf-bound">
              <span class="cf-bound-handle"><b>{{ me.cfHandle }}</b></span>
              <span :style="{color: cfRankColor(me.cfRank)}"><b>{{ me.cfRating ?? '—' }}</b> <small>{{ me.cfRank }}</small></span>
              <button class="text-link danger-text" @click="unbindCf">解除绑定</button>
            </div>
          </template>
</div>
        </div>
      </section></div>
    </template>
  </div>
</template>
