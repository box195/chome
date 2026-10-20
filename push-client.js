// Browser Push API is only for enrollment; never synthesize a foreground Notification.
export function makePushUI({auth, db, ref, set, onValue, remove, $, text}) {
  let currentUser = null, stopReceipt = null, busy = false, config = null;
  const storageKey = 'chome-push-device-id-v1';
  function deviceId() {
    let id = localStorage.getItem(storageKey);
    if (!/^[a-f0-9]{32}$/.test(id || '')) {
      id = crypto.randomUUID().replace(/-/g, '');
      localStorage.setItem(storageKey, id);
    }
    return id;
  }
  const errorText = error => (error?.code || error?.message || String(error)).slice(0, 180);
  const support = () => window.isSecureContext && 'serviceWorker' in navigator &&
    'PushManager' in window && 'Notification' in window;
  const setStatus = msg => text('motionInfo', msg);
  async function getConfig() {
    if (config) return config;
    const response = await fetch('./push-config.json', {cache:'no-store'});
    if (!response.ok) throw Error('공개 푸시 설정 파일이 없습니다 (HTTP ' + response.status + ')');
    const data = await response.json();
    if (!data.enabled || !/^[A-Za-z0-9_-]{86,88}$/.test(data.publicKey || ''))
      throw Error('PC 푸시가 아직 설정되지 않았습니다.');
    config = data;
    return data;
  }
  function toBytes(value) {
    const b64 = value.replace(/-/g, '+').replace(/_/g, '/');
    const raw = atob(b64 + '='.repeat((4 - b64.length % 4) % 4));
    return Uint8Array.from(raw, ch => ch.charCodeAt(0));
  }
  async function service() {
    await navigator.serviceWorker.register('./push-sw.js', {scope:'./', updateViaCache:'none'});
    return navigator.serviceWorker.ready;
  }
  async function refresh() {
    if (!currentUser) return;
    if (!support()) { setStatus('이 브라우저는 HTTPS 웹 푸시를 지원하지 않습니다. Android Chrome을 확인하세요.'); return; }
    if (Notification.permission === 'denied') { setStatus('알림이 차단되었습니다. Android의 사이트 알림 권한을 허용하세요.'); return; }
    try {
      const settings = await getConfig();
      const registration = await service();
      const subscription = await registration.pushManager.getSubscription();
      setStatus(subscription && Notification.permission === 'granted'
        ? '이 휴대폰에 웹 푸시가 등록되어 있습니다. 탭을 닫아도 푸시를 받을 수 있습니다.'
        : '휴대폰 알림 켜기를 눌러 푸시를 등록하세요.');
      $('pushTest').disabled = !subscription;
      $('notify').textContent = subscription ? '휴대폰 알림 다시 등록' : '휴대폰 알림 켜기';
    } catch (error) { setStatus('설정 확인 실패: ' + errorText(error)); $('pushTest').disabled = true; }
  }
  async function enroll() {
    if (busy || !currentUser) return;
    if (!support()) { setStatus('이 브라우저는 웹 푸시를 지원하지 않습니다.'); return; }
    busy = true; $('notify').disabled = true;
    const uid = currentUser.uid;
    try {
      const permission = await Notification.requestPermission();
      if (permission !== 'granted') throw Error('알림 권한이 허용되지 않았습니다.');
      const settings = await getConfig();
      const registration = await service();
      const previous = await registration.pushManager.getSubscription();
      const key = toBytes(settings.publicKey);
      // A changed VAPID public key needs a new subscription.
      let subscription = previous;
      if (subscription && JSON.stringify(subscription.options?.applicationServerKey || []) !== JSON.stringify(Array.from(key))) {
        // The key check below uses raw byte comparison rather than relying on serialization.
        const oldKey = subscription.options?.applicationServerKey;
        if (oldKey && (oldKey.byteLength !== key.byteLength ||
            !Array.from(new Uint8Array(oldKey)).every((b, i) => b === key[i]))) {
          await subscription.unsubscribe(); subscription = null;
        }
      }
      subscription ||= await registration.pushManager.subscribe({userVisibleOnly:true, applicationServerKey:key});
      if (auth.currentUser?.uid !== uid) throw Error('로그인이 변경되었습니다.');
      const json = subscription.toJSON();
      if (!json.endpoint || !json.keys?.p256dh || !json.keys?.auth) throw Error('구독 정보를 받지 못했습니다.');
      await set(ref(db, 'clients/' + uid + '/push/subscriptions/' + deviceId()), {
        endpoint:json.endpoint, keys:{p256dh:json.keys.p256dh,auth:json.keys.auth},
        updatedAt:Date.now(),
      });
      setStatus('웹 푸시 등록 완료. 이제 시험 알림을 보내 확인하세요.');
      $('pushTest').disabled = false;
    } catch (error) { setStatus('알림 등록 실패: ' + errorText(error)); }
    finally { busy = false; $('notify').disabled = false; }
  }
  async function test() {
    if (busy || !currentUser) return;
    busy = true; $('pushTest').disabled = true;
    const uid = currentUser.uid;
    const id = crypto.randomUUID().replace(/-/g, '');
    const path = 'clients/' + uid + '/push/';
    try {
      if (!support() || Notification.permission !== 'granted') throw Error('알림 권한이 필요합니다.');
      const registration = await service();
      if (!await registration.pushManager.getSubscription()) throw Error('먼저 휴대폰 알림을 등록하세요.');
      const device = deviceId();
      const request = {deviceId:device,requester:uid,requestedAt:Date.now()};
      await set(ref(db,path+'testRequests/'+id), request);
      setStatus('시험 요청 접수. PC 발송 결과를 확인 중입니다…');
      await new Promise((resolve, reject) => {
        let settled = false;
        let unsub = null;
        const finish = (err, result) => {
          if (settled) return;
          settled = true;
          clearTimeout(timer);
          if (unsub) unsub();
          if (err) reject(err); else resolve(result);
        };
        const timer = setTimeout(() => finish(Error('PC 발송 확인 시간 초과 (30초).')), 30000);
        unsub = onValue(ref(db,path+'receipts/'+id), snap => {
          const receipt = snap.val();
          if (!receipt) return;
          if (receipt.deviceId !== device) return;
          if(receipt.phase !== 'sent' && receipt.phase !== 'failed') return;
          finish(null,receipt);
        }, error => finish(error));
        if (settled && unsub) unsub();
      }).then(receipt => {
        if (receipt.phase === 'sent') setStatus('PC에서 푸시 서버로 발송 요청 성공. 실제 휴대폰 수신 여부는 알림을 확인하세요.');
        else setStatus('시험 알림 발송 실패: ' + (receipt.detail || '원인 미확인'));
      });
    } catch (error) { setStatus('시험 알림 오류: ' + errorText(error)); }
    finally { busy=false; $('pushTest').disabled=false; }
  }
  $('notify').onclick = enroll;
  $('pushTest').onclick = test;
  async function setUser(user) {
    currentUser = user;
    if (!user) { if (stopReceipt) stopReceipt(); stopReceipt=null; $('pushTest').disabled=true;return; }
    await refresh();
  }
  async function disable() {
    if(currentUser) await remove(ref(db,'clients/'+currentUser.uid+'/push/subscriptions/'+deviceId()));
    if(support()) { const registration=await navigator.serviceWorker.getRegistration('./'); const subscription=await registration?.pushManager.getSubscription();if(subscription)await subscription.unsubscribe(); }
  }
  return {setUser,disable};
}
