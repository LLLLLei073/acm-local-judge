import { createApp } from 'vue';
import App from './App.vue';
import './style.css';
import './workbench.css';
// 管理控制台入口，由 /admin 提供；页面与服务端 API 均要求访问令牌。
createApp(App).mount('#app');
