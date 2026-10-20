<script setup lang="ts">
import { computed } from 'vue';
import type { PublicCommunicationSample, CommunicationFlow } from '../types';
import { flowData } from '../flow';
import FlowPreview from '../FlowPreview.vue';
const props=defineProps<{sample:PublicCommunicationSample}>();
// This structure is display-only: the server never sends flow expectations or hidden test sources.
const flow=computed<CommunicationFlow>(()=>({version:1,enabled:false,members:props.sample.members,steps:props.sample.steps.map(s=>({id:s.id,kind:s.kind,from:s.from,to:s.to,lines:1,data:flowData(),additionalData:[],check:false,expected:flowData(),sample:s.content}))}));
</script>
<template><FlowPreview :flow="flow" /></template>
