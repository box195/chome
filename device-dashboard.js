/**
 * Same layout for PC and Y700. Reuses the original action controls and video
 * nodes without changing their event handlers or Firebase authorization.
 */
export function createDeviceDashboard({tabletUI,getPcState,pcBridgeAlive}) {
 const $=id=>document.getElementById(id);
 const dash=$("dashboard");
 const pcPanel=$("status").closest("section.panel");
 const pcStats=$("pcMotionCount").closest("section.panel");
 const yPanel=$("tabletPanel");
 const oldLive=$("liveVideo-pc").closest("section.panel");
 if(!dash||!pcPanel||!pcStats||!yPanel||!oldLive)
   throw new Error("CHome: 기존 화면 요소를 찾지 못했습니다.");
 const board=document.createElement("section");
 board.className="camera-grid";board.id="cameraDashboard";
 const metrics=[["connection","연결 상태"],["camera","카메라 상태"],
  ["recording","감시·녹화 상태"],["mode","녹화 방식"],
  ["count","움직임 감지 횟수"],["last","최근 움직임 감지"],
  ["cloud","MYBOX 상태"],["pending","업로드 대기"]];
 function card(kind,title) {
   const article=document.createElement("article");
   article.className="panel device-card";
   article.dataset.device=kind;
   const stats=metrics.map(([key,label])=>
     '<div><small>'+label+'</small><strong id="summary-'+kind+'-'+key+'">-</strong></div>').join("");
   article.innerHTML='<header class="device-header"><div><h2>'+title+' 카메라</h2>'+
     '<p class="notice" id="summary-'+kind+'-updated">상태 확인 중</p></div>'+
     '<span class="device-badge" id="summary-'+kind+'-badge">연결 확인 중</span></header>'+
     '<div class="device-video" id="summary-'+kind+'-video"></div>'+
     '<div class="device-stats">'+stats+'</div>'+
     '<div class="device-controls" id="summary-'+kind+'-controls"></div>';
   return article;
 }
 board.append(card("pc","PC"),card("y700","Y700"));
 dash.insertBefore(board,dash.firstChild);
 function move(from,to,selector){
   const element=from.querySelector(selector);
   if(!element)throw new Error("CHome 제어 요소 누락: "+selector);
   to.appendChild(element);
 }
 for(const [source,kind,mode,feedback] of [
   [pcPanel,"pc","pcMode","feedback"],
   [yPanel,"y700","tabletMode","tabletFeedback"]
 ]) {
   const target=$("summary-"+kind+"-controls");
   move(source,target,'label[for="'+mode+'"]');
   move(source,target,"#"+mode);
   move(source,target,".buttons");
   move(source,target,"#"+feedback);
   if(kind==="y700")move(source,target,"details");
   const video=$("liveVideo-"+kind);
   $("summary-"+kind+"-video").appendChild(video.parentElement);
 }
 // Use exactly the same control labels on both device cards.
 for(const [kind,selector] of [["pc","pcMode"],["y700","tabletMode"]]){
   const modePicker=$(selector);
   for(const option of modePicker.options){
     if(option.value==="MOTION")option.textContent="움직임 감지 녹화";
     if(option.value==="CONTINUOUS")option.textContent="상시 녹화";
   }
 }
 for(const id of ["start","tabletStart"])$(id).textContent="시작";
 for(const id of ["stop","tabletStop"])$(id).textContent="중지";
 const details=$("tabletDeviceUid")?.closest("details");
 if(details){
   const summary=details.querySelector("summary");
   if(summary)summary.textContent="Y700 기기 연결 설정";
 }
 for(const old of [pcPanel,pcStats,yPanel,oldLive])old.classList.add("hidden");
 const style=document.createElement("style");
 style.textContent=[
   'main{max-width:1100px}',
   '.camera-grid{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:14px;align-items:stretch}',
   '.device-card{margin:9px 0;padding:17px;min-width:0;display:flex;flex-direction:column;gap:13px}',
   '.device-header{display:flex;justify-content:space-between;align-items:flex-start;gap:10px;min-height:60px}',
   '.device-header h2{margin:0 0 4px;font-size:1.13rem}',
   '.device-header p{margin:0;font-size:.78rem;line-height:1.4}',
   '.device-badge{display:inline-flex;align-items:center;text-align:center;justify-content:center;border:1px solid #52677e;border-radius:16px;padding:5px 9px;font-size:.75rem;max-width:45%;line-height:1.3;overflow-wrap:anywhere}',
   '.device-badge.online{color:#a4f0ca;border-color:#378865}',
   '.device-badge.offline{color:#ccd5e1;border-color:#52677e}',
   '.device-video{min-width:0}.device-video>div{width:100%;min-width:0}.device-video>div>strong{display:none}',
   '.device-video video{width:100%!important;aspect-ratio:16/9;min-height:0!important;max-height:none!important;object-fit:contain}',
   '.device-video button{width:100%;padding:10px}.device-video p{margin:7px 0 0;min-height:34px}',
   '.device-stats{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:8px}',
   '.device-stats>div{background:#0b1b2b;padding:11px;border-radius:10px;min-width:0}',
   '.device-stats small{font-size:.77rem;line-height:1.35}',
   '.device-stats strong{display:block;margin-top:6px;font-size:.89rem;font-weight:650;line-height:1.38;overflow-wrap:anywhere;white-space:normal}',
   '.device-controls{margin-top:auto;display:flex;flex-direction:column;gap:8px}',
   '.device-controls label{font-size:.86rem;color:#a8bdd4}',
   '.device-controls select{color:#e6f2ff;background:#0b1b2b;border:1px solid #476281;border-radius:10px;font:inherit;width:100%;min-width:0;padding:10px}',
   '.device-controls .buttons{margin-top:2px;grid-template-columns:repeat(2,minmax(0,1fr))}',
   '.device-controls .buttons button{width:100%;min-width:0;padding:11px 6px;font-size:.88rem}',
   '.device-controls>.notice{min-height:20px;margin:2px 0}',
   '.device-controls details{border-top:1px solid #344a65;padding-top:11px}',
   '.device-controls details summary{cursor:pointer;font-size:.89rem}',
   '.device-controls details input{margin-top:10px}',
   '@media(max-width:770px){main{max-width:560px}.camera-grid{grid-template-columns:1fr}.device-card{padding:16px;margin:6px 0}}'
 ].join("\n");
 document.head.appendChild(style);
 function time(value){
   if(!value)return "기록 없음";
   const date=new Date(value);
   return Number.isNaN(date.getTime())?String(value):date.toLocaleString("ko-KR");
 }
 const count=value=>value===null||value===undefined||!Number.isFinite(Number(value))?"-":String(value);
 const mode=value=>value==="MOTION"?"움직임 감지":value==="CONTINUOUS"?"상시 녹화":"꺼짐";
 function pc(){
   const data=getPcState(),relay=pcBridgeAlive(),online=relay&&data?.online===true;
   return {online,values:{
     badge:!relay?"연결 끊김":online?"온라인":"녹화기 꺼짐",
     updated:relay?"최근 상태: "+time(data?.updatedAt):"최근 상태 수신 대기",
     connection:!relay?"연결 끊김":online?"연결됨":"릴레이 연결 · 녹화기 꺼짐",
     camera:online?(data?.cameraConnected?"연결됨":"카메라 확인 필요"):"미실행",
     recording:!online?"감시 중단":data.mode==="MOTION"
       ?data.motionActive?"움직임 이벤트 녹화 중":"움직임 감시 중 · 평소 미녹화"
       :data.recording?"상시 녹화 중":"대기",
     mode:online?mode(data.mode):"꺼짐",count:count(data?.motionDetections),
     last:time(data?.lastMotion),
     cloud:online?(data?.cloudConnected?"연결됨":"연결 필요"):"녹화기 미실행",
     pending:count(data?.pendingUploads)
   }};
 }
 function y700(){
   const snapshot=tabletUI.getSnapshot(),data=snapshot.status;
   const paired=Boolean(snapshot.configuredUid);
   const fresh=data&&Number.isFinite(Number(data.bridgeAt))&&Math.abs(Date.now()-Number(data.bridgeAt))<20000;
   const online=Boolean(paired&&fresh&&data?.monitoring);
   return {online,values:{
     badge:!paired?"기기 미연결":online?"온라인":"오프라인",
     updated:fresh?"최근 상태: "+time(data?.bridgeAt):"최근 상태 수신 대기",
     connection:!paired?"기기 등록 필요":online?"연결됨":fresh?"감시 중단":"연결 끊김",
     camera:online?(data?.cameraConnected?"연결됨":"카메라 확인 필요"):"미실행",
     recording:!online?"감시 중단":data.recording
       ?data.mode==="MOTION"?"움직임 이벤트 녹화 중":"상시 녹화 중"
       :data.mode==="MOTION"?"움직임 감시 중 · 평소 미녹화":"녹화 대기",
     mode:online?mode(data?.mode):"꺼짐",count:count(data?.motionDetections),
     last:time(data?.lastMotion),
     cloud:online?(data?.cloudConnected?"연결됨":"연결 필요"):"기기 미실행",
     pending:count(data?.pendingUploads)
   }};
 }
 function refresh(){
   for(const [kind,read] of [["pc",pc],["y700",y700]]){
     const {values,online}=read();
     for(const [key,value] of Object.entries(values)){
       const node=$("summary-"+kind+"-"+key);
       node.textContent=value===null||value===undefined?"-":String(value);
     }
     const badge=$("summary-"+kind+"-badge");
     badge.classList.toggle("online",online);
     badge.classList.toggle("offline",!online);
   }
 }
 refresh();
 const timer=setInterval(refresh,1800);
 return {refresh,destroy(){clearInterval(timer)}};
}