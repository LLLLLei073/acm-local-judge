<script setup lang="ts">
import { computed, nextTick, ref } from 'vue';
import { Plus, Trash2, ArrowUp, ArrowDown, GripVertical, ChevronDown } from 'lucide-vue-next';
import type { CommunicationFlow, FlowStep, TestData } from './types';
import { flowData, newStep, referenceError, route, flowIssues } from './flow';
import FlowDataEditor from './FlowDataEditor.vue';
import FlowPreview from './FlowPreview.vue';
const flow = defineModel<CommunicationFlow>({ required: true });
defineProps<{ test?: TestData }>();
const emit = defineEmits<{ membersChanged: [count: number] }>();
const warning = ref(''), dragging = ref<number>();
const opened = ref<string | undefined>(flow.value.steps[0]?.id);
const issues = computed(()=>flowIssues(flow.value));
function focusStep(id:string) { opened.value=id; nextTick(()=>document.getElementById('flow-step-'+id)?.scrollIntoView({block:'nearest',behavior:matchMedia('(prefers-reduced-motion: reduce)').matches?'instant':'smooth'})); }
function addMember() {
  if (flow.value.members.length >= 16) return;
  let name = `角色 ${flow.value.members.length}`;
  while (flow.value.members.includes(name)) name += ' 新';
  flow.value.members.push(name); emit('membersChanged', flow.value.members.length);
}
function removeMember(i: number) {
  warning.value = '';
  if (flow.value.steps.some(s => s.to === i || s.kind === 'receive' && s.from === i)) { warning.value = '该成员仍被通信步骤使用，请先删除或修改相关步骤。'; return; }
  flow.value.members.splice(i, 1);
  for (const s of flow.value.steps) { if (s.from > i) s.from--; if (s.to > i) s.to--; }
  emit('membersChanged', flow.value.members.length);
}
function addStep(kind: FlowStep['kind']) { if (flow.value.steps.length < 256) { const step = newStep(kind); flow.value.steps.push(step); opened.value = step.id; } }
function reorder(from: number, to: number) {
  if (to < 0 || to >= flow.value.steps.length) return;
  const next = [...flow.value.steps]; const [moved] = next.splice(from, 1); next.splice(to, 0, moved!);
  warning.value = referenceError(next); if (!warning.value) flow.value.steps = next;
}
function drop(to: number) { if (dragging.value !== undefined) reorder(dragging.value, to); dragging.value = undefined; }
function removeStep(i: number) {
  const next = flow.value.steps.filter((_, index) => index !== i);
  warning.value = referenceError(next); if (!warning.value) flow.value.steps = next;
}
</script>
<template>
  <div class="flow-builder">
    <div class="flow-intro"><div><h3>成员与通信</h3><p>把问答过程排成步骤，调度器负责发送、等待和原样转发。</p></div><label class="checkbox"><input type="checkbox" v-model="flow.enabled" />用此流程执行评测</label></div>
    <p class="flow-hint">{{ flow.enabled ? '当前使用可视化流程；原 manager 源码保留，但不参与本题评测。至少配置一个输出校验。' : '当前仍使用 C++ manager。这里的步骤仅展示样例，勾选后才会控制评测。' }}</p>
    <div class="section-toolbar"><h3>通信成员 <span class="count-pill">{{ flow.members.length }}</span></h3><button class="button secondary" :disabled="flow.members.length>=16" @click="addMember"><Plus :size="14" />添加成员</button></div>
    <div class="member-grid"><div class="scheduler-member"><b>调度器</b><span>分发 · 转发 · 校验</span></div><div v-for="(_,i) in flow.members" :key="i" class="member-card"><span class="member-id">argv[1] = {{ i }}</span><input v-model="flow.members[i]" :aria-label="`成员 ${i} 名称`" maxlength="40" /><button class="icon-btn" :aria-label="`删除成员 ${i}`" :disabled="flow.members.length<=2" @click="removeMember(i)"><Trash2 :size="14" /></button></div></div>
    <p class="muted small-note">每个成员是同一程序的独立进程。删除成员后编号连续重排，相关步骤同步更新。</p>
    <div v-if="warning" class="alert" role="alert">{{ warning }}</div>
    <div v-if="issues.length" class="flow-validation" role="status"><strong>流程检查 · {{ issues.length }} 项待处理</strong><ul><li v-for="issue in issues" :key="issue">{{ issue }}</li></ul></div>
    <div class="section-toolbar"><h3>通信步骤 <span class="count-pill">{{ flow.steps.length }}</span></h3><span class="muted small-note">拖动步骤标题，或使用上下箭头排序</span></div>
    <div v-if="!flow.steps.length" class="flow-empty">先添加“发送数据”，再添加“接收 / 转发”，最后校验成员的回答。</div>
    <article v-for="(step,i) in flow.steps" :key="step.id" :id="'flow-step-'+step.id" :aria-label="`步骤 ${i+1}`" class="flow-step" @dragover.prevent @drop.prevent="drop(i)">
      <header class="step-heading" draggable="true" @dragstart="dragging=i" @dragend="dragging=undefined"><GripVertical :size="16" /><span class="step-number">{{ String(i+1).padStart(2,'0') }}</span><button class="step-title" :aria-label="`编辑步骤 ${i+1}`" :aria-expanded="opened===step.id" @click="opened=opened===step.id?undefined:step.id">{{ route(flow,step) }}<ChevronDown :size="14" /></button><div class="step-actions"><button class="icon-btn" :aria-label="`上移步骤 ${i+1}`" :disabled="i===0" @click="reorder(i,i-1)"><ArrowUp :size="15" /></button><button class="icon-btn" :aria-label="`下移步骤 ${i+1}`" :disabled="i===flow.steps.length-1" @click="reorder(i,i+1)"><ArrowDown :size="15" /></button><button class="icon-btn" :aria-label="`删除步骤 ${i+1}`" @click="removeStep(i)"><Trash2 :size="15" /></button></div></header>
      <div v-if="opened===step.id" class="step-body"><div class="step-routing">
        <label>操作<select v-model="step.kind" @change="step.to=step.kind==='receive'?-1:0"><option value="send">发送数据</option><option value="receive">接收 / 转发</option><option value="close">关闭成员输入</option></select></label>
        <label v-if="step.kind==='receive'">发送成员<select v-model.number="step.from"><option v-for="(name,id) in flow.members" :value="id">{{ name }}（{{ id }}）</option></select></label>
        <label>{{ step.kind==='receive'?'接收后转发给':'接收成员' }}<select v-model.number="step.to"><option v-if="step.kind==='receive'" :value="-1">仅调度器（不转发）</option><option v-for="(name,id) in flow.members" :value="id" :disabled="step.kind==='receive'&&step.from===id">{{ name }}（{{ id }}）</option></select></label>
        <label v-if="step.kind==='receive'">读取完整行数<input type="number" min="1" max="1000000" v-model.number="step.lines" /></label>
      </div>
      <FlowDataEditor v-if="step.kind==='send'" v-model="step.data" label="发送数据来源" :previous="flow.steps.slice(0,i)" />
      <template v-if="step.kind==='send'"><div v-for="(_,part) in step.additionalData" :key="part"><FlowDataEditor v-model="step.additionalData[part]!" :label="`追加数据片段 ${part+1}`" :previous="flow.steps.slice(0,i)" /><button class="text-action" @click="step.additionalData.splice(part,1)">删除片段 {{ part+1 }}</button></div><button class="button secondary" :disabled="step.additionalData.length>=16" @click="step.additionalData.push(flowData('input'))">追加数据片段</button><span class="small-note muted">各片段按顺序拼接，不补换行；可组合输入第 1 行和第 3 行。</span></template>
      <template v-if="step.kind==='receive'"><label class="checkbox check-switch"><input type="checkbox" v-model="step.check" />校验接收内容（按 token 精确比较）</label><FlowDataEditor v-if="step.check" v-model="step.expected" label="期望内容来源" :previous="flow.steps.slice(0,i)" /><p class="small-note muted">等到指定行数才执行下一步。选手需输出换行并 flush；转发保留原始字节。</p></template>
      <label v-if="step.kind!=='close'" class="sample-field">题面样例内容 · 仅用于展示<textarea v-model="step.sample" rows="2" maxlength="65536" :placeholder="step.kind==='send'?'留空时从第一个测试点和数据来源生成':'填写这一步的样例输出，使用真实换行'"></textarea></label>
      <p v-else class="small-note muted">关闭后成员读到 EOF，不能再向该成员发送。流程结束时会自动关闭所有成员输入，并检查正常退出和额外输出。</p>
      </div>
    </article>
    <div class="add-steps"><button class="button secondary" :disabled="flow.steps.length>=256" @click="addStep('send')"><Plus :size="14" />发送数据</button><button class="button secondary" :disabled="flow.steps.length>=256" @click="addStep('receive')"><Plus :size="14" />接收 / 转发</button><button class="button secondary" :disabled="flow.steps.length>=256" @click="addStep('close')"><Plus :size="14" />关闭输入</button></div>
    <FlowPreview :flow="flow" :test="test" :selected="opened" @select="focusStep" />
  </div>
</template>
<style scoped>
.flow-intro{display:flex;align-items:flex-start;justify-content:space-between;gap:20px}.flow-intro h3{margin:0 0 8px}.flow-intro p,.flow-hint{font-size:13px;line-height:1.7}.flow-hint{background:#f2f6ed;padding:12px 16px;border-left:3px solid #638672;margin:18px 0 24px}.member-grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(200px,1fr));gap:10px}.member-card,.scheduler-member{border:1px solid #dce3d7;border-radius:8px;padding:12px;display:flex;align-items:center;gap:8px;flex-wrap:wrap}.member-id{width:100%;font-size:11px;color:#7f8a7a;font-family:monospace}.member-card input{flex:1;min-width:80px;width:100px}.scheduler-member{background:#f5f7f0;flex-direction:column;align-items:flex-start;justify-content:center}.scheduler-member span{font-size:12px;color:#819078}.small-note{font-size:12px;line-height:1.7}.flow-empty{border:1px dashed #cbd6c4;border-radius:8px;padding:30px;text-align:center;color:#819078;font-size:13px}.flow-step{border:1px solid #dce3d7;border-radius:10px;margin:14px 0;overflow:hidden}.step-heading{display:flex;align-items:center;gap:10px;background:#f6f8f2;padding:10px 14px;cursor:grab}.step-heading strong{font-size:13px;font-weight:500}.step-number{font-family:monospace;color:#6f8b73;font-size:15px}.step-actions{display:flex;gap:3px;margin-left:auto;flex-shrink:0}.step-body{padding:18px;display:grid;gap:16px}.step-routing{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:12px}.check-switch{border-top:1px solid #edf0e9;padding-top:14px}.sample-field{padding-top:14px;border-top:1px dashed #dce3d7}.sample-field textarea{background:#fafbf7}.add-steps{display:flex;gap:10px;flex-wrap:wrap;margin:18px 0}.icon-btn:disabled{opacity:.35;cursor:not-allowed}@media(max-width:600px){.flow-intro{flex-direction:column}.step-routing{grid-template-columns:1fr}.step-heading{flex-wrap:wrap}.step-body{padding:12px}}
</style>

<style scoped>
.step-title{display:flex;align-items:center;gap:8px;text-align:left;flex:1;font-size:13px;line-height:1.7}.step-title[aria-expanded=true] svg{transform:rotate(180deg)}
</style>
