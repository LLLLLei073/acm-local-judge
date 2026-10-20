<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { X, Search, Download } from 'lucide-vue-next';
import { memory, type Case } from '../types';
import { decodeTrace, filterTrace } from '../domain/workbench';
const props = defineProps<{ value?: Case }>();
defineEmits<{ close: [] }>();
const participant = ref(''), query = ref(''), view = ref('trace'), page = ref(1);
watch(() => props.value, () => { participant.value = ''; query.value = ''; page.value = 1; view.value = props.value?.trace.length ? 'trace' : 'instances'; });
watch([participant, query], () => page.value = 1);
const actors = computed(() => [...new Set(props.value?.trace.flatMap(t => [t.from,t.to]) ?? [])]);
const filtered = computed(() => filterTrace(props.value?.trace ?? [],participant.value,query.value));
const shown = computed(() => filtered.value.slice((page.value-1)*100,page.value*100));
function download() {
  if (!props.value) return;
  const url = URL.createObjectURL(new Blob([JSON.stringify(props.value,null,2)], {type:'application/json'}));
  const a = document.createElement('a'); a.href = url; a.download = 'test-case-' + props.value.name.replace(/[^\w-]/g,'_') + '.json'; a.click(); setTimeout(() => URL.revokeObjectURL(url),0);
}
</script>
<template>
  <div v-if="value" class="modal-backdrop" @keydown.esc="$emit('close')"><section class="modal logs-modal" role="dialog" aria-modal="true" aria-label="测试点详情"><header class="modal-header"><div><span class="eyebrow">RUN INSPECTOR · #{{ value.name }}</span><h2>{{ value.verdict }} · {{ value.message || '测试点详情' }}</h2></div><div class="heading-actions"><button class="button secondary" @click="download"><Download :size="14" />导出日志</button><button class="icon-btn" aria-label="关闭详情" @click="$emit('close')"><X :size="21" /></button></div></header>
    <div class="panel-tabs"><button :class="{active:view==='trace'}" @click="view='trace'">通信时间线 <span class="count-pill">{{ value.trace.length }}</span></button><button :class="{active:view==='instances'}" @click="view='instances'">实例与输出 <span class="count-pill">{{ value.instances.length }}</span></button></div>
    <div class="modal-body">
      <template v-if="view==='trace'"><div class="inspector-filters"><select v-model="participant" aria-label="通信参与者"><option value="">全部参与者</option><option v-for="actor in actors" :key="actor">{{ actor }}</option></select><div class="search-box"><Search :size="15" /><input v-model="query" placeholder="搜索消息内容或方向" aria-label="搜索通信内容" /></div><span class="muted">{{ filtered.length }} / {{ value.trace.length }} 段</span></div><div v-if="!filtered.length" class="empty-state">{{ value.trace.length ? '没有匹配的通信消息，请调整筛选。' : '此测试点没有通信记录。查看实例的 stdout 与 stderr。' }}</div><div v-for="(t,i) in shown" :key="(page-1)*100+i" class="trace-event"><time>+{{ t.ms.toFixed(1) }} ms</time><div><strong>{{ t.from }} <span>→</span> {{ t.to }}</strong><pre>{{ decodeTrace(t.dataBase64) || '（空消息）' }}</pre></div></div><div v-if="filtered.length>100" class="pagination"><button class="button secondary" :disabled="page===1" @click="page--">上一页</button><span>{{ page }} / {{ Math.ceil(filtered.length/100) }}</span><button class="button secondary" :disabled="page*100>=filtered.length" @click="page++">下一页</button></div></template>
      <template v-else><article v-for="instance in value.instances" :key="instance.round+'-'+instance.id" class="instance-card"><header><h3>第 {{ instance.round }} 轮 · 进程 {{ instance.id }}</h3><span class="verdict" :class="instance.run.verdict==='AC'?'success':'failed'">{{ instance.run.verdict }}</span></header><p>{{ instance.run.message }}</p><dl class="instance-metrics"><div><dt>CPU</dt><dd>{{ instance.run.usage.cpuMs.toFixed(2) }} ms</dd></div><div><dt>墙钟</dt><dd>{{ instance.run.usage.wallMs.toFixed(2) }} ms</dd></div><div><dt>内存峰值</dt><dd>{{ memory(instance.run.usage.memoryBytes) }}</dd></div><div><dt>退出码</dt><dd>{{ instance.run.exitCode }}</dd></div></dl><div class="sample-grid"><div><div class="sample-label">stdout</div><pre>{{ instance.run.stdout || '（无输出）' }}</pre></div><div><div class="sample-label">stderr</div><pre>{{ instance.run.stderr || '（无输出）' }}</pre></div></div></article><p v-if="!value.instances.length" class="empty-state">程序尚未启动，或在编译阶段已结束。</p></template>
    </div></section></div>
</template>
