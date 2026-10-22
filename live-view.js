/**
 * On-demand video: Firebase holds only owner-scoped SDP / ephemeral ICE
 * candidates. Never wait for all ICE addresses before contacting the camera.
 */
export function makeLiveView({db,ref,set,remove,onValue,$,getPcState}) {
  let user=null;
  const active=new Map();
  const prefix=(kind,uid)=>kind==="y700"
    ?"clients/"+uid+"/devices/y700/live":"clients/"+uid+"/pcLive";
  const show=(kind,message)=>{$("liveStatus-"+kind).textContent=message;};
  const message=e=>String(e?.code||e?.message||e||"unknown");

  async function stop(kind,{cleanup=true}={}){
    const session=active.get(kind);
    if(!session)return;
    active.delete(kind);
    session.closed=true;
    for(const off of session.off)off();
    session.pc.onicecandidate=null;
    session.pc.close();
    const video=$("liveVideo-"+kind);
    video.pause();video.srcObject=null;
    $("liveButton-"+kind).textContent="실시간 보기";
    show(kind,"영상 연결 종료");
    if(cleanup){
      // Owner may delete his own per-session signaling without touching others.
      await Promise.all([
        remove(ref(db,session.path+"/offer")).catch(()=>{}),
        remove(ref(db,session.path+"/answer")).catch(()=>{}),
        remove(ref(db,session.path+"/offerCandidates/"+session.id)).catch(()=>{}),
        remove(ref(db,session.path+"/answerCandidates/"+session.id)).catch(()=>{})
      ]);
    }
  }

  async function start(kind){
    if(!user||active.has(kind))return;
    if(!window.RTCPeerConnection){
      show(kind,"현재 브라우저에서 WebRTC를 사용할 수 없어.");return;
    }
    const id=crypto.randomUUID().replaceAll("-","");
    const path=prefix(kind,user.uid);
    const pc=new RTCPeerConnection({
      iceServers:[
        {urls:"stun:stun.l.google.com:19302"},
        {urls:"stun:stun.cloudflare.com:3478"}
      ]
    });
    const session={id,path,pc,off:[],closed:false,remoteCandidates:new Set(),pending:[],frames:0};
    active.set(kind,session);
    $("liveButton-"+kind).textContent="영상 중지";
    show(kind,kind==="pc"&&!getPcState()?.online
      ?"PC 녹화기 미실행 · 실시간 카메라를 연결하는 중"
      :"실시간 영상 연결 요청 중");

    const video=$("liveVideo-"+kind);
    video.srcObject=null;
    pc.addTransceiver("video",{direction:"recvonly"});
    if("requestVideoFrameCallback" in video){
      video.requestVideoFrameCallback(()=>{
        if(active.get(kind)===session){session.frames++;show(kind,"실시간 영상 출력 확인");}
      });
    }
    pc.ontrack=e=>{
      if(session.closed)return;
      video.srcObject=e.streams[0]||new MediaStream([e.track]);
      video.play().catch(()=>show(kind,"영상이 수신됐어. 재생 버튼을 눌러 줘."));
    };
    pc.onconnectionstatechange=()=>{
      if(session.closed)return;
      if(pc.connectionState==="connected")show(kind,"WebRTC 연결됨 · 첫 영상 프레임 대기 중");
      else if(pc.connectionState==="failed")show(kind,"ICE 연결 실패: 외부망에서 TURN 중계가 필요할 수 있어.");
      else if(pc.connectionState==="disconnected")show(kind,"영상 연결이 일시적으로 끊어졌어.");
    };
    pc.onicecandidate=({candidate})=>{
      if(session.closed||!candidate)return;
      const key=crypto.randomUUID().replaceAll("-","");
      const value={candidate:candidate.candidate,
        sdpMid:candidate.sdpMid||"0",
        sdpMLineIndex:candidate.sdpMLineIndex??0,
        createdAt:Date.now()};
      if(value.candidate.length>2000)return;
      set(ref(db,path+"/offerCandidates/"+id+"/"+key),value)
        .catch(e=>{if(!session.closed)show(kind,"ICE 후보 등록 오류: "+message(e));});
    };
    try{
      // Subscribe BEFORE writing, so even immediate Android/PC answers are seen.
      session.off.push(onValue(ref(db,path+"/answer"),async snapshot=>{
        if(session.closed)return;
        const a=snapshot.val();
        if(!a||a.id!==id||typeof a.sdp!=="string"||
          !Number.isFinite(a.createdAt)||Math.abs(Date.now()-a.createdAt)>90000)return;
        if(pc.signalingState!=="have-local-offer")return;
        try{
          await pc.setRemoteDescription({type:"answer",sdp:a.sdp});
          show(kind,"영상 연결 확인 중 (ICE 후보 교환)");
          for(const c of session.pending.splice(0))await pc.addIceCandidate(c);
        }catch(e){show(kind,"상대 장치 SDP 응답 오류: "+message(e));}
      },e=>show(kind,"Firebase 영상 응답 읽기 실패: "+message(e))));
      if(kind==="y700"){
        session.off.push(onValue(ref(db,path+"/answerCandidates/"+id),snap=>{
          snap.forEach(item=>{
            if(session.closed||session.remoteCandidates.has(item.key))return;
            const c=item.val();
            if(!c||typeof c.candidate!=="string")return;
            session.remoteCandidates.add(item.key);
            const ice={candidate:c.candidate,sdpMid:c.sdpMid||"0",sdpMLineIndex:c.sdpMLineIndex??0};
            if(pc.remoteDescription)pc.addIceCandidate(ice).catch(e=>show(kind,"Y700 ICE 응답 오류: "+message(e)));
            else session.pending.push(ice);
          });
        },e=>show(kind,"Y700 ICE 후보 조회 실패: "+message(e))));
        session.off.push(onValue(ref(db,path+"/state"),snap=>{
          const progress=snap.val();
          if(!session.closed&&typeof progress==="string"&&pc.connectionState!=="connected")
            show(kind,"Y700: "+progress);
        }));
      }
      // Crucial: do not await iceGatheringState=complete. STUN resolution can
      // hang for >12s on Android / carrier networks while local host candidates
      // are already usable. Candidate trickling continues after the offer.
      await pc.setLocalDescription(await pc.createOffer());
      if(session.closed)return;
      await set(ref(db,path+"/offer"),{id,sdp:pc.localDescription.sdp,createdAt:Date.now()});
      show(kind,"카메라에 연결 요청 전달됨 · ICE 후보 수집 중");
      setTimeout(()=>{
        if(active.get(kind)===session&&pc.connectionState!=="connected"){
          const state=pc.connectionState;
          show(kind,state==="failed"?"P2P 연결 실패: TURN 중계 필요 가능성":
            "영상 연결 지연: 카메라 응답·방화벽·TURN 확인 필요");
        }
      },35000);
    }catch(e){
      await stop(kind);
      show(kind,"영상 연결 실패: "+message(e));
    }
  }

  const card=kind=>'<div style="min-width:0"><strong>'+
    (kind==="pc"?"PC":"Y700")+' 실시간 영상</strong>'+
    '<video id="liveVideo-'+kind+'" playsinline autoplay muted controls '+
    'style="display:block;background:#101821;width:100%;min-height:130px;max-height:300px;border-radius:8px;margin:8px 0"></video>'+
    '<button class="dark" id="liveButton-'+kind+'" disabled>실시간 보기</button>'+
    '<p class="notice" id="liveStatus-'+kind+'" role="status">로그인 후 사용 가능</p></div>';
  const section=document.createElement("section");
  section.className="panel";
  section.innerHTML='<h2>실시간 영상</h2><p class="notice">영상을 볼 때만 연결하고, 녹화와 별도로 작동합니다.</p>'+
    '<div style="display:grid;grid-template-columns:repeat(auto-fit,minmax(225px,1fr));gap:14px">'+
    card("pc")+card("y700")+'</div>';
  $("dashboard").insertBefore(section,$("dashboard").firstChild);
  for(const kind of ["pc","y700"])
    $("liveButton-"+kind).onclick=()=>active.has(kind)?stop(kind):start(kind);
  return {setUser(next){
    for(const kind of ["pc","y700"]){
      stop(kind);
      $("liveButton-"+kind).disabled=!next||!window.RTCPeerConnection;
      show(kind,next?"영상 연결 대기":"로그인 후 사용 가능");
    }
    user=next;
  }};
}