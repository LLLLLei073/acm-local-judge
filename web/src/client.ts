import { ref } from 'vue';

// Shared API client for both the contestant OJ page and the admin console.
export const needsToken = ref(false);
export const token = ref(localStorage.getItem('acm-token') ?? '');

export async function api<T>(url: string, init?: RequestInit): Promise<T> {
  const response = await fetch('/api' + url, { ...init, headers: init?.body instanceof FormData ? init.headers : { 'Content-Type': 'application/json', ...init?.headers } });
  if (response.status === 401) { needsToken.value = true; throw new Error('需要访问令牌'); }
  if (!response.ok) { const body = await response.json().catch(() => ({})); throw new Error(body.error || `请求失败 (${response.status})`); }
  return response.status === 204 || response.headers.get('content-length') === '0' ? undefined as T : response.json();
}
export async function loginRequest() {
  const response = await fetch('/api/auth', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ token: token.value.trim() }) });
  if (!response.ok) throw new Error('令牌不正确');
  localStorage.setItem('acm-token', token.value.trim());
  needsToken.value = false;
}
