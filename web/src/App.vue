<script setup lang="ts">
import { computed, defineAsyncComponent, onMounted, onBeforeUnmount, ref, watch } from 'vue';
import { Plus, Search, Upload, Download, Play, Square, Settings2, ChevronRight, Code2, BookOpen, History, Check, X, FileCode2, Terminal, ArrowUpRight, RefreshCw, Pencil, Trash2, FlaskConical, Layers, AlertCircle, ChevronDown } from 'lucide-vue-next';
import { marked } from 'marked';
import DOMPurify from 'dompurify';
import FlowEditor from './FlowEditor.vue';
import FlowPreview from './FlowPreview.vue';
import { newFlow } from './flow';
const CodeEditor = defineAsyncComponent(() => import('./CodeEditor.vue'));
import { modes, modeInfo, statusLabel, active, memory, blankProblem, type Problem, type ProblemSummary, type Submission, type Result, type Case } from './types';

const problems = ref<ProblemSummary[]>([]), selected = ref<Problem>(), search = ref(''), filter = ref('all');
const source = ref('// C++23\n#include <bits/stdc++.h>\nusing namespace std;\n\nint main() {\n    ios::sync_with_stdio(false);\n    cin.tie(nullptr);\n\n    return 0;\n}\n');
const tab = ref('statement'), history = ref<Submission[]>([]), submission = ref<Submission>(), result = ref<Result>();
const busy = ref(false), error = ref(''), notice = ref(''), showHistory = ref(false), expanded = ref<string>();
const snapshotSource = ref(''), showSnapshot = ref(false);
const draft = ref<Problem>(), editTab = ref('basic'), judgeTab = ref<'checker'|'interactor'|'manager'>('checker');
const importPreview = ref<Problem>(), environment = ref<{ compiler: string; compilerExists: boolean; workerReady: boolean }>(), settings = ref(false), compiler = ref('');
let events: EventSource | undefined;
const visible = computed(() => problems.value.filter(p => p.title.toLowerCase().includes(search.value.toLowerCase()) && (filter.value === 'all' || p.mode === filter.value)));
const info = computed(() => selected.value ? modeInfo(selected.value.mode) : modes[0]);
const rendered = computed(() => DOMPurify.sanitize(marked.parse(selected.value?.statement || '') as string));
const running = computed(() => submission.value && active(submission.value.status));
const accepted = computed(() => result.value?.cases.filter(c => c.verdict === 'AC').length ?? 0);
const latest = computed(() => selected.value ? history.value.find(s => s.problemId === selected.value!.id) : undefined);
const currentCase = computed(() => result.value?.cases.find(c => c.name === expanded.value));
const decode = (value: string) => new TextDecoder().decode(Uint8Array.from(atob(value), c => c.charCodeAt(0)));
async function api<T>(url: string, init?: RequestInit): Promise<T> {
  const response = await fetch('/api' + url, { ...init, headers: init?.body instanceof FormData ? init.headers : { 'Content-Type': 'application/json', ...init?.headers } });
  if (!response.ok) { const body = await response.json().catch(() => ({})); throw new Error(body.error || `请求失败 (${response.status})`); }
  return response.status === 204 || response.headers.get('content-length') === '0' ? undefined as T : response.json();
}
async function perform(fn: () => Promise<void>) { error.value = ''; busy.value = true; try { await fn(); } catch (e) { error.value = e instanceof Error ? e.message : String(e); } finally { busy.value = false; } }
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
watch([draft, source], () => { notice.value = ''; });
function protect(e: BeforeUnloadEvent) { if (draft.value || running.value) { e.preventDefault(); e.returnValue = ''; } }
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
    if (!selected.value) selected.value = blankProblem();
    snapshotSource.value = data.source;
    submission.value = data.submission; result.value = data.result; showHistory.value = false; expanded.value = undefined;
    if (active(data.submission.status)) connect(id); else events?.close();
  });
}
async function cancel() { if (submission.value) await perform(async () => { await api('/submissions/' + submission.value!.id + '/cancel', { method: 'POST' }); await loadHistory(); }); }
async function rejudge() { if (submission.value) await perform(async () => { submission.value = await api('/submissions/' + submission.value!.id + '/rejudge', {method:'POST'}); connect(submission.value!.id); }); }
function edit(p?: Problem) { draft.value = p ? JSON.parse(JSON.stringify(p)) : blankProblem(); editTab.value = 'basic'; }
function openFlow() { if (draft.value) { draft.value.flow ??= newFlow(draft.value.processCount); editTab.value = 'flow'; } }
watch(() => draft.value?.mode, mode => { if (mode !== 'Multiprocess' && draft.value?.flow) draft.value.flow.enabled = false; });
function closeEdit() { if (confirm('关闭编辑？尚未保存的题目修改将丢失。')) draft.value = undefined; }
async function save() {
  if (!draft.value) return;
  await perform(async () => {
    const p = await api<Problem>('/problems' + (draft.value!.id ? '/' + draft.value!.id : ''), { method: draft.value!.id ? 'PUT' : 'POST', body: JSON.stringify(draft.value) });
    draft.value = undefined; await loadList(); await select(p.id); notice.value = '题目已保存';
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
  await perform(async () => { const p = await api<Problem>('/problems', {method:'POST', body:JSON.stringify(importPreview.value)}); importPreview.value = undefined; await loadList(); await select(p.id); notice.value = '题目包已导入为新题目'; });
}
async function uploadTests(event: Event) {
  const input = event.target as HTMLInputElement; const files = [...(input.files ?? [])]; if (!draft.value) return;
  await perform(async () => {
    const map = new Map<string, { name: string; text: string }>();
    for (const f of files) {
      if (!/\.(in|ans)$/i.test(f.name)) throw new Error('请选择 .in 和 .ans 文件');
      const key = f.name.toLowerCase(); if (map.has(key)) throw new Error('文件重名：' + f.name);
      map.set(key, { name: f.name, text: await f.text() });
    }
    const next = JSON.parse(JSON.stringify(draft.value!.tests)) as typeof draft.value.tests;
    for (const [key, f] of map) {
      if (key.endsWith('.ans') && !map.has(key.slice(0,-4) + '.in')) throw new Error('答案缺少对应输入：' + f.name);
      if (!key.endsWith('.in')) continue;
      const name = f.name.slice(0,-3), answer = map.get(key.slice(0,-3) + '.ans');
      if (!answer && ['Standard','Special','Communication'].includes(draft.value!.mode)) throw new Error('缺少 ' + name + '.ans');
      const test = { name, input: f.text, answer: answer?.text ?? '' }, index = next.findIndex(t => t.name.toLowerCase() === name.toLowerCase());
      if (index >= 0) next[index] = test; else next.push(test);
    }
    draft.value!.tests = next;
  }); input.value = '';
}
async function uploadCode(event: Event) { const f = (event.target as HTMLInputElement).files?.[0]; if (f) source.value = await f.text(); (event.target as HTMLInputElement).value = ''; }
function loadExample(wrong = false) { if (selected.value && confirm('用示例代码替换当前编辑器内容？')) source.value = wrong ? selected.value.wrongSource : selected.value.exampleSource; }
async function openSettings() { await perform(async () => { environment.value = await api('/environment'); compiler.value = environment.value!.compiler; settings.value = true; }); }
async function saveSettings() { await perform(async () => { await api('/settings', {method:'PUT', body:JSON.stringify({compiler: compiler.value})}); settings.value = false; environment.value = await api('/environment'); }); }
function toggle(c: Case) { expanded.value = expanded.value === c.name ? undefined : c.name; }
onMounted(async () => { addEventListener('beforeunload', protect); await perform(async () => { await Promise.all([loadList(), loadHistory(), api<typeof environment.value>('/environment').then(x => environment.value=x)]); }); if (problems.value[0]) await select(problems.value[0].id); });
onBeforeUnmount(() => { events?.close(); removeEventListener('beforeunload', protect); });
</script>

<template>
  <div class="app-shell">
    <aside class="sidebar">
      <a class="brand" href="/" aria-label="ACM 测评机首页"><span class="brand-mark"><Terminal :size="21" /></span><span>ACM<span class="brand-sub">LOCAL JUDGE</span></span><span class="version">01</span></a>
      <div class="side-label">你的训练空间<span class="local-dot"></span></div>
      <button class="nav-item selected" @click="showHistory=false"><Layers :size="17" /> 题目工作台 <span>{{ problems.length }}</span></button>
      <button class="nav-item" @click="showHistory=true; loadHistory()"><History :size="17" /> 提交记录 <ArrowUpRight :size="14" class="push" /></button>
      <div class="library-title"><span>本地题库</span><button class="icon-btn" aria-label="新建题目" @click="edit()"><Plus :size="17" /></button></div>
      <div class="search-box"><Search :size="15" /><input v-model="search" placeholder="搜索题目…" aria-label="搜索题目" /></div>
      <select v-model="filter" class="filter" aria-label="评测类型筛选"><option value="all">全部评测类型</option><option v-for="m in modes" :value="m.value">{{ m.label }}</option></select>
      <div class="problem-list">
        <button v-for="(p,i) in visible" :key="p.id" class="problem-link" :class="{current:selected?.id===p.id}" @click="select(p.id)">
          <span class="problem-index">{{ String(i+1).padStart(2,'0') }}</span><span class="problem-link-content"><strong>{{ p.title }}</strong><span><i :style="{background:modeInfo(p.mode).color}"></i>{{ modeInfo(p.mode).label }}<span class="test-count">{{ p.testCount }} 点</span></span></span><ChevronRight v-if="selected?.id===p.id" :size="14" />
        </button>
        <p v-if="!visible.length" class="muted empty-small">没有匹配的题目</p>
      </div>
      <div class="sidebar-bottom"><label class="side-import"><Upload :size="16" /> 导入题目包<input type="file" accept=".zip" hidden @change="importZip" /></label><button class="nav-item" @click="openSettings"><Settings2 :size="16" /> 运行环境</button><p><span class="local-dot"></span> 数据保存在这台电脑上</p></div>
    </aside>

    <div class="main-shell">
      <header class="topbar"><div class="breadcrumb">工作空间<ChevronRight :size="12" /><b>{{ showHistory ? '提交记录' : '题目工作台' }}</b></div><div class="runtime-tag"><span class="local-dot"></span>Windows 原生<span class="separator">/</span><strong>C++23</strong></div></header>
      <div v-if="error" class="alert" role="alert"><AlertCircle :size="17" />{{ error }}<button class="icon-btn push" aria-label="关闭错误" @click="error='' "><X :size="16" /></button></div>
      <div v-if="notice" class="notice" role="status"><Check :size="16" />{{ notice }}</div>

      <main v-if="showHistory" class="history-page"><div class="page-heading"><div><div class="eyebrow">SUBMISSIONS</div><h1>每一次运行，都有记录。</h1><p class="muted">保留源码、题目快照与逐测试点评测结果。</p></div><button class="button secondary" @click="loadHistory"><RefreshCw :size="15" />刷新</button></div><div class="table-wrap"><table><thead><tr><th>状态</th><th>题目</th><th>类型</th><th>提交时间</th><th></th></tr></thead><tbody><tr v-for="s in history" :key="s.id" @click="openSubmission(s.id)"><td><span class="verdict" :class="s.status==='AC'?'success':active(s.status)?'pending':'failed'">{{ s.status }}</span></td><td>{{ s.title }}</td><td>{{ modeInfo(s.mode).label }}</td><td class="muted">{{ new Date(s.createdAt).toLocaleString('zh-CN') }}</td><td><ArrowUpRight :size="16" /></td></tr></tbody></table><div v-if="!history.length" class="empty-state">还没有提交记录。选择一道题，开始第一次评测。</div></div></main>

      <main v-else-if="selected" class="workspace">
        <div class="page-heading"><div><div class="eyebrow">PROBLEM WORKSPACE <span class="tiny-rule"></span> LOCAL / {{ info.short }}</div><h1>{{ selected.title }}</h1><div class="problem-meta"><span class="mode-badge" :style="{color:info.color,background:info.color+'12'}">{{ info.label }}</span><span>{{ selected.limits.cpuMs }} ms</span><span>{{ selected.limits.memoryMiB }} MiB</span><span>{{ selected.tests.length }} 个测试点</span></div></div><div class="heading-actions"><button class="button secondary" @click="edit(selected)"><Pencil :size="15" />编辑题目</button><a class="icon-btn framed" :href="'/api/problems/'+selected.id+'/export'" aria-label="导出题目包" title="导出题目包"><Download :size="17" /></a><button class="icon-btn framed danger-text" aria-label="删除题目" title="删除题目" @click="remove"><Trash2 :size="16" /></button></div></div>
        <div class="work-grid">
          <section class="work-panel">
            <div class="panel-tabs"><button :class="{active:tab==='statement'}" @click="tab='statement'"><BookOpen :size="16" />题目说明</button><button :class="{active:tab==='code'}" @click="tab='code'"><Code2 :size="17" />编写代码</button><span class="push file-label">{{ tab==='code'?'main.cpp':'README.md' }}</span></div>
            <div v-if="tab==='statement'" class="statement-area"><div class="mode-callout" :style="{'--mode':info.color}"><span class="callout-icon"><FlaskConical :size="19" /></span><div><strong>{{ info.label }}</strong><p>{{ info.description }}</p></div></div><article class="markdown" v-html="rendered"></article><FlowPreview v-if="selected.mode==='Multiprocess' && selected.flow" :flow="selected.flow" :test="selected.tests[0]" /><div v-if="selected.protocol" class="protocol-note"><strong>读写约定</strong><p>{{ selected.protocol }}</p></div><div class="sample-grid" v-if="selected.tests[0]"><div><div class="sample-label">{{ selected.mode === 'Multiprocess' ? '测试数据（仅调度器读取）' : '示例输入' }}</div><pre>{{ selected.tests[0].input || '（由评测程序提供）' }}</pre></div><div><div class="sample-label">{{ selected.mode === 'Multiprocess' ? '参考判定（仅调度器读取）' : '参考答案' }}</div><pre>{{ selected.tests[0].answer || '（由判题程序决定）' }}</pre></div></div><div class="statement-footer"><span>{{ latest ? '最近提交：'+statusLabel(latest.status) : '准备好后，切换到代码编辑器开始。' }}</span><button class="button primary" @click="tab='code'">开始编写<ChevronRight :size="16" /></button></div></div>
            <template v-else><div class="editor-toolbar"><span><span class="file-dot"></span>C++23 <span class="muted">/ GNU G++</span></span><div class="toolbar-actions"><button @click="loadExample()">正确示例</button><button @click="loadExample(true)">错误示例</button><label><Upload :size="13" />载入 .cpp<input hidden type="file" accept=".cpp,.cc,.cxx" @change="uploadCode" /></label></div></div><CodeEditor v-model="source" /><div class="editor-footer"><span><span class="local-dot"></span>草稿自动保存在本机</span><kbd>UTF-8</kbd></div></template>
            <div class="submit-bar"><div><span class="language-pill">C++23</span><span class="muted">-O2 · 标准输入输出</span></div><button v-if="running" class="button secondary" @click="cancel"><Square :size="14" />取消评测</button><button v-else class="button primary" :disabled="busy || !source.trim() || !selected.tests.length" @click="submit"><Play :size="15" fill="currentColor" />提交评测</button></div>
          </section>
          <aside class="results-panel"><div class="results-heading"><h2><Terminal :size="17" />评测结果</h2><button v-if="submission" class="icon-btn" aria-label="刷新结果" @click="openSubmission(submission.id)"><RefreshCw :size="15" /></button><span v-else class="live-label">LIVE</span></div>
            <template v-if="result && submission"><div class="result-summary" :class="result.status==='AC'?'good':active(result.status)?'waiting':'bad'"><div class="result-icon"><Check v-if="result.status==='AC'" :size="26" /><RefreshCw v-else-if="active(result.status)" :size="24" class="spin" /><X v-else :size="25" /></div><strong>{{ statusLabel(result.status) }}</strong><span class="result-code">{{ result.status }}</span><p>{{ result.message || '编译并逐个运行测试点，请稍候。' }}</p><small v-if="submission.title!==selected.title">{{ submission.title }}</small></div><div class="run-stats"><div><strong>{{ accepted }}<small> / {{ result.cases.length || selected.tests.length }}</small></strong><span>测试点通过</span></div><div><strong>{{ Math.max(0,...result.cases.map(c=>c.cpuMs)).toFixed(0) }}<small> ms</small></strong><span>最大 CPU 时间</span></div></div><div class="case-list"><button v-for="c in result.cases" :key="c.name" class="case-row" :class="{chosen:expanded===c.name}" @click="toggle(c)"><Check v-if="c.verdict==='AC'" :size="15" class="success-text" /><X v-else :size="15" class="danger-text" /><strong>#{{ c.name }}</strong><span :class="c.verdict==='AC'?'success-text':'danger-text'">{{ c.verdict }}</span><small>{{ c.cpuMs.toFixed(0) }} ms</small><ChevronDown :size="14" /></button></div><details class="compile-details" :open="result.status==='CE'"><summary>编译日志 <ChevronDown :size="13" /></summary><pre>{{ result.compileLog || '等待编译输出…' }}</pre></details><div class="snapshot-button"><button class="text-link" @click="showSnapshot=true"><Code2 :size="13" />查看本次提交源码</button></div><div class="result-tools"><button class="button secondary" :disabled="!!running" @click="rejudge"><RefreshCw :size="14" />按原快照重评</button><a :href="'/api/submissions/'+submission.id+'/logs'" target="_blank" class="text-link">原始日志<ArrowUpRight :size="13" /></a></div><p class="compiler-note">{{ result.compilerVersion }}<br>{{ result.compileArgs }}</p></template>
            <div v-else class="empty-results"><div class="empty-terminal"><span>›_</span><i></i><i></i><i></i></div><h3>等待你的第一次提交</h3><p>运行真实的 C++23 程序，<br>在这里查看每个测试点的结果。</p><div class="empty-steps"><span><b>01</b> 编写或上传代码</span><span><b>02</b> 提交到本地运行器</span><span><b>03</b> 查看判定与通信日志</span></div></div>
            <div class="resource-note"><span class="local-dot"></span>独立进程 · 本地运行<p>内存按 Windows 提交内存统计；多轮与多进程均取最大值。</p></div>
          </aside>
        </div>
        <footer class="workspace-footer"><span>ACM LOCAL JUDGE</span><span>五种评测模式，一个本地工作台。</span><span>仅用于可信代码训练</span></footer>
      </main>
      <main v-else class="empty-state"><h1>建立你的第一道题</h1><p>添加题面和测试数据，开始本地评测。</p><button class="button primary" @click="edit()"><Plus :size="16" />新建题目</button></main>
    </div>

    <div v-if="draft" class="modal-backdrop"><section class="modal edit-modal" role="dialog" aria-modal="true" aria-labelledby="edit-title"><header class="modal-header"><div><span class="eyebrow">PROBLEM BUILDER</span><h2 id="edit-title">{{ draft.id?'编辑题目':'新建题目' }}</h2></div><button class="icon-btn" aria-label="关闭编辑" @click="closeEdit"><X :size="21" /></button></header><div v-if="error" class="alert" role="alert">{{ error }}</div><div class="panel-tabs"><button v-for="t in [{id:'basic',name:'题面与限制'},{id:'tests',name:'测试数据'},{id:'judge',name:'判题程序'}]" :class="{active:editTab===t.id}" @click="editTab=t.id">{{ t.name }}</button><button v-if="draft.mode==='Multiprocess'" :class="{active:editTab==='flow'}" @click="openFlow">成员与通信</button></div><div class="modal-body">
      <div v-if="editTab==='basic'" class="form-grid"><label class="span-2">题目标题<input v-model="draft.title" placeholder="例如：A + B" /></label><label>评测类型<select v-model="draft.mode"><option v-for="m in modes" :value="m.value">{{ m.label }}</option></select></label><label>进程数（多进程题）<input type="number" v-model.number="draft.processCount" min="2" max="16" :disabled="!!draft.flow" /><small v-if="draft.flow">在「成员与通信」中添加或删除成员</small></label><label>CPU 时间 / ms<input type="number" v-model.number="draft.limits.cpuMs" /></label><label>墙钟时间 / ms<input type="number" v-model.number="draft.limits.wallMs" /></label><label>内存 / MiB<input type="number" v-model.number="draft.limits.memoryMiB" /></label><label>输出上限 / KiB<input type="number" v-model.number="draft.limits.outputKiB" /></label><label class="span-2">题目描述 · Markdown<textarea v-model="draft.statement" rows="9"></textarea></label><label class="span-2">读写协议<textarea v-model="draft.protocol" rows="2" placeholder="说明选手程序的输入输出约定"></textarea></label><template v-if="draft.mode==='Communication'"><label>第一轮标记<input v-model="draft.firstMarker" /></label><label>第二轮标记<input v-model="draft.secondMarker" /></label><label class="checkbox span-2"><input type="checkbox" v-model="draft.managedCommunication" />使用 manager 驱动双轮交互</label></template></div>
      <div v-else-if="editTab==='tests'"><div class="section-toolbar"><div><h3>测试数据 <span class="count-pill">{{ draft.tests.length }}</span></h3><p class="muted">同名 .in 与 .ans 配对；上传后预览，保存题目时生效。</p></div><label class="button secondary"><Upload :size="15" />批量载入<input hidden multiple type="file" accept=".in,.ans" @change="uploadTests" /></label><button class="button secondary" @click="draft.tests.push({name:String(draft.tests.length+1).padStart(2,'0'),input:'',answer:''})"><Plus :size="15" />添加</button></div><div v-for="(t,i) in draft.tests" :key="i" class="test-editor"><div class="test-editor-head"><label>测试点 <input v-model="t.name" aria-label="测试点名称" /></label><span class="paired"><Check :size="13" />输入 / 答案</span><button class="icon-btn" aria-label="移除测试点" @click="draft.tests.splice(i,1)"><Trash2 :size="15" /></button></div><div class="sample-grid"><label>输入<textarea v-model="t.input" rows="4"></textarea></label><label>答案<textarea v-model="t.answer" rows="4"></textarea></label></div></div><p v-if="!draft.tests.length" class="empty-state">还没有测试点。批量载入文件，或手动添加。</p></div>
      <FlowEditor v-else-if="editTab==='flow' && draft.flow" v-model="draft.flow" :test="draft.tests[0]" @members-changed="draft.processCount=$event" /><div v-else><p v-if="draft.mode==='Multiprocess' && draft.flow?.enabled" class="notice">已启用可视化通信流程：manager 源码保留，当前不执行。</p><div class="judge-toolbar"><div><h3>判题源码</h3><p class="muted">支持 testlib.h 和 acm_manager.hpp；判题程序资源单独统计。</p></div><select v-model="judgeTab"><option value="checker">checker.cpp</option><option value="interactor">interactor.cpp</option><option value="manager">manager.cpp</option></select></div><div class="judge-editor"><CodeEditor v-model="draft[judgeTab]" /></div><div class="form-grid judge-limits"><label>判题 CPU / ms<input type="number" v-model.number="draft.judgeLimits.cpuMs" /></label><label>判题墙钟 / ms<input type="number" v-model.number="draft.judgeLimits.wallMs" /></label><label>判题内存 / MiB<input type="number" v-model.number="draft.judgeLimits.memoryMiB" /></label><label>判题输出 / KiB<input type="number" v-model.number="draft.judgeLimits.outputKiB" /></label><label class="span-2">正确示例源码<textarea v-model="draft.exampleSource" rows="4"></textarea></label><label class="span-2">错误示例源码<textarea v-model="draft.wrongSource" rows="4"></textarea></label></div></div>
    </div><footer class="modal-footer"><span class="muted">修改不会影响已提交的评测快照。</span><button class="button secondary" @click="closeEdit">取消</button><button class="button primary" :disabled="busy" @click="save"><Check :size="16" />保存题目</button></footer></section></div>

    <div v-if="currentCase" class="modal-backdrop"><section class="modal logs-modal" role="dialog" aria-modal="true" aria-label="测试点详情"><header class="modal-header"><div><span class="eyebrow">TEST CASE / {{ currentCase.name }}</span><h2>{{ currentCase.verdict }} · {{ currentCase.message }}</h2></div><button class="icon-btn" aria-label="关闭详情" @click="expanded=undefined"><X :size="21" /></button></header><div class="modal-body"><div v-for="instance in currentCase.instances" class="instance-detail"><h3>第 {{ instance.round }} 轮 · 进程 {{ instance.id }} <span class="verdict">{{ instance.run.verdict }}</span></h3><p class="muted">CPU {{ instance.run.usage.cpuMs.toFixed(2) }} ms · 墙钟 {{ instance.run.usage.wallMs.toFixed(2) }} ms · {{ memory(instance.run.usage.memoryBytes) }} · 退出码 {{ instance.run.exitCode }}</p><div class="sample-grid"><div><div class="sample-label">stdout</div><pre>{{ instance.run.stdout || '（无输出）' }}</pre></div><div><div class="sample-label">stderr</div><pre>{{ instance.run.stderr || '（无输出）' }}</pre></div></div></div><h3 v-if="currentCase.trace.length">通信时间线 <span class="count-pill">{{ currentCase.trace.length }}</span></h3><div class="trace-line" v-for="(t,i) in currentCase.trace.slice(0,500)" :key="i"><span>{{ t.ms.toFixed(1) }} ms</span><strong>{{ t.from }} → {{ t.to }}</strong><pre>{{ decode(t.dataBase64) }}</pre></div><p v-if="currentCase.trace.length>500" class="muted">页面展示前 500 段，其余内容请查看原始日志。</p></div></section></div>

    <div v-if="importPreview" class="modal-backdrop"><section class="modal small-modal" role="dialog" aria-modal="true" aria-label="导入题目包"><header class="modal-header"><h2>题目包预览</h2><button class="icon-btn" aria-label="关闭导入" @click="importPreview=undefined"><X :size="20" /></button></header><div class="modal-body"><div v-if="error" class="alert">{{ error }}</div><h3>{{ importPreview.title }}</h3><p>{{ modeInfo(importPreview.mode).label }} · {{ importPreview.tests.length }} 个测试点</p><p class="muted">校验完成。将创建新题目，现有题目不会被覆盖。</p><div class="import-files"><span v-for="t in importPreview.tests"><FileCode2 :size="14" />{{ t.name }}.in / .ans</span></div></div><footer class="modal-footer"><button class="button primary" :disabled="busy" @click="confirmImport"><Upload :size="15" />导入为新题目</button></footer></section></div>

    <div v-if="showSnapshot" class="modal-backdrop"><section class="modal" role="dialog" aria-modal="true" aria-label="提交源码"><header class="modal-header"><h2>本次提交源码 · {{ submission?.title }}</h2><button class="icon-btn" aria-label="关闭源码" @click="showSnapshot=false"><X :size="20" /></button></header><CodeEditor v-model="snapshotSource" read-only /></section></div>
    <div v-if="settings" class="modal-backdrop"><section class="modal small-modal" role="dialog" aria-modal="true" aria-label="运行环境"><header class="modal-header"><h2>运行环境</h2><button class="icon-btn" aria-label="关闭设置" @click="settings=false"><X :size="20" /></button></header><div class="modal-body"><div v-if="error" class="alert">{{ error }}</div><div class="environment-status"><span class="local-dot"></span>Windows 原生 · C++23</div><p>编译器 {{ environment?.compilerExists?'已找到':'未找到' }} · Worker {{ environment?.workerReady?'就绪':'需要构建' }}</p><label>G++ 编译器完整路径<input v-model="compiler" /></label><p class="muted">默认参数：-std=c++23 -O2 -pipe</p><div class="protocol-note">仅用于本机可信代码。进程资源限制不等于文件和网络隔离。</div></div><footer class="modal-footer"><button class="button primary" @click="saveSettings" :disabled="busy">保存设置</button></footer></section></div>
  </div>
</template>
