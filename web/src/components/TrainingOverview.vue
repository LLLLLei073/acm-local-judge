<script setup lang="ts">
import { computed } from 'vue';
import { ArrowUpRight, ArrowRight, Activity, Target } from 'lucide-vue-next';
import { modeInfo, type ProblemSummary, type TrainingData } from '../types';
const props = defineProps<{ data?: TrainingData; problems: ProblemSummary[]; name: string }>();
defineEmits<{ open: [id: string]; history: [] }>();
const solved = computed(() => new Set(props.data?.solvedProblemIds ?? []));
const resume = computed(() => props.data?.recent.find(s => !solved.value.has(s.problemId)));
const suggestion = computed(() => {
  const completed = props.problems.filter(p => solved.value.has(p.id)).map(p => p.rating).filter(Boolean);
  const target = completed.length ? Math.max(...completed) : 800;
  return [...props.problems].filter(p => !solved.value.has(p.id) && p.rating > 0).sort((a,b) => Math.abs(a.rating-target)-Math.abs(b.rating-target))[0];
});
const dailyMax = computed(() => Math.max(1,...(props.data?.activity.map(d=>d.submissions)??[])));
</script>
<template>
  <section class="training-overview" aria-label="训练概览">
    <div class="training-intro"><span class="eyebrow">YOUR NEXT ACCEPTED</span><h2>{{ name }}，从这里继续。</h2><p>每一次尝试，都能找到下一步。</p><div class="training-next" v-if="resume"><span>待攻克</span><button @click="$emit('open',resume.problemId)">{{ resume.title }}<ArrowRight :size="16" /></button></div><div class="training-next" v-else-if="suggestion"><span>难度邻近推荐</span><button @click="$emit('open',suggestion.id)">{{ suggestion.title }}<ArrowRight :size="16" /></button></div><p v-else class="muted">从题库选择一道感兴趣的题目开始。</p></div>
    <div class="training-evidence"><div class="training-numbers"><div><Target :size="17" /><b>{{ data?.solvedProblemIds.length ?? 0 }}</b><span>已解决</span></div><div><b>{{ data?.attemptedProblemIds.length ?? 0 }}</b><span>待攻克</span></div><div><b>{{ data?.submissionCount ?? 0 }}</b><span>累计提交</span></div></div><div class="training-activity"><div><span><Activity :size="13" />近 7 天 · UTC</span><button @click="$emit('history')">运行记录<ArrowUpRight :size="13" /></button></div><div class="activity-bars"><div v-for="day in data?.activity ?? []" :key="day.date" :title="`${day.date}：${day.submissions} 次提交，${day.accepted} 次 AC`"><i :style="{height:Math.max(3,day.submissions/dailyMax*30)+'px'}" :class="{accepted:day.accepted>0}"></i><small>{{ day.date.slice(5) }}</small></div></div></div></div>
  </section>
  <div class="mode-strip" aria-label="题型覆盖"><span v-for="mode in ['Standard','Special','Interactive','Communication','Multiprocess'] as const" :key="mode"><i :style="{background:modeInfo(mode).color}"></i>{{ modeInfo(mode).label }}<b>{{ problems.filter(p=>p.mode===mode).length }}</b></span></div>
</template>
