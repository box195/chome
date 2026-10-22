/** One viewer per camera. Firebase transports SDP signaling, not media. */
export function makeLiveView({db,ref,set,remove,onValue,$,getPcState}) {
  let user=null;
  const active=new Map();
  const path=(kind,uid)=>kind==="y700"
    ?"clients/"+uid+"/devices/y700/live":"clients/"+uid+"/pcLive";
  const status=(kind,text)=>{$("liveStatus-"+kind).textContent=text;};
  async function stop(kind){
    const task=active.get(kind);
    if(!task)return;
    active.delete(kind);task.cancelled=true;
    if(task.off)task.off();
    if(task.offState)task.offState();
    task.pc.close();
    const video=$("liveVideo-"+kind);video.pause();video.srcObject=null;
    $("liveButton-"+kind).textContent="실시간 보기";
    status(kind,"종료됨");
    await remove(ref(db,task.path+"/offer")).catch(()=>{});
    await remove(ref(db,task.path+"/answer")).catch(()=>{});
  }
  function gather(pc){
    if(pc.iceGatheringState==="complete")return Promise.resolve();
    return new Promise((resolve,reject)=>{
      const handler=()=>{
        if(pc.iceGatheringState==="complete"){
          clearTimeout(timer);pc.removeEventListener("icegatheringstatechange",handler);resolve();
        }
      };
      const timer=setTimeout(()=>{
        pc.removeEventListener("icegatheringstatechange",handler);
        reject(new Error("ICE 주소 수집 시간 초과"));
      },12000);
      pc.addEventListener("icegatheringstatechange",handler);
    });
  }
  async function start(kind){
    if(!user||active.has(kind))return;
    if(kind==="pc"&&!getPcState()?.online){
      status(kind,"PC 녹화기가 꺼져 있어 영상이 없습니다. PC 감시/녹화부터 시작해 주세요.");return;
    }
    const uid=user.uid,id=crypto.randomUUID().replaceAll("-",""),p=path(kind,uid);
    const pc=new RTCPeerConnection({iceServers:[{urls:"stun:stun.l.google.com:19302"}]});
    const task={id,path:p,pc,cancelled:false,off:null,offState:null};active.set(kind,task);
    $("liveButton-"+kind).textContent="영상 중지";
    status(kind,"WebRTC 연결 준비 중");
    if(kind==="y700")task.offState=onValue(ref(db,p+"/state"),s=>{
      const state=s.val();
      if(!task.cancelled&&typeof state==="string"&&pc.connectionState!=="connected")
        status(kind,"Y700 영상 연결: "+state);
    },e=>status(kind,"Y700 영상 진단 조회 실패: "+e.code));    pc.addTransceiver("video",{direction:"recvonly"});
    pc.ontrack=e=>{
      if(task.cancelled)return;
      const v=$("liveVideo-"+kind);
      v.srcObject=e.streams[0]||new MediaStream([e.track]);
      v.play().catch(()=>status(kind,"영상 재생 버튼을 눌러 줘"));
    };
    pc.onconnectionstatechange=()=>{
      if(task.cancelled)return;
      if(pc.connectionState==="connected")status(kind,"실시간 영상 연결됨");
      else if(["failed","disconnected"].includes(pc.connectionState))
        status(kind,"연결 끊김: 망에 따라 TURN 중계가 필요할 수 있음");
    };
    try{
      await pc.setLocalDescription(await pc.createOffer());
      await gather(pc);if(task.cancelled)return;
      task.off=onValue(ref(db,p+"/answer"),snap=>{
        const a=snap.val();
        if(task.cancelled||!a||a.id!==id||typeof a.sdp!=="string"||
           !Number.isFinite(a.createdAt)||Math.abs(Date.now()-a.createdAt)>90000)return;
        if(pc.signalingState!=="have-local-offer")return;
        pc.setRemoteDescription({type:"answer",sdp:a.sdp})
          .catch(e=>status(kind,"응답 처리 오류: "+e.message));
      },e=>status(kind,"응답 읽기 실패: "+e.code));
      await set(ref(db,p+"/offer"),{id,sdp:pc.localDescription.sdp,createdAt:Date.now()});
      status(kind,"장치의 영상 응답 대기");
      setTimeout(()=>{
        if(active.get(kind)===task&&pc.connectionState!=="connected")
          status(kind,"시간 초과: 장치 연결 또는 ICE/TURN 확인 필요");
      },35000);
    }catch(e){
      await stop(kind);
      status(kind,"연결 실패: "+(e.message||e.code));
    }
  }
  const card=kind=>'<div style="min-width:0"><strong>'+
    (kind==="pc"?"PC":"Y700")+' 실시간 영상</strong>'+
    '<video id="liveVideo-'+kind+'" playsinline autoplay muted controls '+
    'style="display:block;background:#101821;width:100%;min-height:130px;max-height:300px;border-radius:8px;margin:8px 0"></video>'+
    '<button class="dark" id="liveButton-'+kind+'" disabled>실시간 보기</button>'+
    '<p class="notice" id="liveStatus-'+kind+'">로그인 후 사용 가능</p></div>';
  const section=document.createElement("section");section.className="panel";
  section.innerHTML='<h2>실시간 영상</h2><p class="notice">영상 시청 중에만 WebRTC 연결. 기존 녹화는 유지됩니다.</p>'+
    '<div style="display:grid;grid-template-columns:repeat(auto-fit,minmax(225px,1fr));gap:14px">'+
    card("pc")+card("y700")+'</div>';
  $("dashboard").insertBefore(section,$("dashboard").firstChild);
  for(const kind of ["pc","y700"]){
    $("liveButton-"+kind).onclick=()=>active.has(kind)?stop(kind):start(kind);
  }
  return {setUser(u){
    for(const kind of ["pc","y700"]){
      stop(kind);
      $("liveButton-"+kind).disabled=!u||!window.RTCPeerConnection;
      status(kind,u?"연결 대기":"로그인 후 사용 가능");
    }
    user=u;
  }};
}