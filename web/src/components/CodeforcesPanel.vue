<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue';
import { api, currentUser } from '../client';
import { cfRankColor, type UserInfo } from '../types';

interface Challenge { handle: string; code: string; minutes: number; expiresAt: string }
interface Entry { id: number; contestId: number | null; creationTimeSeconds: number; verdict: string | null; programmingLanguage: string; problem: { contestId: number | null; index: string; name: string; rating: number | null; key: string } }
interface History { handle: string; updatedAt: string | null; submissionCount: number; solvedCount: number; page: number; pageCount: number; items: Entry[]; sync: { status: string; fetched: number; message: string }; user: UserInfo }
const emit = defineEmits<{ changed: [] }>();
const owner = currentUser.value?.id;
const controller = new AbortController();
const valid = () => !controller.signal.aborted && currentUser.value?.id === owner;
const request = <T,>(url: string, init?: RequestInit) => api<T>('/users/me/codeforces' + url, { ...init, signal: controller.signal });
const handle = ref(''), challenge = ref<Challenge>(), history = ref<History>();
const busy = ref(false), verifying = ref(false), message = ref(''), error = ref('');
const view = ref<'submissions' | 'solved'>('submissions'), page = ref(1);
let verificationTimer: ReturnType<typeof setTimeout> | undefined, historyTimer: ReturnType<typeof setTimeout> | undefined;
let verificationGeneration = 0;
let historyGeneration = 0;
const syncing = computed(() => ['queued', 'running'].includes(history.value?.sync.status ?? ''));
const me = computed(() => currentUser.value);
const fmt = (date: string) => new Date(date).toLocaleString('zh-CN');
const submissionUrl = (row: Entry) => row.contestId ? `https://codeforces.com/${row.contestId >= 100000 ? 'gym' : 'contest'}/${row.contestId}/submission/${row.id}` : `https://codeforces.com/submissions/${encodeURIComponent(history.value?.handle ?? '')}`;
const verdict = (value: string | null) => ({ OK: '通过', WRONG_ANSWER: '答案错误', TIME_LIMIT_EXCEEDED: '超时', MEMORY_LIMIT_EXCEEDED: '超内存', RUNTIME_ERROR: '运行错误', COMPILATION_ERROR: '编译错误', TESTING: '评测中' }[value ?? 'TESTING'] ?? value);
function report(e: unknown) { if (valid()) error.value = e instanceof Error ? e.message : String(e); }
async function loadHistory() {
  clearTimeout(historyTimer);
  const generation = ++historyGeneration;
  const data = await request<History>(`/history?page=${page.value}&view=${view.value}`);
  if (!valid() || generation !== historyGeneration) return;
  error.value = '';
  history.value = data; page.value = data.page; currentUser.value = data.user;
  if (['queued', 'running'].includes(data.sync.status)) pollHistory(2000);
}
function pollHistory(delay: number) {
  historyTimer = setTimeout(() => { loadHistory().catch(e => { report(e); if (valid()) pollHistory(5000); }); }, delay);
}
async function sync() {
  busy.value = true; error.value = '';
  try { await request('/sync', { method: 'POST' }); await loadHistory(); }
  catch (e) { report(e); } finally { if (valid()) busy.value = false; }
}
async function issue() {
  busy.value = true; error.value = ''; message.value = '';
  try {
    await request('', { method: 'POST', body: JSON.stringify({ handle: handle.value.trim() }) });
    const data = await request<{ challenge: Challenge }>('/challenge');
    if (valid()) challenge.value = data.challenge;
  } catch (e) { report(e); } finally { if (valid()) busy.value = false; }
}
function stopVerification() { verificationGeneration++; clearTimeout(verificationTimer); verifying.value = false; }
async function verify(generation = ++verificationGeneration) {
  if (!challenge.value || !valid() || generation !== verificationGeneration) return;
  if (Date.now() >= Date.parse(challenge.value.expiresAt)) { stopVerification(); error.value = '验证码已过期，请重新获取'; challenge.value = undefined; return; }
  verifying.value = true; error.value = ''; message.value = '正在查询 CF 最新公开资料…';
  try {
    const data = await request<{ status: string; user?: UserInfo; message?: string; retryAfterSeconds?: number }>('/verify', { method: 'POST', body: JSON.stringify({ code: challenge.value.code }) });
    if (!valid() || generation !== verificationGeneration) return;
    if (data.user) {
      currentUser.value = data.user; challenge.value = undefined; stopVerification();
      message.value = '绑定成功，可以恢复 Last name。'; emit('changed'); await loadHistory().catch(report);
    } else {
      message.value = data.message ?? 'CF 资料尚未更新';
      verificationTimer = setTimeout(() => { void verify(generation); }, Math.max(10, data.retryAfterSeconds ?? 10) * 1000);
    }
  } catch (e) { if (valid() && generation === verificationGeneration) { stopVerification(); report(e); message.value = '验证码保持有效；连接恢复后可以再次验证。'; } }
}
async function unbind() {
  busy.value = true; error.value = ''; stopVerification(); clearTimeout(historyTimer);
  historyGeneration++;
  try {
    const data = await request<{ user: UserInfo }>('', { method: 'DELETE' });
    if (valid()) { currentUser.value = data.user; history.value = undefined; challenge.value = undefined; message.value = '已解除绑定并清除本地 CF 记录'; emit('changed'); }
  } catch (e) { report(e); } finally { if (valid()) busy.value = false; }
}
async function navigate(nextView: 'submissions' | 'solved', nextPage = 1) {
  if (busy.value) return;
  busy.value = true; view.value = nextView; page.value = nextPage; error.value = '';
  try { await loadHistory(); } catch (e) { report(e); } finally { if (valid()) busy.value = false; }
}
onMounted(async () => {
  busy.value = true;
  try {
    await loadHistory();
    if (!valid()) return;
    if (!me.value?.cfHandle) {
      const data = await request<{ challenge?: Challenge }>('/challenge');
      if (valid() && data.challenge) { challenge.value = data.challenge; handle.value = data.challenge.handle; }
    } else if (!history.value?.updatedAt && history.value?.sync.status === 'idle') await sync();
  } catch (e) { report(e); } finally { if (valid()) busy.value = false; }
});
onBeforeUnmount(() => { controller.abort(); stopVerification(); clearTimeout(historyTimer); });
</script>

<template>
  <section class="cf-panel" aria-label="Codeforces 绑定与记录">
    <h3 class="section-label">Codeforces 绑定与记录</h3>
    <p v-if="error" class="cf-error" role="alert">{{ error }}</p>
    <p v-if="message" class="cf-message" role="status">{{ message }}</p>
    <template v-if="!me?.cfHandle">
      <p class="muted">验证账号归属后，同步 rating、公开提交历史与已通过题目。</p>
      <label>CF handle<input v-model="handle" :disabled="!!challenge || busy" placeholder="如 tourist" /></label>
      <button v-if="!challenge" class="button secondary" :disabled="busy || !handle.trim()" @click="issue">获取验证码</button>
      <div v-else class="cf-challenge">
        <p>1. 登录 <a href="https://codeforces.com/settings/social" target="_blank" rel="noopener noreferrer">Codeforces → Settings → Social</a>，把 <b>Last name</b> 临时改为：</p>
        <p><code class="cf-code">{{ challenge.code }}</code> <span class="muted">账号 {{ challenge.handle }} · 有效至 {{ fmt(challenge.expiresAt) }}</span></p>
        <p>2. 保存后开始验证。CF 资料可能延迟更新，每 10 秒自动重试；请保持同一验证码，成功后再改回原值。</p>
        <div class="profile-actions">
          <button class="button primary" :disabled="busy || verifying" @click="verify()">{{ verifying ? '等待 CF 资料更新…' : '我已修改，开始验证' }}</button>
          <button v-if="verifying" class="text-link" @click="stopVerification">暂停重试</button>
          <button v-else class="text-link" :disabled="busy" @click="challenge=undefined; message=''">更换账号 / 重新获取</button>
        </div>
      </div>
    </template>
    <template v-else>
      <div class="cf-bound"><b>{{ me.cfHandle }}</b><span :style="{color:cfRankColor(me.cfRank)}">{{ me.cfRating ?? '未评级' }} {{ me.cfRank }}</span><button class="text-link danger-text" :disabled="busy" @click="unbind">解除绑定</button></div>
      <div class="cf-stats"><div><strong>{{ history?.solvedCount ?? '—' }}</strong><span>CF 已通过题目</span></div><div><strong>{{ history?.submissionCount ?? '—' }}</strong><span>CF 公开提交</span></div><button class="button secondary" :disabled="busy || syncing" @click="sync">{{ syncing ? '同步中…' : '同步 CF 记录' }}</button></div>
      <p class="muted">{{ history?.updatedAt ? '上次成功同步：'+fmt(history.updatedAt) : '尚未完成首次同步' }}。仅统计 CF 公开记录，独立于本机解题榜。</p>
      <p v-if="history?.sync.message" :class="history.sync.status==='failed' ? 'cf-error' : 'cf-message'" role="status">{{ history.sync.message }}<span v-if="syncing"> · 已读取 {{ history.sync.fetched }} 条</span></p>
      <div class="panel-tabs"><button :aria-pressed="view==='submissions'" :class="{active:view==='submissions'}" :disabled="busy" @click="navigate('submissions')">提交记录</button><button :aria-pressed="view==='solved'" :class="{active:view==='solved'}" :disabled="busy" @click="navigate('solved')">已通过题目</button></div>
      <div class="cf-records">
        <article v-for="row in history?.items ?? []" :key="row.id" class="cf-record">
          <div><a :href="submissionUrl(row)" target="_blank" rel="noopener noreferrer">{{ row.problem.contestId ?? '' }}{{ row.problem.index }} · {{ row.problem.name }}</a><small>{{ fmt(new Date(row.creationTimeSeconds*1000).toISOString()) }} · {{ row.programmingLanguage }}</small></div>
          <span :class="{'cf-ac':row.verdict==='OK'}">{{ verdict(row.verdict) }}</span>
        </article>
        <p v-if="!history?.items.length" class="muted">{{ history?.updatedAt ? '暂无对应公开记录' : '同步成功后将在这里显示记录' }}</p>
      </div>
      <div v-if="history && history.pageCount>1" class="cf-pagination"><button class="button secondary" :disabled="busy || page<=1" @click="navigate(view,page-1)">上一页</button><span>{{ page }} / {{ history.pageCount }}</span><button class="button secondary" :disabled="busy || page>=history.pageCount" @click="navigate(view,page+1)">下一页</button></div>
    </template>
  </section>
</template>

<style scoped>
.cf-panel { display:grid; gap:14px; min-width:0; }
.cf-panel p { margin:0; line-height:1.7; }
.cf-panel label { display:grid; gap:7px; }
.cf-message,.cf-error { padding:10px 14px; border-radius:8px; background:#edf4ec; }
.cf-error { background:#fff0eb; color:#a33320; }
.cf-stats { display:flex; align-items:center; flex-wrap:wrap; gap:20px; padding:18px 0; border-bottom:1px solid var(--line,#dce3db); }
.cf-stats div { display:grid; gap:3px; }.cf-stats strong { font-size:28px; }.cf-stats span,.cf-record small { color:var(--muted,#667264); font-size:12px; }
.cf-records { max-height:360px; overflow:auto; }.cf-record { display:flex; align-items:center; justify-content:space-between; gap:12px; padding:12px 0; border-bottom:1px solid var(--line,#dce3db); }.cf-record>div { min-width:0; display:grid; gap:4px; }.cf-record a { overflow-wrap:anywhere; }.cf-record>span { flex-shrink:0; font-size:12px; }.cf-ac { color:#18744d; }.cf-pagination { display:flex; justify-content:space-between; align-items:center; }.cf-bound { flex-wrap:wrap; }.cf-challenge { display:grid; gap:12px; }
</style>
