<script setup lang="ts">
import { computed } from 'vue';
import { Check, Circle, ArrowRight } from 'lucide-vue-next';
import { readiness } from '../domain/workbench';
import type { Problem, Submission } from '../types';
const props=defineProps<{ problem: Problem; verification?: Submission }>();
defineEmits<{ edit: [section: string] }>();
const checks=computed(()=>readiness(props.problem));
const count=computed(()=>checks.value.filter(c=>c.ok).length);
</script>
<template>
  <section class="preparation" aria-label="出题准备清单"><header><div><span class="eyebrow">RELEASE CHECK</span><h3>让题目经得起每一次提交</h3></div><b>{{ count }} / {{ checks.length }}</b></header><div class="preparation-grid"><button v-for="item in checks" :key="item.id" :class="{ready:item.ok}" :title="item.detail" @click="$emit('edit',item.id==='tests'?'tests':item.id==='tools'||item.id==='solution'?'judge':'basic')"><Check v-if="item.ok" :size="15" /><Circle v-else :size="15" /><span>{{ item.label }}</span><ArrowRight :size="12" /></button></div><p>最近验证：<b>{{ verification?.status ?? '尚无记录' }}</b>。历史验证可能来自旧版本；修改题面、数据或源码后，请再次运行标准答案。</p></section>
</template>
