/* Chome is a notification-only service worker: all pushes use this single path. */
self.addEventListener('install',event=>event.waitUntil(self.skipWaiting()));
self.addEventListener('activate',event=>event.waitUntil(self.clients.claim()));
self.addEventListener('push', event => {
  event.waitUntil((async () => {
    let data = {};
    try { data = event.data ? event.data.json() : {}; } catch {}
    if (!data || (data.type !== 'motion' && data.type !== 'test')) return;
    const title = data.type === 'test' ? 'CHome 시험 알림' : 'CHome 움직임 감지';
    const body = data.type === 'test' ? '휴대폰 웹 푸시 수신 확인용 알림입니다.' : '카메라에서 새로운 움직임이 감지되었습니다.';
    await self.registration.showNotification(title, {
      body, tag: data.type + '-' + String(data.id || '').slice(0, 64),
      renotify: false, icon: './icon.svg', badge: './icon.svg',
      data: {url: new URL('./', self.registration.scope).href},
    });
  })());
});
self.addEventListener('notificationclick', event => {
  event.notification.close();
  event.waitUntil((async () => {
    const url = new URL('./', self.registration.scope).href;
    const windows = await self.clients.matchAll({type:'window', includeUncontrolled:true});
    const existing = windows.find(client => client.url.startsWith(url));
    if (existing) { await existing.focus(); return; }
    if (self.clients.openWindow) await self.clients.openWindow(url);
  })());
});
