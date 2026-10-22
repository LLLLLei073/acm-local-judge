import { proxyRequest } from '../deployment/proxy.mjs';

export const onRequest = context => proxyRequest(context);
