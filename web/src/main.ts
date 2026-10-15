import { createApp } from 'vue';
import App from './App.vue';
import OjPage from './OjPage.vue';
import './style.css';
// `/admin` 是管理员工作台，其余路径都是选手 OJ 端（服务端 MapFallbackToFile 保证路由可达）。
createApp(location.pathname.startsWith('/admin') ? App : OjPage).mount('#app');
