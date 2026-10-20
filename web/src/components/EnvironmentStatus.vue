<script setup lang="ts">
defineProps<{value?: {workerReady:boolean;judging:number;queued:number;parallelism:number}}>();
</script>
<template><div v-if="value" class="environment-pulse" :title="`并行槽位 ${value.parallelism}；状态每 15 秒刷新`" role="status"><i :class="{ready:value.workerReady}"></i><span>{{ value.workerReady?'评测就绪':'Worker 未就绪' }}</span><b>{{ value.judging }}</b><span>运行</span><b>{{ value.queued }}</b><span>排队</span></div></template>
<style scoped>.environment-pulse{display:flex;align-items:center;gap:6px;font-size:10px;color:#6d815f}.environment-pulse i{height:5px;width:5px;background:#b17b38;border-radius:50%}.environment-pulse i.ready{background:#5c9a5b}.environment-pulse b{font:11px 'Cascadia Code',monospace;margin-left:6px;color:#4c6d43}@media(max-width:700px){.environment-pulse>span:first-of-type{display:none}}</style>
