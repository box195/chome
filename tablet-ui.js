/** Optional Android Y700 recorder; legacy PC paths and behavior are unchanged. */
export function createTabletUI({db,ref,onValue,set,remove,$}) {
  const container=document.createElement("section");
  container.className="panel";
  container.id="tabletPanel";
  container.innerHTML=[
    '<h2>Y700 태블릿 · 수동 제어</h2>',
    '<p class="notice">자동 전환 없음. 태블릿에서 감시 서비스와 최초 연결을 시작해야 합니다.</p>',
    '<div><small>연결 상태</small><strong id="tabletState">로그인 필요</strong></div>',
    '<div><small>녹화 상태</small><strong id="tabletRecording">-</strong></div>',
    '<div><small>감지 횟수 / 최근 움직임</small><strong id="tabletMotion">-</strong></div>',
    '<div><small>로컬 파일 / MYBOX 업로드 / 대기</small><strong id="tabletFiles">-</strong></div>',
    '<label for="tabletMode">녹화 방식</label>',
    '<select id="tabletMode"><option value="MOTION">움직임 감지 시 녹화</option><option value="CONTINUOUS">상시 녹화</option></select>',
    '<div class="buttons"><button class="blue" id="tabletStart" disabled>Y700 감시 시작</button><button class="red" id="tabletStop" disabled>Y700 감시 중지</button></div>',
    '<p class="notice" role="status" id="tabletFeedback">연결을 기다리는 중</p>',
    '<details><summary>Y700 최초 연결 / 연결 해제</summary>',
    '<p class="notice">Y700 앱에서 표시된 기기 UID를 입력하세요. 로그인한 소유자만 연결할 수 있습니다.</p>',
    '<input id="tabletDeviceUid" autocomplete="off" placeholder="Y700 앱에 표시된 기기 UID">',
    '<button id="tabletPair" class="dark">Y700 연결</button><button id="tabletUnpair" class="dark">Y700 연결 해제</button>',
    '<p class="notice" id="tabletPairFeedback" role="status"></p>',
    '<p class="notice">태블릿에 입력할 소유자 UID: <code id="tabletOwnerUid">로그인 후 표시</code></p></details>'
  ].join("");
  const dashboard=$("dashboard");
  dashboard.insertBefore(container,dashboard.firstChild);
  const get=id=>document.getElementById(id);
  const message=(id,value)=>{get(id).textContent=String(value);};
  let user=null,status=null,configuredUid=null,pending=null,lastSeq=0;
  let listeners=[];
  const cleanup=()=>{listeners.forEach(fn=>fn());listeners=[];status=null;configuredUid=null;pending=null;render();};
  const devicePath=()=> "clients/"+user.uid+"/devices/y700";
  function online(){return Boolean(configuredUid&&status?.monitoring&&Number.isFinite(Number(status?.bridgeAt))&&Math.abs(Date.now()-Number(status.bridgeAt))<20000);}
  function render(){
    const alive=online(), paired=Boolean(configuredUid);
    message("tabletState",!user?"로그인 필요":!paired?"기기 연결 필요":alive?"Y700 온라인":"Y700 연결 대기/오프라인");
    message("tabletRecording",alive?(status.recording?(status.mode==="CONTINUOUS"?"상시 녹화":"움직임 이벤트 녹화 중"):"감시 중 / 녹화 대기"):"-");
    message("tabletMotion",alive?String(status.motionDetections||0)+"회 / "+(status.lastMotion||"기록 없음"):"-");
    message("tabletFiles",alive?String(status.localClips||0)+" / "+String(status.uploadedClips||0)+" / "+String(status.pendingUploads||0):"-");
    get("tabletStart").disabled=!user||!paired||!alive||Boolean(pending);
    get("tabletStop").disabled=!user||!paired||!alive||Boolean(pending);
    get("tabletPair").disabled=!user;
    get("tabletUnpair").disabled=!user||!paired;
    message("tabletOwnerUid",user?.uid||"로그인 후 표시");
    if(paired&&get("tabletDeviceUid").value!==configuredUid)get("tabletDeviceUid").value=configuredUid;
  }
  async function command(action){
    if(!user||!online()||pending)return;
    const seq=Math.max(Date.now(),lastSeq+1);lastSeq=seq;
    const cmd={id:crypto.randomUUID().replace(/-/g,""),seq,action,mode:get("tabletMode").value,requester:user.uid,issuedAt:Date.now()};
    pending=cmd;render();message("tabletFeedback","Y700의 실제 처리 결과를 기다리는 중…");
    try{
      await set(ref(db,devicePath()+"/command"),cmd);
      setTimeout(()=>{if(pending?.id===cmd.id){pending=null;message("tabletFeedback","확인 시간 초과. Y700 상태를 확인하세요.");render();}},60000);
    }catch(e){pending=null;render();message("tabletFeedback","전송 실패: "+(e.code||e.message));}
  }
  get("tabletStart").onclick=()=>command("START");
  get("tabletStop").onclick=()=>command("STOP");
  get("tabletPair").onclick=async()=>{
    if(!user)return;
    const deviceUid=get("tabletDeviceUid").value.trim();
    if(!/^[A-Za-z0-9_-]{15,128}$/.test(deviceUid)){message("tabletPairFeedback","Y700 앱의 전체 UID를 입력하세요.");return;}
    try{await set(ref(db,devicePath()+"/authUid"),deviceUid);message("tabletPairFeedback","기기 등록 완료. Y700 앱에서 감시 서비스를 켜주세요.");}
    catch(e){message("tabletPairFeedback","연결 실패: "+(e.code||e.message));}
  };
  get("tabletUnpair").onclick=async()=>{
    if(!user||!configuredUid||!confirm("Y700 원격 제어 권한을 해제할까요? 진행 중인 녹화는 별도로 중지해야 합니다."))return;
    try{await remove(ref(db,devicePath()+"/authUid"));message("tabletPairFeedback","연결 해제 완료");}
    catch(e){message("tabletPairFeedback","해제 실패: "+(e.code||e.message));}
  };
  setInterval(render,3000);
  return {setUser(next){
    cleanup();user=next||null;
    if(!user){render();return;}
    const root=devicePath();
    listeners.push(onValue(ref(db,root+"/authUid"),s=>{configuredUid=s.val();render();},e=>message("tabletPairFeedback","조회 실패: "+e.code)));
    listeners.push(onValue(ref(db,root+"/status"),s=>{status=s.val();render();},e=>message("tabletFeedback","상태 조회 실패: "+e.code)));
    listeners.push(onValue(ref(db,root+"/ack"),s=>{
      const ack=s.val();
      if(pending&&ack&&ack.id===pending.id&&Number(ack.seq)===pending.seq){
        if(ack.phase==="applied"||ack.phase==="failed"){
          message("tabletFeedback",ack.phase==="applied"?"Y700 처리 확인: "+ack.action:"처리 실패: "+(ack.detail||"원인 확인 필요"));
          pending=null;render();
        }else if(ack.phase==="queued")message("tabletFeedback","Y700가 명령을 받았습니다. 확인 중…");
      }
    },e=>message("tabletFeedback","명령 응답 실패: "+e.code)));
    render();
  }};
}