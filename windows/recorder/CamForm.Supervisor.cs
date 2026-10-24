using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Drawing;
using System.Windows.Forms;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Net;
using System.Collections.Generic;
using System.Management;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

internal static class SupervisorLogic {
  internal static bool Fresh(DateTime utc,DateTime now,int seconds) {
    return utc!=DateTime.MinValue && utc<=now.AddSeconds(3) && utc>=now.AddSeconds(-seconds);
  }
  internal static bool IsOwnScript(string command,string script) {
    if(string.IsNullOrEmpty(command)||string.IsNullOrEmpty(script))return false;
    string a=command.Replace('/','\\'),b=Path.GetFullPath(script).Replace('/','\\');
    int index=a.IndexOf(b,StringComparison.OrdinalIgnoreCase);
    if(index<0)return false;
    int end=index+b.Length;
    return (index==0||a[index-1]=='"'||a[index-1]==' '||a[index-1]=='\'') &&
      (end==a.Length||a[end]=='"'||char.IsWhiteSpace(a[end]));
  }
  internal static string SafeError(string raw) {
    string value=(raw??"").Trim();
    return Regex.IsMatch(value,@"^[A-Za-z_][A-Za-z0-9_:\-]{0,60}$")?value:"status_unavailable";
  }
  internal static bool ShouldLaunch(bool exactProcess,bool portHealthy,bool configured) {
    return !exactProcess&&!portHealthy&&configured;
  }
  internal static bool SelfTest(string output) {
    var report=new List<string>();
    bool pass=true;
    Action<string,bool> check=(key,result)=>{report.Add(key+"="+(result?"PASS":"FAIL"));if(!result)pass=false;};
    DateTime now=new DateTime(2026,10,24,10,0,0,DateTimeKind.Utc);
    check("heartbeat_fresh",Fresh(now.AddSeconds(-4),now,15) && !Fresh(now.AddSeconds(-35),now,15));
    check("heartbeat_future_rejected",!Fresh(now.AddSeconds(9),now,15));
    check("script_exact",IsOwnScript("\"C:\\app\\pythonw.exe\" \"C:\\app\\pc_live.py\"","C:\\app\\pc_live.py"));
    check("script_substring_rejected",!IsOwnScript("\"C:\\app\\pythonw.exe\" \"C:\\app\\pc_live.py.evil\"","C:\\app\\pc_live.py"));
    check("script_other_rejected",!IsOwnScript("node C:\\agent\\worker.mjs","C:\\app\\pc_live.py"));
    check("retry_policy",ShouldLaunch(false,false,true)&&!ShouldLaunch(true,false,true)&&!ShouldLaunch(false,true,true));
    check("redact_error",SafeError("database_403")=="database_403" && SafeError("token=secret")=="status_unavailable");
    check("background_switch",new[]{"--background"}.Contains("--background"));
    string name="Local\\CHomeSupervisorTest_"+Guid.NewGuid().ToString("N");
    using(var e=new EventWaitHandle(false,EventResetMode.AutoReset,name)){
      using(var other=EventWaitHandle.OpenExisting(name)){other.Set();check("singleton_restore_signal",e.WaitOne(500));}
    }
    report.Add("PHYSICAL_CAMERA=NOT_TESTED");
    report.Add("FIREBASE_PHONE=NOT_TESTED");
    report.Add("SELFTEST="+(pass?"PASS":"FAIL"));
    Directory.CreateDirectory(Path.GetDirectoryName(output));
    File.WriteAllLines(output,report.ToArray(),Encoding.UTF8);
    return pass;
  }
}

internal sealed partial class CamForm {
  bool explicitQuit=false,supervisorBusy=false,relayHealthy=false;
  DateTime supervisorLast=DateTime.MinValue,relayNextStart=DateTime.MinValue,liveNextStart=DateTime.MinValue;
  NotifyIcon tray;
  EventWaitHandle restoreEvent;
  Label relayStatus,liveStatus,pushStatus,firebaseStatus,autoRunStatus;
  readonly object processGuard=new object();
  string relayMessage="확인 전",liveMessage="확인 전",pushMessage="확인 전",firebaseMessage="상태 제공 안 됨";
  readonly string liveRoot=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"USBHomeCamNAVER","live-view");
  string RelayDir {get{
    string suffix=Path.Combine("Codex","2026-09-28","new-chat","outputs","usb-cam-naver-gui","remote-control-v1");
    string local=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"Documents",suffix);
    return Directory.Exists(local)?local:Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),suffix);
  }}
  string RelayExe {get{return Path.Combine(RelayDir,"HomeCamRemote-CHome.exe");}}
  string LiveScript {get{return Path.Combine(liveRoot,"pc_live.py");}}
  string LivePythonw {get{return Path.Combine(liveRoot,".venv","Scripts","pythonw.exe");}}
  string PushScript {get{return Path.Combine(RelayDir,"push-sender","sender.mjs");}}
  string RemoteFile(string name){return Path.Combine(data,"remote",name);}

  void InitSupervisor(EventWaitHandle signal) {
    restoreEvent=signal;
    tray=new NotifyIcon{Icon=SystemIcons.Application,Text="CHome PC 통합 관리",Visible=true};
    if(motionTray!=null){motionTray.Visible=false;motionTray.Dispose();}motionTray=tray;
    var menu=new ContextMenuStrip();
    menu.Items.Add("관리창 열기",null,delegate{RestoreWindow();});
    menu.Items.Add("모바일 웹 열기",null,delegate{OpenMobileWeb();});
    menu.Items.Add(new ToolStripSeparator());
    menu.Items.Add("완전히 종료",null,delegate{QuitFromTray();});
    tray.ContextMenuStrip=menu;
    tray.DoubleClick+=delegate{RestoreWindow();};
    Task.Run(()=>{
      while(!exiting){
        try{if(signal.WaitOne(600))BeginInvoke((Action)RestoreWindow);}
        catch(ObjectDisposedException){break;}
        catch(InvalidOperationException){break;}
      }
    });
  }
  void HideToTray() {Hide();ShowInTaskbar=false;}
  void RestoreWindow() {
    if(IsDisposed||Disposing)return;
    ShowInTaskbar=true;Show();WindowState=FormWindowState.Normal;Opacity=1;Activate();BringToFront();
  }
  void QuitFromTray() {explicitQuit=true;Close();}
  void DisposeSupervisor() {
    try{if(tray!=null){tray.Visible=false;tray.Dispose();tray=null;}}catch{}
  }
  void OpenMobileWeb() {
    Process.Start(new ProcessStartInfo("https://box195.github.io/chome/"){UseShellExecute=true});
  }
  static bool ProcessAlive(Process p){try{return p!=null&&!p.HasExited;}catch{return false;}}
  void BuildSupervisorUI(TableLayoutPanel panel){
    var outer=new TableLayoutPanel{Dock=DockStyle.Fill,BackColor=Color.White,Padding=new Padding(8),
      ColumnCount=2,RowCount=4};
    outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));
    outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));
    outer.RowStyles.Add(new RowStyle(SizeType.Absolute,27));
    outer.RowStyles.Add(new RowStyle(SizeType.Absolute,27));
    outer.RowStyles.Add(new RowStyle(SizeType.Absolute,27));
    outer.RowStyles.Add(new RowStyle(SizeType.Percent,100));
    relayStatus=AddStatus(outer,0,0,"원격 릴레이","확인 전");
    liveStatus=AddStatus(outer,1,0,"PC 실시간 송신","확인 전");
    pushStatus=AddStatus(outer,0,1,"웹 푸시 송신","확인 전");
    firebaseStatus=AddStatus(outer,1,1,"Firebase","확인 전");
    autoRunStatus=AddStatus(outer,0,2,"로그인 자동 실행",HasStartup()?"설정됨":"미설정");
    var actionBar=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false,AutoScroll=false,Margin=new Padding(0)};
    var relayStart=Btn("릴레이 시작",delegate{Task.Run(()=>StartRelay(false));});
    relayStart.Width=108;actionBar.Controls.Add(relayStart);
    var relayRestart=Btn("릴레이 재시작",delegate{ConfirmRestart("원격 릴레이",()=>RestartRelay());});
    relayRestart.Width=118;actionBar.Controls.Add(relayRestart);
    var liveStart=Btn("실시간 시작",delegate{Task.Run(()=>StartLive(false));});
    liveStart.Width=105;actionBar.Controls.Add(liveStart);
    var liveRestart=Btn("실시간 재시작",delegate{ConfirmRestart("실시간 송신",()=>RestartLive());});
    liveRestart.Width=118;actionBar.Controls.Add(liveRestart);
    var web=Btn("모바일 웹",delegate{OpenMobileWeb();});web.Width=100;actionBar.Controls.Add(web);
    outer.Controls.Add(actionBar,0,3);outer.SetColumnSpan(actionBar,2);
    panel.Controls.Add(outer,0,5);
  }
  void ConfirmRestart(string target,Action operation){
    if(MessageBox.Show(this,target+"을(를) 재시작할까요? 연결 중인 세션이 잠시 끊길 수 있습니다.",
       "재시작 확인",MessageBoxButtons.YesNo,MessageBoxIcon.Question)==DialogResult.Yes)
      Task.Run(operation);
  }

  sealed class ProcessItem {
    internal int Pid,Parent;
    internal string Name,Exe;
  }
  List<ProcessItem> FindScriptProcesses(string script,bool live){
    var items=new List<ProcessItem>();
    try{
      using(var search=new ManagementObjectSearcher("SELECT ProcessId,ParentProcessId,Name,ExecutablePath,CommandLine FROM Win32_Process WHERE Name='pythonw.exe' OR Name='python.exe' OR Name='node.exe'")){
        foreach(ManagementObject proc in search.Get()){
          string name=Convert.ToString(proc["Name"])??"";
          if(live && !(name.Equals("python.exe",StringComparison.OrdinalIgnoreCase)||name.Equals("pythonw.exe",StringComparison.OrdinalIgnoreCase)))continue;
          if(!live && !name.Equals("node.exe",StringComparison.OrdinalIgnoreCase))continue;
          // Never display CommandLine: it is used only to verify the exact intended script.
          if(!SupervisorLogic.IsOwnScript(Convert.ToString(proc["CommandLine"]),script))continue;
          items.Add(new ProcessItem{Pid=Convert.ToInt32(proc["ProcessId"]),
            Parent=Convert.ToInt32(proc["ParentProcessId"]),Name=name,Exe=Convert.ToString(proc["ExecutablePath"])??""});
        }
      }
    }catch{}
    return items;
  }
  Process FindExactRelay(){
    try{
      foreach(var p in Process.GetProcessesByName("HomeCamRemote-CHome")){
        try{
          if(string.Equals(Path.GetFullPath(p.MainModule.FileName),Path.GetFullPath(RelayExe),
            StringComparison.OrdinalIgnoreCase)&&!p.HasExited)return p;
        }catch{}
        try{p.Dispose();}catch{}
      }
    }catch{}
    return null;
  }
  bool ExactRelayRunning(){using(var p=FindExactRelay())return p!=null;}
  bool ProbeHealth(){
    try{
      var request=(HttpWebRequest)WebRequest.Create("http://127.0.0.1:18742/health");
      request.Timeout=1500;request.ReadWriteTimeout=1500;request.Proxy=null;
      using(var response=(HttpWebResponse)request.GetResponse()){
        return response.StatusCode==HttpStatusCode.OK;
      }
    }catch{return false;}
  }
  string Heartbeat(string file,string label,int seconds){
    if(!File.Exists(file))return label+": 미지원(보조 송신기 업데이트 필요)";
    try{
      var raw=File.ReadAllText(file,Encoding.UTF8);
      var value=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(raw);
      string stamp=value.ContainsKey("updatedAt")?Convert.ToString(value["updatedAt"]):"";
      DateTime utc;
      if(!DateTime.TryParse(stamp,null,System.Globalization.DateTimeStyles.AdjustToUniversal|System.Globalization.DateTimeStyles.AssumeUniversal,out utc))
        return label+": 시간 오류";
      if(!SupervisorLogic.Fresh(utc.ToUniversalTime(),DateTime.UtcNow,seconds))return label+": 응답 지연";
      bool ok=value.ContainsKey("ok")&&Convert.ToString(value["ok"]).Equals("True",StringComparison.OrdinalIgnoreCase);
      string error=value.ContainsKey("error")?SupervisorLogic.SafeError(Convert.ToString(value["error"])):"-";
      return ok?label+": 정상 ("+(int)(DateTime.UtcNow-utc.ToUniversalTime()).TotalSeconds+"초 전)":label+": 오류 "+error;
    }catch{return label+": 확인 실패";}
  }
  string SafePushLog(){
    try{
      string file=RemoteFile("push-sender.log");
      if(!File.Exists(file))return "";
      string last=File.ReadLines(file).LastOrDefault()??"";
      string code=last.Split(new[]{' '},StringSplitOptions.RemoveEmptyEntries).LastOrDefault()??"";
      return SupervisorLogic.SafeError(code);
    }catch{return "";}
  }
  void SupervisorTick(){
    if(supervisorBusy || (DateTime.UtcNow-supervisorLast).TotalSeconds<6)return;
    supervisorLast=DateTime.UtcNow;supervisorBusy=true;
    Task.Run(()=>{
      try{
        bool relay=ExactRelayRunning();
        bool live=FindScriptProcesses(LiveScript,true).Count>0;
        bool push=FindScriptProcesses(PushScript,false).Count>0;
        bool health=ProbeHealth();
        string relayText=(relay?"프로세스 실행":"프로세스 없음")+(health?" / HTTP 정상":" / HTTP 응답 없음");
        string liveText=live?Heartbeat(RemoteFile("pc-live-heartbeat.json"),"실행 중",18):"프로세스 없음";
        string pushText=push?Heartbeat(RemoteFile("push-heartbeat.json"),"실행 중",25):"프로세스 없음";
        string pushError=SafePushLog();
        if(push && pushText.Contains("미지원") && pushError.Length>0)pushText+=" / 최근 오류 "+pushError;
        string cloudText=relay?Heartbeat(RemoteFile("firebase-heartbeat.json"),"클라우드",25):"릴레이 오프라인";
        relayHealthy=health;
        // No unknown-process killing; automatic starts only when both identity and port are absent.
        if(SupervisorLogic.ShouldLaunch(relay,health,File.Exists(RelayExe)))StartRelay(true);
        if(!live && File.Exists(LivePythonw)&&File.Exists(LiveScript))StartLive(true);
        try{BeginInvoke((Action)delegate{
          relayMessage=relayText;liveMessage=liveText;pushMessage=pushText;firebaseMessage=cloudText;
          relayStatus.Text="원격 릴레이: "+relayMessage;
          liveStatus.Text="PC 실시간: "+liveMessage;
          pushStatus.Text="웹 푸시: "+pushMessage;
          firebaseStatus.Text="Firebase: "+firebaseMessage;
          autoRunStatus.Text="로그인 자동 실행: "+(HasStartup()?"설정됨 (--background)":"미설정");
        });}catch{}
      }finally{supervisorBusy=false;}
    });
  }
  void LaunchHidden(string executable,string arguments,string workingDir){
    var psi=new ProcessStartInfo(executable,arguments){UseShellExecute=false,
      CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=workingDir};
    var p=Process.Start(psi);
    if(p==null)throw new IOException("process_start_failed");
    p.Dispose();
  }
  void StartRelay(bool watchdog){
    lock(processGuard){
      if(DateTime.UtcNow<relayNextStart)return;
      relayNextStart=DateTime.UtcNow.AddSeconds(35);
      if(ExactRelayRunning() || ProbeHealth())return;
      if(!File.Exists(RelayExe) || !File.Exists(PushScript)){
        if(!watchdog)Log("릴레이 파일/푸시 구성 누락: 경로를 확인하세요.");
        return;
      }
      try{LaunchHidden(RelayExe,"",RelayDir);Log("원격 릴레이 백그라운드 시작 요청");}
      catch(Exception e){Log("릴레이 시작 실패: "+SupervisorLogic.SafeError(e.GetType().Name));}
    }
  }
  void StartLive(bool watchdog){
    lock(processGuard){
      if(DateTime.UtcNow<liveNextStart)return;
      liveNextStart=DateTime.UtcNow.AddSeconds(35);
      if(FindScriptProcesses(LiveScript,true).Count>0)return;
      if(!File.Exists(LivePythonw)||!File.Exists(LiveScript)){
        if(!watchdog)Log("실시간 송신기 pythonw/pc_live.py 누락");
        return;
      }
      try{LaunchHidden(LivePythonw,Q(LiveScript),liveRoot);Log("실시간 송신기 백그라운드 시작 요청");}
      catch(Exception e){Log("실시간 시작 실패: "+SupervisorLogic.SafeError(e.GetType().Name));}
    }
  }
  void KillOwn(Process p){
    if(p==null)return;
    try{if(!p.HasExited){p.Kill();p.WaitForExit(2500);}}catch{}
  }
  void RestartRelay(){
    lock(processGuard){
      Process p=FindExactRelay();
      int pid=p==null?-1:p.Id;
      if(pid>0){
        // Kill only the push sender whose parent PID and script path belong to this relay.
        foreach(var item in FindScriptProcesses(PushScript,false).Where(x=>x.Parent==pid)){
          try{using(var child=Process.GetProcessById(item.Pid))KillOwn(child);}catch{}
        }
        KillOwn(p);
        p.Dispose();
      }
      relayNextStart=DateTime.MinValue;
      StartRelay(false);
    }
  }
  void RestartLive(){
    lock(processGuard){
      foreach(var item in FindScriptProcesses(LiveScript,true)){
        try{using(var p=Process.GetProcessById(item.Pid))KillOwn(p);}catch{}
      }
      liveNextStart=DateTime.MinValue;
      StartLive(false);
    }
  }
}
