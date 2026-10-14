<script setup lang="ts">
import { computed } from 'vue';
import type { CommunicationFlow, TestData } from './types';
import { samples } from './flow';
const props = defineProps<{ flow: CommunicationFlow; test?: TestData }>();
const rows = computed(() => samples(props.flow, props.test));
</script>
<template>
  <section v-if="rows.length" class="flow-preview">
    <h3>通信样例 · 按通信顺序</h3>
    <p class="muted">样例内容用于说明；实际执行读取各测试点的数据。内容保留真实换行。</p>
    <div class="flow-table-wrap"><table><thead><tr><th>步骤</th><th>发送方 → 接收方</th><th>内容</th></tr></thead><tbody>
      <tr v-for="(row,i) in rows" :key="row.id"><td>{{ i+1 }}</td><td>{{ row.route }}</td><td><pre>{{ row.content || '（空内容）' }}</pre></td></tr>
    </tbody></table></div>
  </section>
</template>
<style scoped>
.flow-preview{margin:24px 0}.flow-preview h3{font-size:17px}.flow-preview p{font-size:12px;margin:8px 0 16px}.flow-table-wrap{overflow:auto}table{width:100%;border-collapse:collapse;font-size:13px}th{text-align:left;background:#f3f6ef;color:#758672;font-weight:500}th,td{padding:16px 14px;border-bottom:1px solid #e8ece2;vertical-align:top}td:first-child{width:55px;color:#87947e}td:nth-child(2){min-width:185px}pre{white-space:pre-wrap;overflow-wrap:anywhere;min-width:100px;margin:0!important;padding:0!important;background:none!important;border:none!important}
</style>
