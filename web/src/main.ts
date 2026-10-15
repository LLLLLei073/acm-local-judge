import { createApp } from 'vue';
import OjPage from './OjPage.vue';
import './style.css';
// 选手 OJ 端入口；管理端是独立的 /admin 页面（admin.html），不在此打包。
createApp(OjPage).mount('#app');
