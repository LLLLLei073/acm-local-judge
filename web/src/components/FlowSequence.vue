<script setup lang="ts">
import { computed, useId } from 'vue';
import type { CommunicationFlow } from '../types';
const props=defineProps<{flow:CommunicationFlow;rows:{id:string;route:string;content:string}[];selected?:string}>();
defineEmits<{select:[id:string]}>();
const marker='arrow-'+useId();
const lanes=computed(()=>['调度器',...props.flow.members]);
const width=computed(()=>Math.max(600,lanes.value.length*150));
const x=(actor:number)=>(actor+1)*150+75;
const segments=computed(()=>props.flow.steps.map((s,i)=>({id:s.id,y:68+i*67,from:s.kind==='receive'?s.from:-1,to:s.to,kind:s.kind,checked:s.kind==='receive'&&s.check,forward:s.kind==='receive'&&s.to>=0,content:props.rows[i]?.content.replace(/\n/g,' ↵ ').slice(0,40)})));
</script>
<template>
  <div class="sequence-scroll"><svg :viewBox="`0 0 ${width} ${segments.length*67+90}`" :style="{minWidth:width+'px'}" role="group" aria-label="成员通信时序图">
    <defs><marker :id="marker" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="5" markerHeight="5" orient="auto-start-reverse"><path d="M 0 0 L 10 5 L 0 10 z" fill="#537f60" /></marker></defs>
    <g v-for="(name,i) in lanes" :key="i"><rect :x="i*150+20" y="5" width="110" height="30" rx="4" fill="#eaf1df" /><text :x="i*150+75" y="25" text-anchor="middle" class="lane-name">{{ name }}</text><line :x1="i*150+75" :x2="i*150+75" y1="36" :y2="segments.length*67+70" stroke="#ccd9c2" stroke-dasharray="4 5" /></g>
    <g v-for="(s,i) in segments" :key="s.id" tabindex="0" role="button" :aria-label="`步骤 ${i+1}：${rows[i]?.route}`" :class="{chosen:selected===s.id}" @click="$emit('select',s.id)" @keydown.enter="$emit('select',s.id)" @keydown.space.prevent="$emit('select',s.id)"><rect x="4" :y="s.y-19" :width="width-8" height="59" rx="4" class="sequence-highlight" /><text x="8" :y="s.y+8" class="sequence-number">{{ i+1 }}</text><line v-if="s.kind!=='receive'" :x1="x(-1)" :x2="x(s.to)" :y1="s.y+8" :y2="s.y+8" stroke="#537f60" :marker-end="`url(#${marker})`" /><template v-else><line :x1="x(s.from)" :x2="x(-1)" :y1="s.y+8" :y2="s.y+8" stroke="#537f60" :marker-end="`url(#${marker})`" /><line v-if="s.forward" :x1="x(-1)" :x2="x(s.to)" :y1="s.y+19" :y2="s.y+19" stroke="#a47e4e" :marker-end="`url(#${marker})`" /></template><text x="90" :y="s.y-1" class="sequence-content">{{ s.content }}{{ s.checked?' · 校验':'' }}</text></g>
  </svg></div>
</template>
<style scoped>
.sequence-scroll{overflow:auto;max-height:600px;border:1px solid #dce5d0;border-radius:5px;background:#fcfef8}.sequence-scroll svg{display:block;width:100%}.lane-name{font-size:12px;fill:#496d42}.sequence-content{font-size:10px;fill:#657f54}.sequence-number{font-size:11px;fill:#7c906b}.sequence-highlight{fill:transparent}g[role=button]{cursor:pointer}g[role=button]:hover .sequence-highlight,g.chosen .sequence-highlight{fill:#dceac659}
</style>
