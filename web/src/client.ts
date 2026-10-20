import { ref } from 'vue';
import type { UserInfo } from './types';

// Shared API client for both the contestant OJ page and the admin console.
export const needsToken = ref(false);
export const needsAuth = ref(false);
export const token = ref(localStorage.getItem('acm-token') ?? '');
export const currentUser = ref<UserInfo>();

export async function api<T>(url: string, init?: RequestInit): Promise<T> {
  const response = await fetch('/api' + url, { ...init, headers: init?.body instanceof FormData ? init.headers : { 'Content-Type': 'application/json', ...init?.headers } });
  if (response.status === 401) {
    // The server distinguishes the two gates by message: management APIs want the shared token,
    // contestant APIs want a signed-in user.
    const body = await response.json().catch(() => ({} as { error?: string }));
    const adminGate = body.error === '需要访问令牌';
    if (adminGate) needsToken.value = true; else needsAuth.value = true;
    throw new Error(body.error || (adminGate ? '需要访问令牌' : '请先登录'));
  }
  if (!response.ok) { const body = await response.json().catch(() => ({})); throw new Error(body.error || `请求失败 (${response.status})`); }
  return response.status === 204 || response.headers.get('content-length') === '0' ? undefined as T : response.json();
}
export async function loginRequest() {
  const response = await fetch('/api/auth', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ token: token.value.trim() }) });
  if (!response.ok) throw new Error('令牌不正确');
  localStorage.setItem('acm-token', token.value.trim());
  needsToken.value = false;
}

// —— 用户会话：注册 / 登录 / 退出 / 当前用户 ——
export async function loadMe(): Promise<UserInfo | undefined> {
  const data = await fetch('/api/users/me').then(r => r.ok ? r.json() : { user: null }).catch(() => ({ user: null })) as { user: UserInfo | null };
  currentUser.value = data.user ?? undefined;
  return currentUser.value;
}
export async function registerUser(body: { studentId: string; name: string; email: string; password: string }): Promise<UserInfo> {
  const data = await fetch('/api/users/register', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
  const payload = await data.json().catch(() => ({}));
  if (!data.ok) throw new Error(payload.error || '注册失败');
  needsAuth.value = false;
  currentUser.value = payload.user;
  return payload.user as UserInfo;
}
export async function userLogin(studentId: string, password: string): Promise<UserInfo> {
  const data = await fetch('/api/users/login', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ studentId, password }) });
  const payload = await data.json().catch(() => ({}));
  if (!data.ok) throw new Error(payload.error || '登录失败');
  needsAuth.value = false;
  currentUser.value = payload.user;
  return payload.user as UserInfo;
}
export async function userLogout() {
  const response=await fetch('/api/users/logout', { method: 'POST' });
  if (!response.ok) throw new Error('退出失败，请检查连接后重试');
  currentUser.value = undefined;
}
