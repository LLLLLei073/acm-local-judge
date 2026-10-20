<script setup lang="ts">
import { computed, defineAsyncComponent, onMounted, onBeforeUnmount, ref, watch } from 'vue';
import { Plus, Search, Upload, Download, Play, Square, Settings2, ChevronRight, Code2, History, Check, X, FileCode2, Terminal, ArrowUpRight, RefreshCw, Pencil, Trash2, FlaskConical, Layers, AlertCircle, ChevronDown, Trophy, Users, Eye, EyeOff, Sparkles, ArrowLeft } from 'lucide-vue-next';
import { marked } from 'marked';
import DOMPurify from 'dompurify';
import FlowEditor from './FlowEditor.vue';
import FlowPreview from './FlowPreview.vue';
import { newFlow } from './flow';
import { api, needsToken, token, loginRequest } from './client';
import JudgeResult from './components/JudgeResult.vue';
import CaseInspector from './components/CaseInspector.vue';
import TestDataWorkbench from './components/TestDataWorkbench.vue';
import PreparationChecklist from './components/PreparationChecklist.vue';
import EnvironmentStatus from './components/EnvironmentStatus.vue';
import DraftShelf from './components/DraftShelf.vue';
import JudgeToolEditor from './components/JudgeToolEditor.vue';
import { bruteAssessment } from './domain/judge-tools';
import { useProblemDrafts } from './composables/useProblemDrafts';
import type { SubmissionContext } from './types';
import { useSubmissionFeed } from './composables/useSubmissionFeed';
import { useModalAccessibility } from './composables/useModalAccessibility';
const CodeEditor = defineAsyncComponent(() => import('./CodeEditor.vue'));
import { modes, modeInfo, statusLabel, active, blankProblem, ratingColor, type Problem, type ProblemSummary, type Submission, type Result } from './types';
import { loadAiConfig, saveAiConfig, aiReady, suggestRating, generateHarshTests, draftStatement, testConnection, fetchModels } from './ai';

const problems = ref<ProblemSummary[]>([]), selected = ref<Problem>(), search = ref('');
const history = ref<Submission[]>([]), submission = ref<Submission>(), result = ref<Result>();
const submissionContext=ref<SubmissionContext>();
const busy = ref(false), error = ref(''), notice = ref(''), showHistory = ref(false), showContests = ref(false), showUsers = ref(false), expanded = ref<string>();
const users = ref<{ id: string; studentId: string; name: string; email: string; cfHandle: string; cfRating: number | null; cfRank: string; siteRating: number | null; anonymous: boolean; createdAt: string }[]>([]);
const contests = ref<{ id: string; title: string; count: number; problemIds: string[]; startsAt: string | null; endsAt: string | null; rated: boolean; settled: boolean; phase: 'open' | 'upcoming' | 'running' | 'finished' }[]>([]);
const contestDraft = ref<{ id?: string; title: string; problemIds: string[]; startsAt: string; endsAt: string; rated: boolean }>();
const rosterSearch = ref('');
const bruteSource = ref(''), bruteResult = ref<{ results: { name: string; verdict: string; cpuMs: number }[]; stopped: boolean }>();
const snapshotSource = ref(''), showSnapshot = ref(false);
const bruteEvidence=computed(()=>bruteAssessment(bruteResult.value?.results??[]));
const draft = ref<Problem>(), editTab = ref('basic');
const importPreview = ref<Problem>(), environment = ref<{ os: string; memoryMetric: string; compiler: string; compilerExists: boolean; workerReady: boolean; parallelism: number; judging: number; queued: number; rateLimit: number }>(), settings = ref(false), compiler = ref(''), parallelism = ref(2);
const ai = ref(loadAiConfig()), aiBusy = ref(false), aiSuggestion = ref<{ rating: number; reason: string }>();
const aiTopic = ref('');
const aiModels = ref<string[]>([]), aiLoadingModels = ref(false), aiFeedback=ref('');
const aiConfigured = computed(() => aiReady(ai.value));
// AI 配置输入即保存：任何时刻点「建议评级/生成强化数据」用的都是最新配置。
watch(ai, v => saveAiConfig(v), { deep: true });
const { connect, close: closeFeed, connection } = useSubmissionFeed(submission, result, () => { loadHistory().catch(() => {}); }, submissionContext);
const adminFilter = ref('all'), userSearch = ref(''), historySearch = ref(''), historyVerdict = ref('all');
const visible = computed(() => problems.value.filter(p => (p.title+' '+p.id).toLowerCase().includes(search.value.trim().toLowerCase()) && (adminFilter.value==='all'||adminFilter.value==='draft'&&!p.published||adminFilter.value==='published'&&p.published||adminFilter.value==='unrated'&&!p.rating||adminFilter.value==='untested'&&!verifyOf(p.id))));
const visibleUsers = computed(()=>users.value.filter(u=>(u.name+' '+u.studentId+' '+u.email+' '+u.cfHandle).toLowerCase().includes(userSearch.value.trim().toLowerCase())));
const visibleHistory = computed(()=>history.value.filter(s=>(s.title+' '+(s.userName??'')+' '+s.id).toLowerCase().includes(historySearch.value.trim().toLowerCase())&&(historyVerdict.value==='all'||s.status===historyVerdict.value)));
const {entries:localDrafts,open:openProblemDraft,restore:restoreProblemDraft,saved:finishProblemDraft,remove:removeProblemDraft,dirty:dirtyProblemDraft,persisted:draftPersisted,status:draftStatus}=useProblemDrafts(draft);
const previewStatement = ref(false);
const draftRendered = computed(()=>DOMPurify.sanitize(marked.parse(draft.value?.statement||'') as string));
const aiPreview = ref<{problemId:string;tests:{input:string}[]}>();
const statementSuggestion = ref('');

const verifyOf = (problemId: string) => history.value.filter(s => s.problemId === problemId && !s.userId).sort((a, b) => b.createdAt.localeCompare(a.createdAt))[0];
const info = computed(() => selected.value ? modeInfo(selected.value.mode) : modes[0]);
const rendered = computed(() => DOMPurify.sanitize(marked.parse(selected.value?.statement || '') as string));
const running = computed(() => submission.value && active(submission.value.status));
const currentCase = computed(() => result.value?.cases.find(c => c.name === expanded.value));
const osName = computed(() => environment.value ? environment.value.os.split(' ')[0] : '本地');
let pendingOperations=0;
async function perform<T>(fn: () => Promise<T>):Promise<T|undefined> { error.value=''; pendingOperations++; busy.value=true; try { return await fn(); } catch(e) { error.value=e instanceof Error?e.message:String(e); } finally { busy.value=--pendingOperations>0; } }
async function login() {
  await perform(async () => {
    await loginRequest();
    await Promise.all([loadList(), loadHistory(), loadContests(), api<typeof environment.value>('/environment').then(data=>environment.value=data)]);
    notice.value = '登录成功，评测机已就绪';
  });
}
async function loadList() { problems.value = await api('/problems'); }
async function loadHistory() { history.value = await api('/submissions'); }
async function loadContests() { contests.value = await api('/contests'); }
async function loadUsers() { users.value = await api('/users'); }
async function removeUser(u: { id: string; name: string; studentId: string }) {
  if (!confirm('删除用户「' + u.name + '（' + u.studentId + '）」？其提交记录会保留但不再显示姓名。')) return;
  await perform(async () => { await api('/users/' + u.id, { method: 'DELETE' }); await loadUsers(); notice.value = '用户已删除'; });
}
// datetime-local 输入 ↔ ISO：编辑器里用本地时间，传输与存储用 UTC ISO。
const toLocalInput = (iso: string | null) => {
  if (!iso) return '';
  const d = new Date(iso), p = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}T${p(d.getHours())}:${p(d.getMinutes())}`;
};
const toIso = (value: string) => value ? new Date(value).toISOString() : null;
const phaseText = (phase: string) => ({ open: '长期开放', upcoming: '未开始', running: '进行中', finished: '已结束' }[phase] ?? phase);
const fmtTime = (iso: string | null) => iso ? new Date(iso).toLocaleString('zh-CN', { month: 'numeric', day: 'numeric', hour: '2-digit', minute: '2-digit' }) : '—';
function editContest(c?: { id: string; title: string; problemIds: string[]; startsAt: string | null; endsAt: string | null; rated: boolean }) {
  contestDraft.value = c ? { id: c.id, title: c.title, problemIds: [...c.problemIds], startsAt: toLocalInput(c.startsAt), endsAt: toLocalInput(c.endsAt), rated: c.rated } : { title: '', problemIds: [], startsAt: '', endsAt: '', rated: false };
}
function rosterMove(problemId: string, into: boolean) {
  if (!contestDraft.value) return;
  if (into && !problems.value.find(p=>p.id===problemId)?.rating) { error.value='请先为题目评级，再加入比赛'; return; }
  contestDraft.value.problemIds = into ? [...contestDraft.value.problemIds, problemId] : contestDraft.value.problemIds.filter(x => x !== problemId);
}
async function saveContest() {
  if (!contestDraft.value) return;
  await perform(async () => {
    const draft = contestDraft.value!;
    if (!draft.title.trim()) throw new Error('请填写比赛标题');
    if (draft.startsAt && draft.endsAt && new Date(draft.endsAt) <= new Date(draft.startsAt)) throw new Error('结束时间必须晚于开始时间');
    const saved = await api<{ id: string }>('/contests' + (draft.id ? '/' + draft.id : ''), { method: draft.id ? 'PUT' : 'POST', body: JSON.stringify({ title: draft.title.trim(), problemIds: draft.problemIds, startsAt: toIso(draft.startsAt), endsAt: toIso(draft.endsAt), rated: draft.rated }) });
    contestDraft.value = undefined; await loadContests(); await loadList(); notice.value = '比赛已保存（' + saved.id.slice(0, 8) + '…）';
  });
}
async function removeContest(c: { id: string; title: string; count: number }) {
  if (!confirm('解散比赛「' + c.title + '」？比赛内的题目不会被删除。')) return;
  await perform(async () => { await api('/contests/' + c.id, { method: 'DELETE' }); await loadContests(); await loadList(); });
}
async function select(id: string) {
  return perform(async () => {
    const p = await api<Problem>('/problems/' + id); selected.value = p; showHistory.value = false; showContests.value = false; showUsers.value = false;
    result.value = undefined; submission.value = undefined; submissionContext.value=undefined; expanded.value = undefined; closeFeed(); return p;
  });
}
function protect(e: BeforeUnloadEvent) { if (dirtyProblemDraft.value && !draftPersisted.value || running.value) { e.preventDefault(); e.returnValue = ''; } }
async function verify() {
  if (!selected.value) return;
  const src = selected.value.exampleSource.trim();
  if (!src) return;
  await perform(async () => {
    submission.value = await api<Submission>('/submissions', { method: 'POST', body: JSON.stringify({ problemId: selected.value!.id, source: src, purpose: 'verify' }) });
    submissionContext.value={title:selected.value!.title,mode:selected.value!.mode,limits:{...selected.value!.limits},testCount:selected.value!.tests.length};
    snapshotSource.value = src;
    result.value = { status: 'Queued', message: '', compilerVersion: '', compileArgs: '', compileLog: '', cases: [] }; expanded.value = undefined;
    connect(submission.value.id); await loadHistory();
  });
}
async function runBrute() {
  if (!selected.value || busy.value) return;
  await perform(async () => {
    bruteResult.value = await api('/problems/' + selected.value!.id + '/brute', { method: 'POST', body: JSON.stringify({ source: bruteSource.value }) });
  });
}
async function rejudgeAll() {
  if (!selected.value || !confirm('把该题全部历史提交按当前测试数据重新评测？')) return;
  await perform(async () => {
    const r = await api<{ requeued: number }>('/problems/' + selected.value!.id + '/rejudge-all', { method: 'POST' });
    await loadHistory();
    notice.value = '已重新入队 ' + r.requeued + ' 份提交，可在「提交记录」查看进度';
  });
}
async function openSubmission(id: string) {
  await perform(async () => {
    const data = await api<{submission: Submission; result: Result; context?: SubmissionContext; source: string}>('/submissions/' + id);
    selected.value = await api<Problem>('/problems/' + data.submission.problemId).catch(() => ({...blankProblem(), title: data.submission.title}));
    snapshotSource.value = data.source;
    submissionContext.value=data.context; submission.value = data.submission; result.value = data.result; showHistory.value = false; expanded.value = undefined;
    if (active(data.submission.status)) connect(id); else closeFeed();
  });
}
async function cancel() { if (submission.value) await perform(async () => { await api('/submissions/' + submission.value!.id + '/cancel', { method: 'POST' }); await loadHistory(); }); }
async function rejudge() { if (!submission.value||busy.value||running.value)return; await perform(async () => { submission.value = await api('/submissions/' + submission.value!.id + '/rejudge', {method:'POST'}); result.value={status:'Queued',message:'按原快照重新评测',compilerVersion:'',compileArgs:'',compileLog:'',cases:[]}; connect(submission.value!.id); }); }
async function togglePublish() {
  if (!selected.value) return;
  await perform(async () => {
    const p = await api<Problem>('/problems/' + selected.value!.id, { method: 'PUT', body: JSON.stringify({ ...selected.value!, published: !selected.value!.published }) });
    selected.value = p; await loadList();
    notice.value = p.published ? '题目已发布，选手端立即可见' : '题目已下架，选手端不再显示';
  });
}
async function toggleContest(c: { id: string; title: string; count: number; problemIds: string[]; startsAt: string | null; endsAt: string | null; rated: boolean }, into: boolean) {
  if (!selected.value) return;
  await perform(async () => {
    const problemIds = into ? [...c.problemIds, selected.value!.id] : c.problemIds.filter(x => x !== selected.value!.id);
    // PUT 是整体覆盖：保留原有时间窗与 rated 设置，只改题目名单。
    await api('/contests/' + c.id, { method: 'PUT', body: JSON.stringify({ title: c.title, problemIds, startsAt: c.startsAt, endsAt: c.endsAt, rated: c.rated }) });
    await loadContests(); await loadList();
    notice.value = (into ? '已加入比赛「' : '已移出比赛「') + c.title + '」';
  });
}
async function aiTest() {
  await perform(async () => { const model = await testConnection(); aiFeedback.value = 'AI 连接正常，模型 ' + model + ' 已就绪'; notice.value=aiFeedback.value; });
}
async function aiLoadModels() {
  await perform(async () => {
    aiLoadingModels.value = true;
    try {
      aiModels.value = await fetchModels();
      if (!ai.value.model.trim() && aiModels.value.length) ai.value.model = aiModels.value[0];
      aiFeedback.value = '已获取 ' + aiModels.value.length + ' 个可用模型，在模型输入框下拉选择'; notice.value=aiFeedback.value;
    } finally { aiLoadingModels.value = false; }
  });
}
async function aiRate() {
  if (!selected.value || aiBusy.value) return;
  await perform(async () => { aiBusy.value = true; try { aiSuggestion.value = await suggestRating(selected.value!); } finally { aiBusy.value = false; } });
}
async function aiAdoptRating() {
  if (!selected.value || !aiSuggestion.value) return;
  await perform(async () => {
    const p = await api<Problem>('/problems/' + selected.value!.id, { method: 'PUT', body: JSON.stringify({ ...selected.value!, rating: aiSuggestion.value!.rating }) });
    selected.value = p; aiSuggestion.value = undefined; await loadList(); notice.value = '已评级 ' + p.rating;
  });
}
async function aiDraftStatement() {
  const d = draft.value;
  if (!d || aiBusy.value) return;
  await perform(async () => {
    aiBusy.value = true;
    let markdown: string;
    try { markdown = await draftStatement(aiTopic.value.trim() || d.title, modeInfo(d.mode).label, modeInfo(d.mode).description); }
    finally { aiBusy.value = false; }
    if (!markdown) throw new Error('AI 未返回题面，请重试');
    statementSuggestion.value = markdown; notice.value = '题面建议已生成，预览后可应用到草稿';
  });
}
async function aiHarden() {
  if (!selected.value || aiBusy.value) return;
  await perform(async () => {
    aiBusy.value = true; let gen: { tests: { input: string }[] };
    try { gen = await generateHarshTests(selected.value!); } finally { aiBusy.value = false; }
    if(!gen.tests.length) throw new Error('AI 未能依据题面约束给出可靠输入，请补充明确的数据范围后重试');
    aiPreview.value={problemId:selected.value!.id,tests:gen.tests};
  });
}
async function applyAiTests() {
  if(!aiPreview.value) return;
  const preview=aiPreview.value;
  await perform(async()=> { const p=await api<Problem>('/problems/'+preview.problemId+'/harden',{method:'POST',body:JSON.stringify({tests:preview.tests})}); selected.value=p; aiPreview.value=undefined; await loadList(); notice.value='已计算答案并追加数据。请运行标准答案验证；AI 输入仍需检查是否满足题面约束。'; });
}
function edit(p?: Problem) { const restored=openProblemDraft(p??blankProblem()); editTab.value='basic'; previewStatement.value=false; statementSuggestion.value=''; if(restored)notice.value='已恢复本题未保存草稿'; }
function restoreDraft(id:string) { if(restoreProblemDraft(id)) { editTab.value='basic'; notice.value='已恢复本机草稿，请检查后保存'; } else error.value='该恢复点已不存在或无法读取'; }
function discardDraft(id:string) { if(confirm('丢弃这份本机未保存草稿？服务器题目和其他草稿将保留。')) removeProblemDraft(id); }
function editSection(section:string) { edit(selected.value); editTab.value=section; }

function openFlow() { if (draft.value) { draft.value.flow ??= newFlow(draft.value.processCount); editTab.value = 'flow'; } }
watch(() => draft.value?.mode, mode => { if (mode !== 'Multiprocess' && draft.value?.flow) draft.value.flow.enabled = false; });
function closeEdit() { if(busy.value)return; if(dirtyProblemDraft.value&&!draftPersisted.value&&!confirm('本机未能保存草稿，直接关闭将丢失修改。继续关闭？'))return; draft.value=undefined; }
async function save() {
  if (!draft.value) return;
  await perform(async () => {
    const body=JSON.stringify(draft.value);
    const p = await api<Problem>('/problems' + (draft.value!.id ? '/' + draft.value!.id : ''), { method: draft.value!.id ? 'PUT' : 'POST', body });
    if(JSON.stringify(draft.value)!==body) { await loadList(); notice.value='提交版本已保存；提交后的新修改仍保留在本机草稿中'; return; }
    finishProblemDraft(); await loadList(); await select(p.id); notice.value = '题目已保存';
  });
}
async function remove() {
  if (!selected.value || !confirm('删除当前题目？历史提交和快照仍会保留。')) return;
  await perform(async () => { await api('/problems/' + selected.value!.id, {method:'DELETE'}); selected.value = undefined; await loadList(); if (problems.value[0]) await select(problems.value[0].id); });
}
async function importZip(event: Event) {
  const input = event.target as HTMLInputElement; const file = input.files?.[0]; if (!file) return;
  await perform(async () => { const form = new FormData(); form.append('file', file); importPreview.value = await api('/packages/preview', { method: 'POST', body: form }); }); input.value = '';
}
async function confirmImport() {
  await perform(async () => { const p = await api<Problem>('/problems', {method:'POST', body:JSON.stringify({...importPreview.value,published:false})}); importPreview.value = undefined; await loadList(); await select(p.id); notice.value = '题目包已导入为新草稿'; });
}
async function openSettings() { aiFeedback.value=''; await perform(async () => { environment.value = await api('/environment'); compiler.value = environment.value!.compiler; parallelism.value = environment.value!.parallelism; settings.value = true; }); }
async function saveSettings() { await perform(async () => { if (!Number.isInteger(parallelism.value) || parallelism.value < 1 || parallelism.value > 8) throw new Error('并行评测数必须是 1–8 的整数'); await api('/settings', {method:'PUT', body:JSON.stringify({compiler: compiler.value, parallelism: String(parallelism.value)})}); settings.value = false; environment.value = await api('/environment'); notice.value = '设置已保存，并行评测数对后续提交立即生效'; }); }
async function openEditor(p: ProblemSummary) { const loaded=await select(p.id); if(loaded)edit(loaded); }
let environmentClock: ReturnType<typeof setInterval> | undefined;
useModalAccessibility(computed(()=>[draft.value,settings.value,importPreview.value,showSnapshot.value,currentCase.value,contestDraft.value,needsToken.value,aiPreview.value,statementSuggestion.value&&draft.value].filter(Boolean).length));
onMounted(async () => { environmentClock=setInterval(()=>{ if(!needsToken.value) api<typeof environment.value>('/environment').then(data=>environment.value=data).catch(()=>{}); },15000); addEventListener('beforeunload', protect); try { await Promise.all([loadList(), loadHistory(), loadContests(), api<typeof environment.value>('/environment').then(data=>environment.value=data)]); } catch { /* 401 已由 api() 弹出令牌输入 */ } });
onBeforeUnmount(() => { clearInterval(environmentClock); closeFeed(); removeEventListener('beforeunload', protect); });
</script>

<template>
  <div class="app-shell">
    <aside class="sidebar">
      <a class="brand" href="/admin" aria-label="ACM 管理控制台"><span class="brand-mark"><Terminal :size="21" /></span><span>ACM<span class="brand-sub">ADMIN</span></span><span class="version">01</span></a>
      <div class="side-label">管理控制台<span class="local-dot"></span></div>
      <button class="nav-item" :class="{selected: !showHistory && !showContests && !showUsers && !selected}" @click="showHistory=false; showContests=false; showUsers=false; selected=undefined"><Layers :size="17" /> 题目工作台 <span>{{ problems.length }}</span></button>
      <button class="nav-item" :class="{selected: showContests}" @click="showContests=true; showHistory=false; showUsers=false; loadContests()"><Trophy :size="16" /> 比赛管理 <span>{{ contests.length }}</span></button>
      <button class="nav-item" :class="{selected: showUsers}" @click="showUsers=true; showHistory=false; showContests=false; loadUsers()"><Users :size="16" /> 用户管理 <span>{{ users.length }}</span></button>
      <button class="nav-item" :class="{selected: showHistory}" @click="showHistory=true; showContests=false; showUsers=false; loadHistory()"><History :size="17" /> 提交记录 <ArrowUpRight :size="14" class="push" /></button>
      <div class="sidebar-bottom"><label class="side-import"><Upload :size="16" /> 导入题目包<input type="file" accept=".zip" hidden @change="importZip" /></label><button class="nav-item" @click="openSettings"><Settings2 :size="16" /> 运行环境</button></div>
    </aside>

    <div class="main-shell">
      <header class="topbar"><div class="breadcrumb">工作空间<ChevronRight :size="12" /><b>{{ showHistory ? '提交记录' : showContests ? '比赛管理' : showUsers ? '用户管理' : '题目工作台' }}</b></div><EnvironmentStatus :value="environment" /></header>
      <div v-if="error" class="alert" role="alert"><AlertCircle :size="17" />{{ error }}<button class="icon-btn push" aria-label="关闭错误" @click="error='' "><X :size="16" /></button></div>
      <div v-if="notice" class="notice" role="status"><Check :size="16" />{{ notice }}</div>

      <main v-if="showUsers" class="history-page"><div class="set-bar"><div class="search-box"><Search :size="15" /><input v-model="userSearch" placeholder="搜索姓名、学号、邮箱或 CF 账号" aria-label="搜索用户" /></div><span class="muted">{{ visibleUsers.length }} / {{ users.length }} 位用户</span><button class="button secondary" @click="loadUsers">刷新用户</button></div><div class="page-heading"><div><div class="eyebrow">USERS</div><h1>用户管理</h1><p class="muted">注册用户绑定浙江科技大学学号（唯一）；删除用户后其提交仍保留，但榜单与提交记录不再显示其姓名。</p></div></div><div class="table-wrap"><table><thead><tr><th>姓名</th><th>学号</th><th>邮箱</th><th>校内 Rating</th><th>Codeforces</th><th>匿名</th><th>注册时间</th><th></th></tr></thead><tbody><tr v-for="u in visibleUsers" :key="u.id"><td><strong>{{ u.name }}</strong></td><td class="muted">{{ u.studentId }}</td><td class="muted">{{ u.email }}</td><td><b v-if="u.siteRating" :style="{color:ratingColor(u.siteRating)}">{{ u.siteRating }}</b><span v-else class="muted">未定级</span></td><td>{{ u.cfHandle ? u.cfHandle + ' · ' + (u.cfRating ?? '—') : '—' }}</td><td>{{ u.anonymous ? '是' : '否' }}</td><td class="muted">{{ new Date(u.createdAt).toLocaleString('zh-CN') }}</td><td><button class="icon-btn framed danger-text" aria-label="删除用户" title="删除用户" @click="removeUser(u)"><Trash2 :size="15" /></button></td></tr></tbody></table><div v-if="!users.length" class="empty-state">还没有注册用户。选手可在 OJ 首页注册。</div></div></main>

      <main v-else-if="showContests" class="history-page"><div class="page-heading"><div><div class="eyebrow">CONTESTS</div><h1>比赛管理</h1><p class="muted">为比赛设置时间窗（AtCoder 式）：未开赛的题目对选手隐藏；勾选 rated 的比赛结束后，选手首次访问排行榜即自动结算校内 rating。</p></div><button class="button primary" @click="editContest()"><Plus :size="15" />新建比赛</button></div><div class="table-wrap"><table><thead><tr><th>比赛</th><th>阶段</th><th>时间窗</th><th>题目数</th><th></th></tr></thead><tbody><tr v-for="c in contests" :key="c.id"><td><strong>{{ c.title }}</strong><span v-if="c.rated" class="rated-chip">RATED</span><span v-if="c.rated && c.settled" class="muted">（已结算）</span></td><td><span class="phase-badge" :class="c.phase">{{ phaseText(c.phase) }}</span></td><td class="muted">{{ c.startsAt ? fmtTime(c.startsAt) + ' – ' + fmtTime(c.endsAt) : '长期开放' }}</td><td class="muted">{{ c.count }} 道题</td><td><div class="heading-actions"><button class="button secondary" @click="editContest(c)"><Pencil :size="14" />编辑</button><button class="icon-btn framed danger-text" aria-label="解散比赛" title="解散比赛" @click="removeContest(c)"><Trash2 :size="15" /></button></div></td></tr></tbody></table><div v-if="!contests.length" class="empty-state">还没有比赛，点击右上角新建，然后把题目加入其中。</div></div></main>

      <main v-else-if="showHistory" class="history-page"><div class="set-bar"><div class="search-box"><Search :size="15" /><input v-model="historySearch" placeholder="搜索题目、提交者或提交编号" aria-label="搜索提交记录" /></div><select v-model="historyVerdict" aria-label="筛选提交状态"><option value="all">全部状态</option><option v-for="state in ['AC','WA','CE','RE','TLE','MLE','OLE','PE','Judge Error','Queued','Compiling','Running','Cancelled','Interrupted']" :key="state">{{ state }}</option></select><button class="button secondary" @click="loadHistory">刷新记录</button></div><div class="page-heading"><div><div class="eyebrow">SUBMISSIONS</div><h1>每一次运行，都有记录。</h1><p class="muted">保留源码、题目快照与逐测试点评测结果。</p></div></div><div class="table-wrap"><table><thead><tr><th>状态</th><th>题目</th><th>作者</th><th>类型</th><th>提交时间</th><th></th></tr></thead><tbody><tr v-for="s in visibleHistory" :key="s.id" tabindex="0" @keydown.enter="openSubmission(s.id)" @click="openSubmission(s.id)"><td><span class="verdict" :class="s.status==='AC'?'success':active(s.status)?'pending':'failed'">{{ s.status }}</span></td><td>{{ s.title }}</td><td>{{ s.userName || '—' }}</td><td>{{ modeInfo(s.mode).label }}</td><td class="muted">{{ new Date(s.createdAt).toLocaleString('zh-CN') }}</td><td><ArrowUpRight :size="16" /></td></tr></tbody></table><div v-if="!history.length" class="empty-state">还没有提交记录。选择一道题，先做一次验证。</div></div></main>

      <main v-else-if="selected" class="workspace">
        <div class="page-heading"><div><div class="eyebrow">VERIFY WORKBENCH <span class="tiny-rule"></span> {{ info.short }}</div><h1>{{ selected.title }}</h1><div class="problem-meta"><span class="mode-badge" :style="{color:info.color,background:info.color+'12'}">{{ info.label }}</span><span class="rating-chip" :style="{color:ratingColor(selected.rating),borderColor:ratingColor(selected.rating)}">{{ selected.rating ? selected.rating : '未评级' }}</span><span>{{ selected.limits.cpuMs }} ms</span><span>{{ selected.limits.memoryMiB }} MiB</span><span>{{ selected.tests.length }} 个测试点</span></div></div><div class="heading-actions"><button class="button secondary" @click="selected=undefined"><ArrowLeft :size="15" />返回列表</button><button class="button secondary" @click="edit(selected)"><Pencil :size="15" />编辑题目</button><a class="icon-btn framed" :href="'/api/problems/'+selected.id+'/export'" aria-label="导出题目包" title="导出题目包"><Download :size="17" /></a><button class="icon-btn framed danger-text" aria-label="删除题目" title="删除题目" @click="remove"><Trash2 :size="16" /></button></div></div>
        <PreparationChecklist :problem="selected" :verification="verifyOf(selected.id)" @edit="editSection" /><div class="publish-bar">
          <span class="publish-state" :class="selected.published ? 'live' : 'off'"><span class="local-dot"></span>{{ selected.published ? '已发布 · 选手端可见' : '草稿 · 选手不可见' }}</span>
          <button class="button secondary" :disabled="!selected.rating && !selected.published" :title="selected.rating ? '' : '评级后才能发布，可先用 AI 助手建议'" @click="togglePublish"><component :is="selected.published ? EyeOff : Eye" :size="15" />{{ selected.published ? '下架' : '发布' }}</button>
          <span v-if="!selected.rating && !selected.published" class="publish-warn">评级后才能发布</span>
          <span class="publish-label">所属比赛</span>
          <template v-if="contests.length"><button v-for="c in contests" :key="c.id" class="contest-chip" :class="{in:c.problemIds.includes(selected.id)}" :title="c.problemIds.includes(selected.id) ? '点击移出该比赛' : '点击加入该比赛'" @click="toggleContest(c, !c.problemIds.includes(selected.id))">{{ c.title }}</button></template>
          <span v-else class="muted">暂无比赛，可在「比赛管理」新建后加入</span>
        </div>
        <div class="ai-bar">
          <span class="ai-label">AI 助手</span>
          <button class="button secondary" :disabled="!aiConfigured || aiBusy" title="根据题面与类型建议 CF 风格难度" @click="aiRate"><Sparkles :size="14" />建议评级</button>
          <button class="button secondary" :disabled="!aiConfigured || aiBusy || !selected.exampleSource.trim()" title="在题目约束内生成最严苛的测试输入（由标准答案自动计算答案后入库），卡掉高复杂度提交" @click="aiHarden"><Sparkles :size="14" />生成强化数据</button>
          <template v-if="aiSuggestion"><span class="ai-suggestion">建议 <b :style="{color:ratingColor(aiSuggestion.rating)}">{{ aiSuggestion.rating }}</b> · {{ aiSuggestion.reason }}</span><button class="button primary" @click="aiAdoptRating"><Check :size="14" />采用</button><button class="icon-btn" aria-label="忽略建议" @click="aiSuggestion=undefined"><X :size="15" /></button></template>
          <span v-else-if="aiBusy" class="muted">AI 正在分析题目…（强化数据需在服务端编译标准答案逐条运行，请稍候）</span>
          <span v-else-if="!aiConfigured" class="muted">在「运行环境」中配置 AI 后，可一键建议评级、生成卡高复杂度的强化数据</span>
        </div>
        <div class="work-grid">
          <section class="work-panel">
            <div class="statement-area"><div class="mode-callout" :style="{'--mode':info.color}"><span class="callout-icon"><FlaskConical :size="19" /></span><div><strong>{{ info.label }}</strong><p>{{ info.description }}</p></div></div><article class="markdown" v-html="rendered"></article><FlowPreview v-if="selected.mode==='Multiprocess' && selected.flow" :flow="selected.flow" :test="selected.tests[0]" /><div v-if="selected.protocol" class="protocol-note"><strong>读写约定</strong><p>{{ selected.protocol }}</p></div><div class="sample-grid" v-if="selected.tests[0]"><div><div class="sample-label">{{ selected.mode === 'Multiprocess' ? '测试数据（仅调度器读取）' : '示例输入' }}</div><pre>{{ selected.tests[0].input || '（由评测程序提供）' }}</pre></div><div><div class="sample-label">{{ selected.mode === 'Multiprocess' ? '参考判定（仅调度器读取）' : '参考答案' }}</div><pre>{{ selected.tests[0].answer || '（由判题程序决定）' }}</pre></div></div></div>
            <div class="submit-bar verify-bar"><div><span class="language-pill">验题</span><span class="muted">运行标准答案源码，检验全部测试点（含强化数据）</span></div><div class="verify-actions"><button v-if="running" class="button secondary" @click="cancel"><Square :size="14" />取消验证</button><button v-else class="button primary" :disabled="busy || !selected.exampleSource.trim() || !selected.tests.length" @click="verify"><Play :size="15" fill="currentColor" />用标准答案验证</button><button v-if="!running" class="button secondary" :disabled="busy || !selected.tests.length" title="把该题历史提交按当前测试数据全部重新评测" @click="rejudgeAll"><RefreshCw :size="14" />整题重测</button></div></div>
            <p v-if="!selected.exampleSource.trim()" class="muted verify-hint">还没有标准答案源码：在「编辑题目 → 标准答案」中填写后即可一键验证，强化数据的答案也由它计算。</p>
            <details v-else class="brute-panel"><summary>暴力对拍<span class="muted"> —— 粘贴一段复杂度不达标的程序，确认强化数据能卡掉它</span></summary>
              <textarea v-model="bruteSource" rows="5" placeholder="#include <bits/stdc++.h> ... int main(){ /* O(n²) 暴力 */ }"></textarea>
              <div class="brute-actions"><button class="button primary" :disabled="busy || !bruteSource.trim() || !selected.tests.length" @click="runBrute"><Play :size="14" />运行对拍</button><span class="muted">按题目限制逐测试点运行，遇到首个非 AC 即停止</span></div>
              <div v-if="bruteResult" class="brute-result"><div class="brute-cases"><span v-for="r in bruteResult.results" :key="r.name" class="brute-case"><b>#{{ r.name }}</b><span :class="r.verdict==='AC'?'success-text':'danger-text'">{{ r.verdict }}</span><small>{{ r.cpuMs.toFixed(0) }} ms</small></span></div><p :class="bruteEvidence.tone">{{ bruteEvidence.text }}</p></div>
            </details>
          </section>
          <JudgeResult :result="result" :submission="submission" :limits="submissionContext?.limits" :total="submissionContext?.testCount ?? result?.cases.length ?? 0" :mode="submissionContext?.mode ?? submission?.mode ?? selected?.mode" :connection="connection" :memory-metric="environment?.memoryMetric" source-visible title="验证结果" @inspect="expanded=$event" @source="showSnapshot=true" @refresh="submission && openSubmission(submission.id)"><template #actions><button class="text-link" :disabled="busy||!!running" @click="rejudge">原快照重评</button></template></JudgeResult>
        </div>
      </main>
      <main v-else class="history-page">
        <div class="page-heading"><div><div class="eyebrow">VERIFY WORKBENCH</div><h1>验题工作台</h1><p class="muted">全部题目一屏评审：评级、测试点、最近验证与发布状态一目了然；点击任意一行进入单题验证。</p></div><div class="heading-actions"><div class="search-box"><Search :size="15" /><input v-model="search" placeholder="搜索题名…" aria-label="搜索题名" /></div><button class="button primary" @click="edit()"><Plus :size="15" />新建题目</button></div></div>
        <div class="admin-overview"><div><span class="eyebrow">PROBLEM OPERATIONS</span><h2>把一道好题，变成一次好训练。</h2><p>从数据与协议，到验证与发布，每一步都有依据。</p></div><dl><div><dt>全部题目</dt><dd>{{ problems.length }}</dd></div><div><dt>草稿</dt><dd>{{ problems.filter(p=>!p.published).length }}</dd></div><div><dt>待评级</dt><dd>{{ problems.filter(p=>!p.rating).length }}</dd></div><div><dt>已发布</dt><dd>{{ problems.filter(p=>p.published).length }}</dd></div></dl></div><div class="set-bar"><select v-model="adminFilter" aria-label="题目准备筛选"><option value="all">全部题目</option><option value="draft">草稿</option><option value="published">已发布</option><option value="unrated">待评级</option><option value="untested">没有验证记录</option></select><span class="muted">{{ visible.length }} / {{ problems.length }} 题</span></div><DraftShelf :entries="localDrafts" @restore="restoreDraft" @discard="discardDraft" /><div class="table-wrap compact"><table><thead><tr><th>编号</th><th>题目</th><th>难度</th><th>类型</th><th>测试点</th><th>最近验证</th><th>状态</th><th></th></tr></thead><tbody>
          <tr v-for="(p,i) in visible" :key="p.id" tabindex="0" @keydown.enter="select(p.id)" @click="select(p.id)">
            <td><span class="problem-index">{{ String(i+1).padStart(2,'0') }}</span></td>
            <td class="set-title"><strong>{{ p.title }}</strong><em v-if="!p.published" class="draft-flag">草稿</em></td>
            <td><b :style="{color:ratingColor(p.rating)}">{{ p.rating || '—' }}</b></td>
            <td>{{ modeInfo(p.mode).label }}</td>
            <td class="muted">{{ p.testCount }}</td>
            <td><span v-if="verifyOf(p.id)" class="verdict" :class="verifyOf(p.id)!.status==='AC'?'success':active(verifyOf(p.id)!.status)?'pending':'failed'">{{ verifyOf(p.id)!.status==='AC'?'通过':statusLabel(verifyOf(p.id)!.status) }}</span><span v-else class="muted">未验证</span></td>
            <td><span class="publish-state" :class="p.published?'live':'off'"><span class="local-dot"></span>{{ p.published ? '已发布' : '草稿' }}</span></td>
            <td><button class="button secondary" @click.stop="openEditor(p)"><Pencil :size="14" />编辑</button></td>
          </tr>
        </tbody></table><div v-if="!visible.length" class="empty-state">{{ search ? '没有匹配的题目。' : '还没有题目。点击右上角「新建题目」，或导入题目包开始。' }}</div></div>
      </main>
    </div>

    <div v-if="draft" class="modal-backdrop"><section class="modal edit-modal" role="dialog" aria-modal="true" aria-labelledby="edit-title"><header class="modal-header"><div><span class="eyebrow">PROBLEM BUILDER</span><h2 id="edit-title">{{ draft.id?'编辑题目':'新建题目' }}</h2></div><button class="icon-btn" aria-label="关闭编辑" @click="closeEdit"><X :size="21" /></button></header><div v-if="error" class="alert" role="alert">{{ error }}</div><div class="draft-save-line">{{ draftStatus || '修改会自动保留本机恢复点' }}</div><div class="panel-tabs"><button v-for="t in [{id:'basic',name:'题面与限制'},{id:'tests',name:'测试数据'},{id:'judge',name:'判题程序'}]" :class="{active:editTab===t.id}" @click="editTab=t.id">{{ t.name }}</button><button v-if="draft.mode==='Multiprocess'" :class="{active:editTab==='flow'}" @click="openFlow">成员与通信</button></div><div class="modal-body">
      <div v-if="editTab==='basic'" class="form-grid"><label class="span-2">题目标题<input v-model="draft.title" placeholder="例如：A + B" /></label><label>评测类型<select v-model="draft.mode"><option v-for="m in modes" :value="m.value">{{ m.label }}</option></select></label><label>难度 Rating<input type="number" v-model.number="draft.rating" min="0" max="3500" step="100" placeholder="0 = 未评级" /><small>100–3500；发布与加入比赛前必须评级，可在工作台用 AI 建议</small></label><label v-if="draft.mode==='Multiprocess'">进程数<input type="number" v-model.number="draft.processCount" min="2" max="16" :disabled="!!draft.flow" /><small v-if="draft.flow">在「成员与通信」中添加或删除成员</small></label><label>CPU 时间 / ms<input type="number" v-model.number="draft.limits.cpuMs" /></label><label>墙钟时间 / ms<input type="number" v-model.number="draft.limits.wallMs" /></label><label>内存 / MiB<input type="number" v-model.number="draft.limits.memoryMiB" /></label><label>输出上限 / KiB<input type="number" v-model.number="draft.limits.outputKiB" /></label><div class="span-2"><div class="section-toolbar"><h3>题面 · Markdown</h3><button class="button secondary" @click="previewStatement=!previewStatement">{{ previewStatement?'继续编辑':'预览题面' }}</button></div><article v-if="previewStatement" class="markdown statement-preview" v-html="draftRendered"></article><textarea v-else v-model="draft.statement" rows="12" class="full-width" aria-label="题目描述 Markdown"></textarea></div><div class="ai-draft span-2"><label>AI 起草题面 · 主题或要求<input v-model="aiTopic" placeholder="例如：树上带修改的两点最短路（结合当前评测类型）" /></label><button class="button secondary" :disabled="!aiConfigured || aiBusy" :title="aiConfigured ? '按主题生成完整 Markdown 题面草稿，填入后请人工核对' : '在「运行环境」中配置 AI 后可用'" @click="aiDraftStatement"><Sparkles :size="14" />{{ aiBusy ? 'AI 起草中…' : 'AI 起草题面' }}</button></div><label v-if="draft.mode!=='Standard' && draft.mode!=='Special'" class="span-2">读写协议<textarea v-model="draft.protocol" rows="2" placeholder="说明选手程序的输入输出约定"></textarea></label><template v-if="draft.mode==='Communication'"><label>第一轮标记<input v-model="draft.firstMarker" /></label><label>第二轮标记<input v-model="draft.secondMarker" /></label><label class="checkbox span-2"><input type="checkbox" v-model="draft.managedCommunication" />使用 manager 驱动双轮交互</label></template></div>
      <TestDataWorkbench v-else-if="editTab==='tests'" v-model="draft.tests" :mode="draft.mode" />
      <FlowEditor v-else-if="editTab==='flow' && draft.flow" v-model="draft.flow" :test="draft.tests[0]" @members-changed="draft.processCount=$event" /><JudgeToolEditor v-else v-model="draft" />
    </div><footer class="modal-footer"><span class="muted">修改不会影响已提交的评测快照。</span><button class="button secondary" @click="closeEdit">取消</button><button class="button primary" :disabled="busy" @click="save"><Check :size="16" />保存题目</button></footer></section></div>

    <CaseInspector :value="currentCase" @close="expanded=undefined" />

    <div v-if="importPreview" class="modal-backdrop"><section class="modal small-modal" role="dialog" aria-modal="true" aria-label="导入题目包"><header class="modal-header"><h2>题目包预览</h2><button class="icon-btn" aria-label="关闭导入" @click="importPreview=undefined"><X :size="20" /></button></header><div class="modal-body"><div v-if="error" class="alert">{{ error }}</div><h3>{{ importPreview.title }}</h3><p>{{ modeInfo(importPreview.mode).label }} · {{ importPreview.tests.length }} 个测试点 · 难度 <b :style="{color:ratingColor(importPreview.rating)}">{{ importPreview.rating || '未评级' }}</b> · CPU {{ importPreview.limits.cpuMs }}ms / {{ importPreview.limits.memoryMiB }}MiB</p><p class="muted">校验完成。将创建新草稿，现有题目不会被覆盖。核对数据并用标准答案验证后再发布。</p><div class="import-summary"><span>题面 {{ importPreview.statement.length }} 字符</span><span>{{ importPreview.exampleSource ? '包含标准答案源码' : '尚无标准答案源码' }}</span><span v-if="importPreview.flow">{{ importPreview.flow.members.length }} 位成员 · {{ importPreview.flow.steps.length }} 个通信步骤</span><span>工具：{{ [importPreview.checker?'checker':'',importPreview.interactor?'interactor':'',importPreview.manager?'manager':''].filter(Boolean).join(' / ')||'引擎内置判定' }}</span></div><div class="import-files"><span v-for="t in importPreview.tests"><FileCode2 :size="14" />{{ t.name }}.in / .ans</span></div></div><footer class="modal-footer"><button class="button primary" :disabled="busy" @click="confirmImport"><Upload :size="15" />导入为草稿</button></footer></section></div>

    <div v-if="aiPreview" class="modal-backdrop"><section class="modal" role="dialog" aria-modal="true" aria-label="AI 测试数据预览"><header class="modal-header"><div><span class="eyebrow">AI PROPOSAL</span><h2>检查输入，再计算答案</h2></div><button class="icon-btn" aria-label="关闭预览" :disabled="busy" @click="aiPreview=undefined"><X :size="20" /></button></header><div class="modal-body"><p class="protocol-note">以下是 AI 提出的输入，尚未写入题目。请检查规模与约束；应用后由标准答案计算答案。可删除不合法的数据。</p><article v-for="(t,i) in aiPreview.tests" :key="i" class="test-editor"><div class="section-toolbar"><b>建议 {{ i+1 }}</b><button class="text-link danger-text" :disabled="busy" @click="aiPreview.tests.splice(i,1)">移除此项</button></div><textarea v-model="t.input" :readonly="busy" rows="6" class="full-width" :aria-label="'建议输入 '+(i+1)"></textarea></article></div><footer class="modal-footer"><button class="button secondary" :disabled="busy" @click="aiPreview=undefined">取消</button><button class="button primary" :disabled="busy||!aiPreview.tests.length" @click="applyAiTests">计算答案并追加 {{ aiPreview.tests.length }} 个测试点</button></footer></section></div>
    <div v-if="statementSuggestion && draft" class="modal-backdrop"><section class="modal" role="dialog" aria-modal="true" aria-label="AI 题面预览"><header class="modal-header"><h2>AI 题面建议</h2><button class="icon-btn" aria-label="关闭题面建议" @click="statementSuggestion=''"><X :size="20" /></button></header><div class="modal-body"><p class="protocol-note">建议尚未覆盖原稿。应用后请核对约束、协议与样例。</p><pre>{{ statementSuggestion }}</pre></div><footer class="modal-footer"><button class="button secondary" @click="statementSuggestion=''">保留原稿</button><button class="button primary" @click="draft.statement=statementSuggestion; statementSuggestion=''">应用到草稿</button></footer></section></div>
    <div v-if="showSnapshot" class="modal-backdrop"><section class="modal" role="dialog" aria-modal="true" aria-label="提交源码"><header class="modal-header"><h2>本次验证源码 · {{ submission?.title }}</h2><button class="icon-btn" aria-label="关闭源码" @click="showSnapshot=false"><X :size="20" /></button></header><CodeEditor v-model="snapshotSource" read-only /></section></div>
    <div v-if="settings" class="modal-backdrop"><section class="modal small-modal" role="dialog" aria-modal="true" aria-label="运行环境"><header class="modal-header"><h2>运行环境</h2><button class="icon-btn" aria-label="关闭设置" @click="settings=false"><X :size="20" /></button></header><div class="modal-body"><div v-if="error" class="alert">{{ error }}</div><div class="environment-status"><span class="local-dot"></span>{{ osName }} · C++23</div><p>编译器 {{ environment?.compilerExists?'已找到':'未找到' }} · Worker {{ environment?.workerReady?'就绪':'需要构建' }}</p><label>G++ 编译器完整路径<input v-model="compiler" /></label><p class="muted">默认参数：-std=c++23 -O2 -pipe</p><label>并行评测数（1–8）<input type="number" v-model.number="parallelism" min="1" max="8" /></label><p class="muted">同时评测的提交数，保存后立即生效。当前评测中 {{ environment?.judging ?? 0 }} · 排队 {{ environment?.queued ?? 0 }} · 每 IP 提交限流 {{ environment?.rateLimit ?? '—' }} 次/分钟（0 = 关闭）。</p><div class="ai-config"><h3>AI 助手（LangChain）</h3><div class="form-grid"><label>API Base URL<input v-model="ai.base" placeholder="https://api.deepseek.com/v1" /></label><div class="model-field"><label class="grow">模型<input v-model="ai.model" list="ai-model-options" placeholder="deepseek-chat" /><datalist id="ai-model-options"><option v-for="m in aiModels" :key="m" :value="m" /></datalist></label><button class="button secondary" :disabled="busy || aiLoadingModels || !ai.base.trim() || !ai.key.trim()" title="从服务拉取可用模型列表（GET /models）" @click="aiLoadModels">{{ aiLoadingModels ? '获取中…' : '获取模型' }}</button></div><label class="span-2">API Key<input v-model="ai.key" type="password" autocomplete="off" placeholder="sk-…" /></label></div><p class="muted">兼容 OpenAI 接口的服务（DeepSeek / Kimi / 通义 / Ollama 等）。配置仅保存在本浏览器，输入后自动保存并立即生效。</p><p v-if="aiFeedback" class="notice ai-feedback" role="status">{{ aiFeedback }}</p><div class="ai-config-actions"><button class="button secondary" :disabled="busy || !ai.base.trim() || !ai.key.trim() || !ai.model.trim()" @click="aiTest"><Check :size="14" />测试连接</button><span class="muted">点「测试连接」验证 Base URL / Key / 模型与跨域是否可用</span></div></div><div class="protocol-note">仅用于可信代码。远程部署时请经反向代理启用 HTTPS，并妥善保管访问令牌；进程资源限制不等于完整文件和网络隔离。</div></div><footer class="modal-footer"><button class="button primary" @click="saveSettings" :disabled="busy">保存设置</button></footer></section></div>
    <div v-if="contestDraft" class="modal-backdrop"><section class="modal" role="dialog" aria-modal="true" aria-label="比赛编辑"><header class="modal-header"><div><span class="eyebrow">CONTEST</span><h2>{{ contestDraft.id ? '编辑比赛' : '新建比赛' }}</h2></div><button class="icon-btn" aria-label="关闭" @click="contestDraft=undefined"><X :size="20" /></button></header><div class="modal-body"><div v-if="error" class="alert">{{ error }}</div><label>比赛标题<input v-model="contestDraft.title" placeholder="例如：2026 校赛初赛" /></label><div class="form-grid" style="margin-top:14px"><label>开始时间（留空 = 长期开放）<input type="datetime-local" v-model="contestDraft.startsAt" /></label><label>结束时间<input type="datetime-local" v-model="contestDraft.endsAt" :disabled="!contestDraft.startsAt" /></label></div><label class="checkbox" style="margin-top:14px"><input type="checkbox" v-model="contestDraft.rated" /> Rated：结束后按排名结算校内 rating（需设置结束时间）</label><p class="muted">设置了开始时间的比赛在开赛前对选手隐藏题目；结束时间到后比赛转为「已结束」，题目仍可练习。</p><label style="margin-top:14px">按题名过滤<input v-model="rosterSearch" placeholder="输入关键字…" /></label><div class="roster-cols"><div><h4>比赛内题目 · {{ contestDraft.problemIds.length }}</h4><div class="roster"><button v-for="p in problems.filter(p=>contestDraft!.problemIds.includes(p.id)&&p.title.toLowerCase().includes(rosterSearch.toLowerCase()))" :key="p.id" @click="rosterMove(p.id,false)"><X :size="13" class="danger-text" />{{ p.title }}<em v-if="!p.rating" class="roster-unrated">未评级</em><em v-else class="roster-rating" :style="{color:ratingColor(p.rating)}">{{ p.rating }}</em></button><p v-if="!contestDraft.problemIds.length" class="muted empty-small">尚未加入题目，从右侧添加</p></div></div><div><h4>未加入的题目</h4><div class="roster"><button v-for="p in problems.filter(p=>!contestDraft!.problemIds.includes(p.id)&&p.title.toLowerCase().includes(rosterSearch.toLowerCase()))" :key="p.id" @click="rosterMove(p.id,true)"><Plus :size="13" class="success-text" />{{ p.title }}<em v-if="!p.rating" class="roster-unrated">未评级</em><em v-else class="roster-rating" :style="{color:ratingColor(p.rating)}">{{ p.rating }}</em></button></div></div></div></div><footer class="modal-footer"><span class="muted">保存后选手端题库立即按比赛分组展示。</span><button class="button secondary" @click="contestDraft=undefined">取消</button><button class="button primary" :disabled="busy" @click="saveContest"><Check :size="16" />保存比赛</button></footer></section></div>
    <div v-if="needsToken" class="modal-backdrop"><section class="modal small-modal" role="dialog" aria-modal="true" aria-label="访问令牌"><header class="modal-header"><h2>输入访问令牌</h2><button class="icon-btn" aria-label="关闭" @click="needsToken=false"><X :size="20" /></button></header><div class="modal-body"><div v-if="error" class="alert">{{ error }}</div><p class="muted">这台测评机的 API 需要访问令牌。请向管理员索取启动日志中打印的令牌。</p><label>访问令牌<input v-model="token" type="password" autocomplete="off" @keyup.enter="login" /></label></div><footer class="modal-footer"><button class="button primary" :disabled="busy || !token.trim()" @click="login"><Check :size="16" />登录</button></footer></section></div>
  </div>
</template>
