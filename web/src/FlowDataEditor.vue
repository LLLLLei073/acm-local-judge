<script setup lang="ts">
import type { FlowData, FlowStep } from './types';
import { computed } from 'vue';
const model = defineModel<FlowData>({ required: true });
const props = defineProps<{ previous: FlowStep[]; label: string }>();
const received = computed(() => props.previous.map((step,i) => ({ step, number: i+1 })).filter(x => x.step.kind==='receive'));
</script>
<template>
  <div class="flow-data-editor">
    <label>{{ label }}<select v-model="model.source">
      <option value="input">测试输入 .in</option><option value="answer">参考答案 .ans</option>
      <option value="literal">固定文本</option><option value="message">前面收到的消息</option>
    </select></label>
    <label v-if="model.source==='message'">引用消息<select v-model="model.stepId"><option value="" disabled>选择前面的接收步骤</option><option v-for="item in received" :key="item.step.id" :value="item.step.id">步骤 {{ item.number }} · 成员 {{ item.step.from }} 的消息</option></select></label>
    <template v-if="model.source!=='literal'">
      <label>起始行（从 1 开始）<input v-model.number="model.startLine" type="number" min="1" max="1000000" /></label>
      <label>行数（0 表示到末尾）<input v-model.number="model.lineCount" type="number" min="0" max="1000000" /></label>
    </template>
    <label v-else class="literal-data">固定文本<textarea v-model="model.text" rows="3" placeholder="使用真实换行，不会自动补换行" maxlength="65536"></textarea><button type="button" class="text-action" @click="model.text+='\n'">末尾添加换行</button></label>
  </div>
</template>
<style scoped>
.flow-data-editor{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:12px}.literal-data{grid-column:1/-1}.text-action{font-size:12px;color:var(--accent,#285c48);text-align:left;padding:4px 0}@media(max-width:600px){.flow-data-editor{grid-template-columns:1fr}}
</style>
