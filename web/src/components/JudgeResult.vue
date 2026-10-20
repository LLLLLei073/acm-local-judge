<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { Check, X, RefreshCw, Terminal, ArrowRight, Code2 } from 'lucide-vue-next';
import { active, statusLabel, memory, type Result, type Submission, type Limits, type Mode } from '../types';
import { resourcePercent, verdictAdvice } from '../domain/workbench';
import type { ConnectionState } from '../services/submission-stream';
const props = defineProps<{ result?: Result; submission?: Submission; limits?: Limits; total: number; mode?: Mode; title?: string; connection: ConnectionState; sourceVisible?: boolean; memoryMetric?: string }>();
defineEmits<{ inspect: [name: string]; source: []; refresh: [] }>();
const onlyFailed = ref(false);
watch(() => props.submission?.id, () => onlyFailed.value = false);
const cases = computed(() => props.result?.cases ?? []);
const rows = computed(() => onlyFailed.value ? cases.value.filter(c => c.verdict !== 'AC') : cases.value);
const firstFailure = computed(() => cases.value.find(c => c.verdict !== 'AC'));
const passed = computed(() => cases.value.filter(c => c.verdict === 'AC').length);
const cpu = computed(() => Math.max(0, ...cases.value.map(c => c.cpuMs)));
const peak = computed(() => Math.max(0, ...cases.value.map(c => c.memoryBytes)));
const connectionLabel = computed(() => ({ live: '实时连接', recovering: '恢复连接中', polling: '自动刷新中', offline: '连接中断 · 自动重试', done: '已记录' })[props.connection]);
</script>
<template>
  <aside class="results-panel evidence-panel" aria-label="运行证据">
    <div class="results-heading"><h2><Terminal :size="17" />{{ title || '运行证据' }}</h2><button v-if="submission" class="icon-btn" aria-label="刷新结果" @click="$emit('refresh')"><RefreshCw :size="15" /></button><span v-else class="live-label">C++23</span></div>
    <template v-if="result && submission">
      <div class="connection-line" role="status"><i :class="{ live: connection==='live', offline: connection==='offline' }"></i>{{ connectionLabel }}<span>{{ submission.id.slice(0, 8) }}</span></div>
      <div class="result-summary" :class="result.status==='AC'?'good':active(result.status)?'waiting':'bad'" aria-live="polite">
        <div class="result-icon"><Check v-if="result.status==='AC'" :size="26" /><RefreshCw v-else-if="active(result.status)" :size="24" class="spin" /><X v-else :size="25" /></div>
        <strong>{{ statusLabel(result.status) }}</strong><span class="result-code">{{ result.status }}</span><p>{{ result.message || '正在编译并运行测试点…' }}</p>
      </div>
      <div class="progress-track" role="progressbar" aria-label="测试点完成进度" :aria-valuenow="cases.length" :aria-valuemax="Math.max(total,cases.length)" :aria-valuemin="0"><i :style="{width:resourcePercent(cases.length,Math.max(total,cases.length))+'%'}"></i></div>
      <div class="evidence-stats"><div><b>{{ passed }}<small> / {{ Math.max(total,cases.length) }}</small></b><span>测试点通过</span></div><div><b>{{ cpu.toFixed(0) }}<small> ms</small></b><span>最大 CPU</span></div><div><b>{{ memory(peak) }}</b><span>最大内存</span></div></div>
      <div v-if="limits" class="resource-bars"><span class="snapshot-limits">提交快照 · {{ limits.cpuMs }} ms / {{ limits.memoryMiB }} MiB</span><div><span>CPU 额度</span><b>{{ resourcePercent(cpu,limits.cpuMs).toFixed(0) }}%</b><meter :value="cpu" min="0" :max="limits.cpuMs" :high="limits.cpuMs*.85" :optimum="0" /></div><div><span>内存额度</span><b>{{ resourcePercent(peak,limits.memoryMiB*1048576).toFixed(0) }}%</b><meter :value="peak" min="0" :max="limits.memoryMiB*1048576" :high="limits.memoryMiB*1048576*.85" :optimum="0" /></div></div>
      <p v-else class="verdict-advice">提交快照元数据暂不可用，未使用当前题目的额度替代。</p>
      <p class="verdict-advice">{{ verdictAdvice(result.status) }}</p>
      <button v-if="firstFailure" class="first-failure" @click="$emit('inspect',firstFailure.name)">定位首个失败点 <b>#{{ firstFailure.name }} · {{ firstFailure.verdict }}</b><ArrowRight :size="14" /></button>
      <div v-if="cases.length" class="case-filter"><span>{{ cases.length }} 个已完成</span><label class="checkbox"><input type="checkbox" v-model="onlyFailed" />仅失败</label></div>
      <div class="case-list"><button v-for="c in rows" :key="c.name" class="case-row" @click="$emit('inspect',c.name)"><Check v-if="c.verdict==='AC'" :size="15" class="success-text" /><X v-else :size="15" class="danger-text" /><strong>#{{ c.name }}</strong><span>{{ c.verdict }}</span><small>{{ c.cpuMs.toFixed(0) }} ms</small><ArrowRight :size="13" /></button><p v-if="onlyFailed&&!rows.length" class="muted empty-small">已完成的测试点均通过</p></div>
      <details class="compile-details" :open="result.status==='CE'"><summary>编译记录</summary><pre>{{ result.compileLog || (active(result.status)?'等待编译输出…':'编译完成，无诊断输出') }}</pre><p class="compiler-note">{{ result.compilerVersion }}<br />{{ result.compileArgs }}</p></details>
      <div class="result-tools"><button v-if="sourceVisible" class="text-link" @click="$emit('source')"><Code2 :size="14" />查看本次源码</button><span v-else class="muted">源码仅作者与管理员可见</span><slot name="actions" /></div>
    </template>
    <div v-else class="empty-results"><div class="empty-terminal"><span>›_</span><i></i><i></i></div><h3>让每一次运行都有依据</h3><p>提交后，这里将呈现测试点进度、资源用量与问题定位。</p><ol class="evidence-empty-steps"><li>阅读题面与读写协议</li><li>编写代码并运行评测</li><li>从第一个失败点开始检查</li></ol></div>
    <div class="resource-note">内存：{{ memoryMetric || '系统进程内存峰值' }}<p>{{ mode==='Multiprocess' ? '每个实例分别限额，资源取最大值。' : mode==='Communication' ? '两轮分别限额，资源取最大值。' : '选手进程独立限额；判题工具单独统计。' }}</p></div>
  </aside>
</template>
