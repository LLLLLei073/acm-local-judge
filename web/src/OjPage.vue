<script setup lang="ts">
import { computed, defineAsyncComponent, onMounted, onBeforeUnmount, ref, watch } from 'vue';
import { Search, ChevronRight, Code2, BookOpen, History, Check, X, Terminal, ArrowUpRight, RefreshCw, Play, Square, Upload, Settings2, Layers, AlertCircle, ChevronDown } from 'lucide-vue-next';
import { marked } from 'marked';
import DOMPurify from 'dompurify';
import { api, needsToken, token, loginRequest } from './client';
import { modes, modeInfo, statusLabel, active, memory, blankProblem, type Problem, type ProblemSummary, type Submission, type Result, type Case } from './types';
const CodeEditor = defineAsyncComponent(() => import('./CodeEditor.vue'));

// 选手 OJ 端：看题、提交、看结果。建题与管理功能在 /admin。
const problems = ref<ProblemSummary[]>([]), selected = ref<Problem>(), search = ref(''), filter = ref('all');
const source = ref('// C++23\n#include <bits/stdc++.h>\nusing namespace std;\n\nint main() {\n    ios::sync_with_stdio(false);\n    cin.tie(nullptr);\n\n    return 0;\n}\n');
const tab = ref('statement'), history = ref<Submission[]>([]), submission = ref<Submission>(), result = ref<Result>();
const busy = ref(false), error = ref(''), notice = ref(''), showHistory = ref(false), expanded = ref<string>();
const snapshotSource = ref(''), showSnapshot = ref(false);
const environment = ref<{ os: string; memoryMetric: string; workerReady: boolean; parallelism: number; judging: number; queued: number }>();
let events: EventSource | undefined;
const visible = computed(() => problems.value.filter(p => p.title.toLowerCase().includes(search.value.toLowerCase()) && (filter.value === 'all' || p.mode === filter.value)));
const info = computed(() => selected.value ? modeInfo(selected.value.mode) : modes[0]);
const rendered = computed(() => DOMPurify.sanitize(marked.parse(selected.value?.statement || '') as string));
const running = computed(() => submission.value && active(submission.value.status));
const accepted = computed(() => result.value?.cases.filter(c => c.verdict === 'AC').length ?? 0);
const latest = computed(() => selected.value ? history.value.find(s => s.problemId === selected.value!.id) : undefined);
const currentCase = computed(() => result.value?.cases.find(c => c.name === expanded.value));
const platformTag = computed(() => environment.value ? environment.value.os.split(' ')[0] : '在线评测');
const decode = (value: string) => new TextDecoder().decode(Uint8Array.from(atob(value), c => c.charCodeAt(0)));
async function perform(fn: () => Promise<void>) { error.value = ''; busy.value = true; try { await fn(); } catch (e) { error.value = e instanceof Error ? e.message : String(e); } finally { busy.value = false; } }
async function login() {
  await perform(async () => {
    await loginRequest();
    await Promise.all([loadList(), loadHistory(), api<typeof environment.value>('/environment').then(x => environment.value = x)]);
    if (problems.value[0]) await select(problems.value[0].id);
    notice.value = '登录成功，评测机已就绪';
  });
}
async function loadList() { problems.value = await api('/problems'); }
async function loadHistory() { history.value = await api('/submissions'); }
async function select(id: string) {
  await perform(async () => {
    const p = await api<Problem>('/problems/' + id); selected.value = p; showHistory.value = false;
    source.value = localStorage.getItem('acm-source-' + id) ?? p.exampleSource ?? '';
    result.value = undefined; submission.value = undefined; expanded.value = undefined; events?.close(); tab.value = 'statement';
  });
}
watch(source, value => { if (selected.value) localStorage.setItem('acm-source-' + selected.value.id, value); });
watch([selected, source], () => { notice.value = ''; });
function protect(e: BeforeUnloadEvent) { if (running.value) { e.preventDefault(); e.returnValue = ''; } }
function connect(id: string) {
  events?.close(); events = new EventSource('/api/submissions/' + id + '/events');
  events.onmessage = e => {
    const data = JSON.parse(e.data); submission.value = data.submission; result.value = data.result;
    if (!active(data.submission.status)) { events?.close(); loadHistory().catch(() => {}); }
  };
  events.onerror = () => { events?.close(); error.value = '实时连接已断开。点击刷新结果可重新连接。'; };
}
async function submit() {
  if (!selected.value) return;
  await perform(async () => {
    submission.value = await api<Submission>('/submissions', { method: 'POST', body: JSON.stringify({ problemId: selected.value!.id, source: source.value }) });
    snapshotSource.value = source.value;
    result.value = { status: 'Queued', message: '', compilerVersion: '', compileArgs: '', compileLog: '', cases: [] }; expanded.value = undefined;
    connect(submission.value.id); await loadHistory();
  });
}
async function openSubmission(id: string) {
  await perform(async () => {
    const data = await api<{submission: Submission; result: Result; source: string}>('/submissions/' + id);
    // Opening from history: show it under its own problem when the list knows it.
    if (!selected.value || selected.value.id !== data.submission.problemId)
      selected.value = problems.value.find(p => p.id === data.submission.problemId) ? await api<Problem>('/problems/' + data.submission.problemId) : blankProblem();
    snapshotSource.value = data.source;
    submission.value = data.submission; result.value = data.result; showHistory.value = false; expanded.value = undefined;
    if (active(data.submission.status)) connect(id); else events?.close();
  });
}
async function cancel() { if (submission.value) await perform(async () => { await api('/submissions/' + submission.value!.id + '/cancel', { method: 'POST' }); await loadHistory(); }); }
async function uploadCode(event: Event) { const f = (event.target as HTMLInputElement).files?.[0]; if (f) source.value = await f.text(); (event.target as HTMLInputElement).value = ''; }
function loadExample() { if (selected.value && confirm('用示例代码替换当前编辑器内容？')) source.value = selected.value.exampleSource; }
function toggle(c: Case) { expanded.value = expanded.value === c.name ? undefined : c.name; }
onMounted(async () => { addEventListener('beforeunload', protect); try { await Promise.all([loadList(), loadHistory(), api<typeof environment.value>('/environment').then(x => environment.value = x)]); if (problems.value[0]) await select(problems.value[0].id); } catch { /* 401 已由 api() 弹出令牌输入 */ } });
onBeforeUnmount(() => { events?.close(); removeEventListener('beforeunload', protect); });
</script>

<template>
  <div class="app-shell">
    <aside class="sidebar">
      <a class="brand" href="/" aria-label="ACM OJ 首页"><span class="brand-mark"><Terminal :size="21" /></span><span>ACM<span class="brand-sub">ONLINE JUDGE</span></span><span class="version">01</span></a>
      <div class="side-label">题目练习<span class="local-dot"></span></div>
      <button class="nav-item selected" @click="showHistory=false"><Layers :size="17" /> 题目列表 <span>{{ problems.length }}</span></button>
      <button class="nav-item" @click="showHistory=true; loadHistory()"><History :size="17" /> 提交记录 <ArrowUpRight :size="14" class="push" /></button>
      <div class="search-box"><Search :size="15" /><input v-model="search" placeholder="搜索题目…" aria-label="搜索题目" /></div>
      <select v-model="filter" class="filter" aria-label="评测类型筛选"><option value="all">全部评测类型</option><option v-for="m in modes" :value="m.value">{{ m.label }}</option></select>
      <div class="problem-list">
        <button v-for="(p,i) in visible" :key="p.id" class="problem-link" :class="{current:selected?.id===p.id}" @click="select(p.id)">
          <span class="problem-index">{{ String(i+1).padStart(2,'0') }}</span><span class="problem-link-content"><strong>{{ p.title }}</strong><span><i :style="{background:modeInfo(p.mode).color}"></i>{{ modeInfo(p.mode).label }}<span class="test-count">{{ p.testCount }} 点</span></span></span><ChevronRight v-if="selected?.id===p.id" :size="14" />
        </button>
        <p v-if="!visible.length" class="muted empty-small">没有匹配的题目</p>
      </div>
      <div class="sidebar-bottom"><a class="nav-item" href="/admin"><Settings2 :size="16" /> 管理员入口</a><p><span class="local-dot"></span> 提交即表示同意仅用于学习训练</p></div>
    </aside>

    <div class="main-shell">
      <header class="topbar"><div class="breadcrumb">在线评测<ChevronRight :size="12" /><b>{{ showHistory ? '提交记录' : '题目列表' }}</b></div><div class="runtime-tag"><span class="local-dot"></span>{{ platformTag }}<span class="separator">/</span><strong>C++23</strong></div></header>
      <div v-if="error" class="alert" role="alert"><AlertCircle :size="17" />{{ error }}<button class="icon-btn push" aria-label="关闭错误" @click="error='' "><X :size="16" /></button></div>
      <div v-if="notice" class="notice" role="status"><Check :size="16" />{{ notice }}</div>

      <main v-if="showHistory" class="history-page"><div class="page-heading"><div><div class="eyebrow">SUBMISSIONS</div><h1>每一次运行，都有记录。</h1><p class="muted">保留源码与逐测试点评测结果。</p></div><button class="button secondary" @click="loadHistory"><RefreshCw :size="15" />刷新</button></div><div class="table-wrap"><table><thead><tr><th>状态</th><th>题目</th><th>类型</th><th>提交时间</th><th></th></tr></thead><tbody><tr v-for="s in history" :key="s.id" @click="openSubmission(s.id)"><td><span class="verdict" :class="s.status==='AC'?'success':active(s.status)?'pending':'failed'">{{ s.status }}</span></td><td>{{ s.title }}</td><td>{{ modeInfo(s.mode).label }}</td><td class="muted">{{ new Date(s.createdAt).toLocaleString('zh-CN') }}</td><td><ArrowUpRight :size="16" /></td></tr></tbody></table><div v-if="!history.length" class="empty-state">还没有提交记录。选择一道题，开始第一次评测。</div></div></main>

      <main v-else-if="selected" class="workspace">
        <div class="page-heading"><div><div class="eyebrow">PROBLEM <span class="tiny-rule"></span> {{ info.short }}</div><h1>{{ selected.title }}</h1><div class="problem-meta"><span class="mode-badge" :style="{color:info.color,background:info.color+'12'}">{{ info.label }}</span><span>{{ selected.limits.cpuMs }} ms</span><span>{{ selected.limits.memoryMiB }} MiB</span><span>{{ selected.tests.length }} 个测试点</span></div></div><div class="heading-actions"><span class="muted">评测类型：{{ info.label }}</span></div></div>
        <div class="work-grid">
          <section class="work-panel">
            <div class="panel-tabs"><button :class="{active:tab==='statement'}" @click="tab='statement'"><BookOpen :size="16" />题目说明</button><button :class="{active:tab==='code'}" @click="tab='code'"><Code2 :size="17" />编写代码</button><span class="push file-label">{{ tab==='code'?'main.cpp':'README.md' }}</span></div>
            <div v-if="tab==='statement'" class="statement-area"><div class="mode-callout" :style="{'--mode':info.color}"><span class="callout-icon"><Layers :size="19" /></span><div><strong>{{ info.label }}</strong><p>{{ info.description }}</p></div></div><article class="markdown" v-html="rendered"></article><div v-if="selected.protocol" class="protocol-note"><strong>读写约定</strong><p>{{ selected.protocol }}</p></div><div class="sample-grid" v-if="selected.tests[0] && (selected.tests[0].input || selected.tests[0].answer)"><div><div class="sample-label">{{ selected.mode === 'Multiprocess' ? '测试数据（仅调度器读取）' : '示例输入' }}</div><pre>{{ selected.tests[0].input || '（由评测程序提供）' }}</pre></div><div><div class="sample-label">{{ selected.mode === 'Multiprocess' ? '参考判定（仅调度器读取）' : '参考答案' }}</div><pre>{{ selected.tests[0].answer || '（由判题程序决定）' }}</pre></div></div><div class="statement-footer"><span>{{ latest ? '最近提交：'+statusLabel(latest.status) : '准备好后，切换到代码编辑器开始。' }}</span><button class="button primary" @click="tab='code'">开始编写<ChevronRight :size="16" /></button></div></div>
            <template v-else><div class="editor-toolbar"><span><span class="file-dot"></span>C++23 <span class="muted">/ GNU G++</span></span><div class="toolbar-actions"><button @click="loadExample()">正确示例</button><label><Upload :size="13" />载入 .cpp<input hidden type="file" accept=".cpp,.cc,.cxx" @change="uploadCode" /></label></div></div><CodeEditor v-model="source" /><div class="editor-footer"><span><span class="local-dot"></span>草稿自动保存在本机</span><kbd>UTF-8</kbd></div></template>
            <div class="submit-bar"><div><span class="language-pill">C++23</span><span class="muted">-O2 · 标准输入输出</span></div><button v-if="running" class="button secondary" @click="cancel"><Square :size="14" />取消评测</button><button v-else class="button primary" :disabled="busy || !source.trim() || !selected.tests.length" @click="submit"><Play :size="15" fill="currentColor" />提交评测</button></div>
          </section>
          <aside class="results-panel"><div class="results-heading"><h2><Terminal :size="17" />评测结果</h2><button v-if="submission" class="icon-btn" aria-label="刷新结果" @click="openSubmission(submission.id)"><RefreshCw :size="15" /></button><span v-else class="live-label">LIVE</span></div>
            <template v-if="result && submission"><div class="result-summary" :class="result.status==='AC'?'good':active(result.status)?'waiting':'bad'"><div class="result-icon"><Check v-if="result.status==='AC'" :size="26" /><RefreshCw v-else-if="active(result.status)" :size="24" class="spin" /><X v-else :size="25" /></div><strong>{{ statusLabel(result.status) }}</strong><span class="result-code">{{ result.status }}</span><p>{{ result.message || '编译并逐个运行测试点，请稍候。' }}</p><small v-if="submission.title!==selected.title">{{ submission.title }}</small></div><div class="run-stats"><div><strong>{{ accepted }}<small> / {{ result.cases.length || selected.tests.length }}</small></strong><span>测试点通过</span></div><div><strong>{{ Math.max(0,...result.cases.map(c=>c.cpuMs)).toFixed(0) }}<small> ms</small></strong><span>最大 CPU 时间</span></div></div><div class="case-list"><button v-for="c in result.cases" :key="c.name" class="case-row" :class="{chosen:expanded===c.name}" @click="toggle(c)"><Check v-if="c.verdict==='AC'" :size="15" class="success-text" /><X v-else :size="15" class="danger-text" /><strong>#{{ c.name }}</strong><span :class="c.verdict==='AC'?'success-text':'danger-text'">{{ c.verdict }}</span><small>{{ c.cpuMs.toFixed(0) }} ms</small><ChevronDown :size="14" /></button></div><details class="compile-details" :open="result.status==='CE'"><summary>编译日志 <ChevronDown :size="13" /></summary><pre>{{ result.compileLog || '等待编译输出…' }}</pre></details><div class="snapshot-button"><button class="text-link" @click="showSnapshot=true"><Code2 :size="13" />查看本次提交源码</button></div><div class="result-tools"><a :href="'/api/submissions/'+submission.id+'/logs'" target="_blank" class="text-link">原始日志<ArrowUpRight :size="13" /></a></div><p class="compiler-note">{{ result.compilerVersion }}<br>{{ result.compileArgs }}</p></template>
            <div v-else class="empty-results"><div class="empty-terminal"><span>›_</span><i></i><i></i><i></i></div><h3>等待你的第一次提交</h3><p>运行真实的 C++23 程序，<br>在这里查看每个测试点的结果。</p><div class="empty-steps"><span><b>01</b> 编写或上传代码</span><span><b>02</b> 提交到在线评测</span><span><b>03</b> 查看判定结果</span></div></div>
            <div class="resource-note"><span class="local-dot"></span>独立进程 · 限额运行<p>内存按{{ environment?.memoryMetric ?? '提交内存' }}统计；多轮与多进程均取最大值。</p></div>
          </aside>
        </div>
        <footer class="workspace-footer"><span>ACM ONLINE JUDGE</span><span>五种评测模式，一个在线工作台。</span><span>仅用于可信代码训练</span></footer>
      </main>
      <main v-else class="empty-state"><h1>选择一道题开始</h1><p>从左侧题目列表中选择，编写代码并提交评测。</p></main>
    </div>

    <div v-if="currentCase" class="modal-backdrop"><section class="modal logs-modal" role="dialog" aria-modal="true" aria-label="测试点详情"><header class="modal-header"><div><span class="eyebrow">TEST CASE / {{ currentCase.name }}</span><h2>{{ currentCase.verdict }} · {{ currentCase.message }}</h2></div><button class="icon-btn" aria-label="关闭详情" @click="expanded=undefined"><X :size="21" /></button></header><div class="modal-body"><div v-for="instance in currentCase.instances" class="instance-detail"><h3>第 {{ instance.round }} 轮 · 进程 {{ instance.id }} <span class="verdict">{{ instance.run.verdict }}</span></h3><p class="muted">CPU {{ instance.run.usage.cpuMs.toFixed(2) }} ms · 墙钟 {{ instance.run.usage.wallMs.toFixed(2) }} ms · {{ memory(instance.run.usage.memoryBytes) }} · 退出码 {{ instance.run.exitCode }}</p><div class="sample-grid"><div><div class="sample-label">stdout</div><pre>{{ instance.run.stdout || '（无输出）' }}</pre></div><div><div class="sample-label">stderr</div><pre>{{ instance.run.stderr || '（无输出）' }}</pre></div></div></div><h3 v-if="currentCase.trace.length">通信时间线 <span class="count-pill">{{ currentCase.trace.length }}</span></h3><div class="trace-line" v-for="(t,i) in currentCase.trace.slice(0,500)" :key="i"><span>{{ t.ms.toFixed(1) }} ms</span><strong>{{ t.from }} → {{ t.to }}</strong><pre>{{ decode(t.dataBase64) }}</pre></div><p v-if="currentCase.trace.length>500" class="muted">页面展示前 500 段，其余内容请查看原始日志。</p></div></section></div>

    <div v-if="showSnapshot" class="modal-backdrop"><section class="modal" role="dialog" aria-modal="true" aria-label="提交源码"><header class="modal-header"><h2>本次提交源码 · {{ submission?.title }}</h2><button class="icon-btn" aria-label="关闭源码" @click="showSnapshot=false"><X :size="20" /></button></header><CodeEditor v-model="snapshotSource" read-only /></section></div>
    <div v-if="needsToken" class="modal-backdrop"><section class="modal small-modal" role="dialog" aria-modal="true" aria-label="访问令牌"><header class="modal-header"><h2>输入访问令牌</h2><button class="icon-btn" aria-label="关闭" @click="needsToken=false"><X :size="20" /></button></header><div class="modal-body"><div v-if="error" class="alert">{{ error }}</div><p class="muted">这台测评机的 API 需要访问令牌。请向管理员索取启动日志中打印的令牌。</p><label>访问令牌<input v-model="token" type="password" autocomplete="off" @keyup.enter="login" /></label></div><footer class="modal-footer"><button class="button primary" :disabled="busy || !token.trim()" @click="login"><Check :size="16" />登录</button></footer></section></div>
  </div>
</template>
