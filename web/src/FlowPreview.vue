<script setup lang="ts">
import { computed, ref } from 'vue';
import FlowSequence from './components/FlowSequence.vue';
import type { CommunicationFlow, TestData } from './types';
import { samples } from './flow';
const props = defineProps<{ flow: CommunicationFlow; test?: TestData; selected?: string }>();
const emit=defineEmits<{ select: [id: string] }>();
const sequence=ref(true);
const focused=ref<string>();
const rows = computed(() => samples(props.flow, props.test));
const selectedRow=computed(()=>rows.value.find(row=>row.id===(props.selected??focused.value)));
function choose(id:string) { focused.value=id; emit('select',id); }
</script>
<template>
  <section v-if="rows.length" class="flow-preview">
    <h3>通信样例 · 按通信顺序</h3>
    <p class="muted">样例内容用于说明；实际执行读取各测试点的数据。内容保留真实换行。</p>
    <div class="preview-switch"><button :class="{active:sequence}" @click="sequence=true">成员时序图</button><button :class="{active:!sequence}" @click="sequence=false">通信表格</button><span>↓ 按顺序执行 · ↵ 表示换行</span></div>
    <FlowSequence v-if="sequence" :flow="flow" :rows="rows" :selected="selected ?? focused" @select="choose" />
    <div v-if="sequence && selectedRow" class="sequence-detail"><strong>{{ selectedRow.route }}</strong><pre>{{ selectedRow.content || '（空内容）' }}</pre></div>
    <div v-if="!sequence" class="flow-table-wrap"><table><thead><tr><th>步骤</th><th>发送方 → 接收方</th><th>内容</th></tr></thead><tbody>
      <tr v-for="(row,i) in rows" :key="row.id"><td>{{ i+1 }}</td><td>{{ row.route }}</td><td><pre>{{ row.content || '（空内容）' }}</pre></td></tr>
    </tbody></table></div>
  </section>
</template>
<style scoped>
.flow-preview{margin:24px 0}.flow-preview h3{font-size:17px}.flow-preview p{font-size:12px;margin:8px 0 16px}.flow-table-wrap{overflow:auto}table{width:100%;border-collapse:collapse;font-size:13px}th{text-align:left;background:#f3f6ef;color:#758672;font-weight:500}th,td{padding:16px 14px;border-bottom:1px solid #e8ece2;vertical-align:top}td:first-child{width:55px;color:#87947e}td:nth-child(2){min-width:185px}pre{white-space:pre-wrap;overflow-wrap:anywhere;min-width:100px;margin:0!important;padding:0!important;background:none!important;border:none!important}
</style>
