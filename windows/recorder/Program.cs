using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Drawing;
using System.Windows.Forms;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Reflection;
using Microsoft.Win32;

internal static class Entry {
 internal static bool AutoMotionIntegrationTest=false;
 [STAThread] static void Main(string[] args) {
  if(args.Length>0 && args[0]=="--selftest") {Environment.Exit(SelfTest()?0:1);return;}
   if(args.Length>0 && args[0]=="--motion-selftest") {string a,b;bool aok=FixedCameraMotion.SelfTest(out a);bool bok=MotionWindow.SelfTest(out b);Console.WriteLine("MOTION_DETECTOR: "+a);Console.WriteLine("MOTION_WINDOW: "+b);Environment.Exit(aok&&bok?0:1);return;}
  if(args.Contains("--motion-test-live"))AutoMotionIntegrationTest=true;
   bool created;
  using(var mutex=new Mutex(true,@"Local\USBHomeCamNAVERSingleInstance",out created)) {
   if(!created){MessageBox.Show("USB HomeCam NAVER이 이미 실행 중입니다.");return;}
  Application.EnableVisualStyles();
  Application.SetCompatibleTextRenderingDefault(false);
  Application.Run(new CamForm());
 }
}

 static bool SelfTest() {
  string folder=Path.Combine(Path.GetTempPath(),"USBHomeCam-selftest-"+Guid.NewGuid().ToString("N"));
  Directory.CreateDirectory(folder);
  string report=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"USBHomeCamNAVER","logs","selftest-last.txt");
  var lines=new List<string>();
  try{
    string ff=Path.Combine(folder,"ffmpeg.exe");
    foreach(var name in new[]{"ffmpeg.exe"}){
      using(var input=Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
      using(var output=File.Create(Path.Combine(folder,name))) {
       if(input==null)throw new Exception("Missing embedded resource: "+name);
       input.CopyTo(output);
      }
    }
    lines.Add("EXTRACT=PASS");
    var p=new Process();
    string outputName=Path.Combine(folder,"part%03d.ts");
    string args="-hide_banner -loglevel error -f lavfi -i testsrc2=size=320x180:rate=15 -t 8 -c:v libx264 -preset ultrafast -force_key_frames \"expr:gte(t,n_forced*3)\" -f segment -segment_time 3 -reset_timestamps 1 -segment_format mpegts \""+outputName+"\"";
    p.StartInfo=new ProcessStartInfo(ff,args){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true};
    p.Start();string err=p.StandardError.ReadToEnd();p.WaitForExit(60000);
    if(!p.HasExited || p.ExitCode!=0)throw new Exception("FFMPEG: "+err);
    var videos=Directory.GetFiles(folder,"*.ts");
    if(videos.Length<2)throw new Exception("Only "+videos.Length+" video segments");
    lines.Add("SEGMENTS="+videos.Length);
    foreach(var video in videos){
      string copy=Path.Combine(folder,"copy-"+Path.GetFileName(video));
      File.Copy(video,copy);
      using(var md5=MD5.Create()){
        byte[] a,b;
        using(var fa=File.OpenRead(video))a=md5.ComputeHash(fa);
        using(var fb=File.OpenRead(copy))b=md5.ComputeHash(fb);
        if(!a.SequenceEqual(b))throw new Exception("MD5 mismatch");
      }
    }
    lines.Add("LOCAL_COPY_CHECKSUM=PASS");
    CloudBundler.Pack(ff,videos.OrderBy(x=>x).Take(2).Select(x=>new FileInfo(x)).ToList(),Path.Combine(folder,"bundled20.ts"));
    if(new FileInfo(Path.Combine(folder,"bundled20.ts")).Length<1000)throw new IOException("Cloud bundling failed");
    lines.Add("CLOUD_BUNDLER=PASS");
    lines.Add("MYBOX_AUTH=NOT_TESTED");
    lines.Add("REAL_CAMERA=NOT_TESTED");
    lines.Add("SELFTEST=PASS");
    return true;
  }catch(Exception e){lines.Add("SELFTEST=FAIL "+e.ToString());return false;}
  finally{Directory.CreateDirectory(Path.GetDirectoryName(report));File.WriteAllLines(report,lines);try{Directory.Delete(folder,true);}catch{}}
 }
}

internal sealed partial class CamForm : Form {
 readonly string data=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"USBHomeCamNAVER");
 readonly string bin,config,logs,ffmpeg,settings;
 readonly object logLock=new object();
 readonly System.Windows.Forms.Timer tick=new System.Windows.Forms.Timer();
 Label cameraLabel,recordLabel,driveLabel,uploadLabel,statsLabel,diskLabel,pathLabel,heartbeatLabel;
 ComboBox cameraBox;
 TextBox logBox;
 Button startButton,stopButton,loginButton,folderButton,refreshButton,viewButton;
 CheckBox autoStart;
 string recordRoot,remote="gdrive",remoteFolder="HomeCam/USB-Camera-01";
 string cameraName="",lastUpload="-",lastError="-";
 Process capture;
 int lastCaptureExit=0,completedUploads=0,failedUploads=0;
 bool recording=false,loggedIn=false,loginBusy=false,uploadBusy=false,scanning=false,exiting=false,settingStartup=false,cloudCheckBusy=false,autoRecordPending=true;
 DateTime lastCameraScan=DateTime.MinValue,lastCloudProbe=DateTime.MinValue,lastUploadAttempt=DateTime.MinValue,lastCameraStart=DateTime.MinValue,lastCaptureWarning=DateTime.MinValue;
 Task uploader,authTask;
 CancellationTokenSource cancel=new CancellationTokenSource();
 string lastCaptureFile="";
 readonly object captureLock=new object();
 const int chunkSeconds=1;
 const int stableSeconds=7;
 const int minDiskGb=15;

 public CamForm() {
  bin=Path.Combine(data,"bin"); config=Path.Combine(data,"auth","naver-pat.dat");
  logs=Path.Combine(data,"logs"); settings=Path.Combine(data,"settings.txt");
  ffmpeg=Path.Combine(bin,"ffmpeg.exe");
  recordRoot=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),"USBHomeCam");
  Directory.CreateDirectory(data); Directory.CreateDirectory(bin);Directory.CreateDirectory(logs);Directory.CreateDirectory(Path.GetDirectoryName(config));
  ReadSettings();InitRemoteControl();LoadRecordingMode();
  Text="USB HomeCam NAVER | USB 카메라 실시간 녹화 + 네이버 MYBOX 백업";
  Font=new Font("Malgun Gothic",9F); Size=new Size(860,715);MinimumSize=new Size(720,610);
  StartPosition=FormStartPosition.CenterScreen;
  BackColor=Color.FromArgb(247,249,253);
  BuildUI();
  Shown+=delegate {
    BeginInit();
  };
  FormClosing+=OnFormClosing;
  tick.Interval=500;
  tick.Tick+=delegate { OnTick(); };
  tick.Start();
 }
 void BuildUI(){
  var panel=new TableLayoutPanel { Dock=DockStyle.Fill,Padding=new Padding(14),ColumnCount=1,RowCount=6,AutoScroll=true };
  panel.RowStyles.Add(new RowStyle(SizeType.Absolute,55));
  panel.RowStyles.Add(new RowStyle(SizeType.Absolute,92));
  panel.RowStyles.Add(new RowStyle(SizeType.Absolute,136));
  panel.RowStyles.Add(new RowStyle(SizeType.Absolute,58));
  panel.RowStyles.Add(new RowStyle(SizeType.Absolute,45));
  panel.RowStyles.Add(new RowStyle(SizeType.Percent,100));
  Controls.Add(panel);
  var header=new Label {Dock=DockStyle.Fill,Text="USB HomeCam NAVER     |     PC 켜진 동안 연속 녹화 → 네이버 MYBOX 자동 백업",Font=new Font("Malgun Gothic",12F,FontStyle.Bold),ForeColor=Color.FromArgb(25,48,83),TextAlign=ContentAlignment.MiddleLeft};
  panel.Controls.Add(header,0,0);
  var selection=new TableLayoutPanel { Dock=DockStyle.Fill, ColumnCount=5,RowCount=2,Padding=new Padding(8),BackColor=Color.White };
  selection.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,104));
  selection.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
  selection.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,102));
  selection.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,126));
  selection.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,98));
  selection.Controls.Add(new Label {Text="카메라",Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft},0,0);
  cameraBox=new ComboBox {Dock=DockStyle.Fill,DropDownStyle=ComboBoxStyle.DropDownList,Margin=new Padding(4,4,4,4)};
  cameraBox.SelectedIndexChanged+=delegate { if(cameraBox.SelectedItem!=null) {cameraName=cameraBox.SelectedItem.ToString();WriteSettings();RefreshLabels();}};
  selection.Controls.Add(cameraBox,1,0);
  refreshButton=Btn("다시 찾기",delegate{ScanCameras();});
  selection.Controls.Add(refreshButton,2,0);
  startButton=Btn("녹화 시작",delegate{StartRecording();});selection.Controls.Add(startButton,3,0);
  stopButton=Btn("녹화 중지",delegate{StopRecording();});selection.Controls.Add(stopButton,4,0);
  cameraLabel=new Label {Dock=DockStyle.Fill,ForeColor=Color.Gray,Text="카메라 검색 중...",TextAlign=ContentAlignment.MiddleLeft};
  selection.SetColumnSpan(cameraLabel,5);selection.Controls.Add(cameraLabel,0,1);
  panel.Controls.Add(selection,0,1);

  var states=new TableLayoutPanel {Dock=DockStyle.Fill, ColumnCount=2,RowCount=4,Padding=new Padding(10),BackColor=Color.White};
  states.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));states.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));
  recordLabel=AddStatus(states,0,0,"녹화", "정지"); driveLabel=AddStatus(states,1,0,"네이버 MYBOX", "로그인 전");
  uploadLabel=AddStatus(states,0,1,"자동 업로드","대기");
  statsLabel=AddStatus(states,1,1,"업로드 성공/실패","0 / 0");
  diskLabel=AddStatus(states,0,2,"디스크 여유 공간","확인 중");
  heartbeatLabel=AddStatus(states,1,2,"최근 녹화 파일","없음");
  pathLabel=AddStatus(states,0,3,"녹화 저장 폴더",recordRoot);
  states.SetColumnSpan(pathLabel,2);panel.Controls.Add(states,0,2);

  var tools=new FlowLayoutPanel {Dock=DockStyle.Fill,WrapContents=false,AutoScroll=true,Padding=new Padding(3,10,0,4)};
  loginButton=Btn("MYBOX 토큰 등록",delegate{ConnectGoogle();});loginButton.Width=165;tools.Controls.Add(loginButton);
  folderButton=Btn("저장 폴더 변경",delegate{ChooseFolder();});folderButton.Width=140;tools.Controls.Add(folderButton);
  viewButton=Btn("녹화 폴더 열기",delegate{OpenFolder();});viewButton.Width=140;tools.Controls.Add(viewButton);
  var logsOpen=Btn("로그 폴더 열기",delegate{Process.Start("explorer.exe",logs);}); logsOpen.Width=140; tools.Controls.Add(logsOpen);
  panel.Controls.Add(tools,0,3);

  var settingsPanel=new FlowLayoutPanel {Dock=DockStyle.Fill,Padding=new Padding(4,5,0,2),WrapContents=false};
  autoStart=new CheckBox {Text="Windows 로그인 시 자동 실행 및 녹화",Width=300,AutoSize=false};
  autoStart.Checked=HasStartup();autoStart.CheckedChanged+=delegate{if(!settingStartup) SetStartup(autoStart.Checked);};
  settingsPanel.Controls.Add(autoStart);
  var note=new Label {Text="로컬 영상 자동 삭제 안 함 · 10초 TS 조각 · 클라우드 HTTP 업로드 완료 확인",Width=470,Height=27,TextAlign=ContentAlignment.MiddleLeft,ForeColor=Color.FromArgb(89,106,125)};
  settingsPanel.Controls.Add(note);SetupMotionUI(tools,settingsPanel);SetupRecordingModeUI(tools);panel.Controls.Add(settingsPanel,0,4);
  var logPanel=new Panel{Dock=DockStyle.Fill,BackColor=Color.White,Padding=new Padding(10)};
  var label=new Label{Text="상세 작업 로그 (인증 토큰·비밀번호는 표시하지 않음)",Dock=DockStyle.Top,Height=24,ForeColor=Color.FromArgb(30,54,81)};
  logBox=new TextBox{Dock=DockStyle.Fill,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,BackColor=Color.White,ForeColor=Color.FromArgb(29,50,70),Font=new Font("Consolas",9F),WordWrap=false};
  logPanel.Controls.Add(logBox);logPanel.Controls.Add(label);panel.Controls.Add(logPanel,0,5);
  RefreshLabels();
 }
 Button Btn(string name,Action action){
  var b=new Button{Text=name,Height=34,Width=99,FlatStyle=FlatStyle.Flat,BackColor=Color.FromArgb(237,244,252),ForeColor=Color.FromArgb(23,68,121),Margin=new Padding(4,2,4,2)};
  b.FlatAppearance.BorderColor=Color.FromArgb(190,211,236);b.Click+=delegate{try{action();}catch(Exception e){Error(e.Message);}}; return b;
 }
 Label AddStatus(TableLayoutPanel p,int x,int y,string key,string val){
  var l=new Label{Dock=DockStyle.Fill,Text=key+" : "+val,TextAlign=ContentAlignment.MiddleLeft,AutoEllipsis=true,Padding=new Padding(4,1,4,1)};
  p.Controls.Add(l,x,y);return l;
 }
 void Log(string msg){
  if(exiting) return;
  string line=DateTime.Now.ToString("HH:mm:ss")+"  "+msg;
  lock(logLock){try{File.AppendAllText(Path.Combine(logs,DateTime.Now.ToString("yyyyMMdd")+".log"),line+Environment.NewLine,Encoding.UTF8);}catch{}}
  if(InvokeRequired){try{BeginInvoke(new Action<string>(AppendLog),line);}catch{}}else AppendLog(line);
 }
 void AppendLog(string line){
  if(logBox.IsDisposed) return;
  logBox.AppendText(line+Environment.NewLine);
  if(logBox.TextLength>33000){logBox.Text=logBox.Text.Substring(logBox.TextLength-22000);logBox.SelectionStart=logBox.TextLength;logBox.ScrollToCaret();}
 }
 void Error(string message){lastError=message;Log("오류: "+message);RefreshLabels();}
 void RefreshLabels(){
  if(InvokeRequired){try{BeginInvoke(new Action(RefreshLabels));}catch{}return;}
  if(IsDisposed)return;
  cameraLabel.Text=string.IsNullOrEmpty(cameraName)?"USB 카메라 연결 필요":"USB 카메라: "+cameraName+(OldVersionRunning()?" (기존 Google 앱 먼저 종료)":"");
  recordLabel.Text="녹화: "+(recording ? (capture!=null&&!capture.HasExited ? "● 진행 중":"카메라 재접속 대기"):"■ 정지");
  recordLabel.ForeColor=recording?Color.FromArgb(7,120,71):Color.FromArgb(140,55,55);
  driveLabel.Text=loginBusy?"MYBOX: 토큰 확인 중":"MYBOX: "+cloudDescription;
  driveLabel.ForeColor=loggedIn?Color.FromArgb(7,120,71):Color.FromArgb(145,95,32);
  uploadLabel.Text="MYBOX: "+(!loggedIn?"토큰 확인 필요":uploadBusy?"업로드 중":"대기 "+pendingUploads+"개 / "+oldestQueueMinutes.ToString("0")+"분");
  statsLabel.Text="업로드 "+completedUploads+"건 / 실패 "+failedUploads+(uploadDelaySamples>0?" | 최근 지연 "+lastArrivalDelaySeconds.ToString("0.0")+"초":"");
  pathLabel.Text="로컬 저장: "+recordRoot;
  heartbeatLabel.Text="최근 녹화: "+(lastCaptureFile.Length>0?lastCaptureFile:"아직 없음");
  startButton.Enabled=!recording && !string.IsNullOrEmpty(cameraName);
  stopButton.Enabled=recording;loginButton.Enabled=!loginBusy;
  try{
    var root=Path.GetPathRoot(recordRoot);
    var d=new DriveInfo(root);
    diskLabel.Text="디스크 여유: "+(d.AvailableFreeSpace/(1024*1024*1024)).ToString("N1")+" GB";
  }catch{diskLabel.Text="디스크: 저장장치 연결 확인 필요";}
 }
 void ReadSettings(){
  if(!File.Exists(settings))return;
  try{foreach(var x in File.ReadAllLines(settings,Encoding.UTF8)){
   int i=x.IndexOf('=');if(i<1)continue; string k=x.Substring(0,i),v=x.Substring(i+1);
   if(k=="root")recordRoot=v;else if(k=="camera")cameraName=v;
  }}catch{}
 }
 void WriteSettings(){try{File.WriteAllText(settings,"root="+recordRoot+Environment.NewLine+"camera="+cameraName+Environment.NewLine,Encoding.UTF8);}catch{}}
 static string Q(string s){return "\""+s.Replace("\"","\\\"")+"\"";}
 void EnsureTools(){
  var assembly=System.Reflection.Assembly.GetExecutingAssembly();
  foreach(var name in new[]{ "ffmpeg" }){
    var path=Path.Combine(bin,name+".exe");
    if(File.Exists(path) && new FileInfo(path).Length>1000000)continue;
    using(var src=assembly.GetManifestResourceStream(name+".exe")){
     if(src==null)throw new Exception("내장 실행 도구를 찾을 수 없습니다: "+name);
     var tmp=path+".new";
     using(var dst=File.Create(tmp))src.CopyTo(dst);
     if(File.Exists(path))File.Delete(path);
     File.Move(tmp,path);
    }
  }
 }
 async void BeginInit(){
  Log("USB HomeCam NAVER 시작. 도구 확인 중...");
  try{await Task.Run((Action)EnsureTools);}catch(Exception ex){Error("초기 설정 실패: "+ex.Message);return;}
  Log("내장 녹화 도구 준비 완료.");
  StartSegmentWatcher();
  ScanCameras();
  CheckGoogleConnection();

  RefreshLabels();
 }
 void ScanCameras(){
  if(scanning || !File.Exists(ffmpeg))return;
  scanning=true;
  Task.Run(()=>{
   try{
    var p=new Process();
    p.StartInfo=new ProcessStartInfo(ffmpeg,"-hide_banner -list_devices true -f dshow -i dummy") {UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true};
    p.Start();string text=p.StandardError.ReadToEnd();p.WaitForExit(12000);
    if(!p.HasExited)try{p.Kill();}catch{}
    var found=Regex.Matches(text,"\"([^\"]+)\"\\s*\\(video\\)",RegexOptions.IgnoreCase).Cast<Match>().Select(m=>m.Groups[1].Value).Distinct().ToList();
    BeginInvoke((Action)delegate {
      string prior=cameraName;
      cameraBox.Items.Clear();
      foreach(var n in found)cameraBox.Items.Add(n);
      if(found.Count>0){
        var choice=found.Contains(prior)?prior:found[0];
        cameraBox.SelectedItem=choice;cameraName=choice;WriteSettings();
        Log("USB 비디오 장치 "+found.Count+"개 감지, "+cameraName+" 자동 선택.");
      }else{
        cameraName="";Log("연결된 USB 비디오 장치 없음.");
      }
      lastCameraScan=DateTime.Now;scanning=false;RefreshLabels();
      if(autoRecordPending && !recording && !string.IsNullOrEmpty(cameraName) && !OldVersionRunning())StartRecording();
    });
   }catch(Exception e){try{BeginInvoke((Action)delegate{scanning=false;Error("카메라 검색 실패: "+e.Message);});}catch{}}
  });
 }
 static string Limit(string x,int n){return x.Length>n?x.Substring(0,n)+"...":x;}
 void ChooseFolder(){
  if(recording){MessageBox.Show("녹화를 중지한 다음 폴더를 변경하세요.");return;}
  using(var dlg=new FolderBrowserDialog()){dlg.Description="영상이 계속 저장될 폴더 선택 (여유 공간 확인)";
    if(Directory.Exists(recordRoot))dlg.SelectedPath=recordRoot;
    if(dlg.ShowDialog(this)==DialogResult.OK){recordRoot=dlg.SelectedPath;WriteSettings();Log("저장 폴더 변경: "+recordRoot);RefreshLabels();}
  }
 }
 void OpenFolder(){Directory.CreateDirectory(recordRoot);Process.Start("explorer.exe",recordRoot);}
 bool HasStartup(){
  try{using(var k=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")){
   var v=k==null?"":(k.GetValue("USBHomeCamNAVER") as string ?? "");
   return v.Contains(Application.ExecutablePath);
  }}catch{return false;}
 }
 void SetStartup(bool yes){
  try{using(var key=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run",true)){
    if(yes)key.SetValue("USBHomeCamNAVER",Q(Application.ExecutablePath));
    else key.DeleteValue("USBHomeCamNAVER",false);
    Log(yes?"Windows 로그인 시 자동 실행 설정됨.":"자동 실행 해제됨.");
   }}catch(Exception ex){settingStartup=true;autoStart.Checked=!yes;settingStartup=false;Error("자동 실행 설정 실패: "+ex.Message);}
 }
 void StartRecording(){
  if(recording)return;
  if(string.IsNullOrEmpty(cameraName)){Log("카메라가 연결되지 않았습니다.");return;}
  Directory.CreateDirectory(recordRoot);
  try{
   var d=new DriveInfo(Path.GetPathRoot(recordRoot));if(d.AvailableFreeSpace<minDiskGb*1024L*1024*1024){Error("디스크 여유 공간이 15GB 미만이라 녹화를 시작할 수 없습니다.");return;}
  }catch(Exception ex){Error("저장 디스크 확인 실패: "+ex.Message);return;}
  recording=true;autoRecordPending=false;PersistRemoteDesired("START");Log("연속 녹화 시작. 10초 TS 파일 분할, 클라우드 자동 백업.");
  StartCapture();RefreshLabels();
   if(Entry.AutoMotionIntegrationTest){Entry.AutoMotionIntegrationTest=false;Task.Run(()=>{Thread.Sleep(32000);try{BeginInvoke((Action)delegate{if(recording)ForceMotionTest();});}catch{}});}
 }
 void StartCapture(){
  if(!recording || capture!=null&&!capture.HasExited)return;
  var day=DateTime.Now.ToString("yyyy-MM-dd");
  var dir=Path.Combine(recordRoot,motionOnly?"raw-motion":"raw",day);Directory.CreateDirectory(dir);
  string file=Path.Combine(dir,"%Y-%m-%d_%H-%M-%S.ts");
  string arg="-nostdin -hide_banner -loglevel warning -f dshow -i "+Q("video="+cameraName)+
   " -an -c:v libx264 -preset ultrafast -tune zerolatency -crf 28 -pix_fmt yuv420p "+
   "-force_key_frames "+Q("expr:gte(t,n_forced*1)")+
   " -f segment -segment_time "+chunkSeconds+" -reset_timestamps 1 -strftime 1 -segment_format mpegts "+Q(file);
  var p=new Process();
  p.StartInfo=new ProcessStartInfo(ffmpeg,arg){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true};
  p.ErrorDataReceived+=delegate(object s,DataReceivedEventArgs e){
   if(e.Data==null||string.IsNullOrEmpty(e.Data))return;
   if(e.Data.IndexOf("frame=",StringComparison.OrdinalIgnoreCase)>=0)return;
   if(e.Data.IndexOf("error",StringComparison.OrdinalIgnoreCase)>=0||e.Data.IndexOf("could not",StringComparison.OrdinalIgnoreCase)>=0
    ||e.Data.IndexOf("failed",StringComparison.OrdinalIgnoreCase)>=0)Log("카메라: "+Limit(e.Data,180));
  };
  try{
   if(p.Start()){p.BeginErrorReadLine();p.BeginOutputReadLine();capture=p;lastCameraStart=DateTime.Now;Log("카메라 녹화 프로세스 실행됨: "+cameraName);}
  }catch(Exception e){Error("녹화 시작 실패: "+e.Message);recording=false;}
 }
 void StopRecording(){
  if(!recording)return;recording=false;autoRecordPending=false;PersistRemoteDesired("STOP");
  try{if(capture!=null && !capture.HasExited)capture.Kill();}catch{}
  Log("녹화 중지. 기존 로컬/클라우드 영상은 삭제하지 않습니다.");
  RefreshLabels();
 }
 void OnTick(){
  if(exiting)return;
  if(!scanning && !recording && (DateTime.Now-lastCameraScan).TotalSeconds>25 && string.IsNullOrEmpty(cameraName)) ScanCameras();
  if(recording){
    try{
     var d=new DriveInfo(Path.GetPathRoot(recordRoot));
     if(d.AvailableFreeSpace<minDiskGb*1024L*1024*1024){Error("디스크 부족: 녹화를 안전하게 중지합니다.");StopRecording();}
     else {
      bool crashed=capture==null || capture.HasExited;
      bool stalled=false;
      var dir=Path.Combine(recordRoot,"raw",DateTime.Now.ToString("yyyy-MM-dd"));
      if(Directory.Exists(dir)){
       FileInfo newest=null; string live; lock(segmentSync){live=activeSegmentPath;} if(!string.IsNullOrEmpty(live) && File.Exists(live))newest=new FileInfo(live);
       if(newest!=null){
        lastCaptureFile=newest.Name;
        if((DateTime.Now-lastVideoEvent).TotalSeconds>25 && (DateTime.Now-lastCameraStart).TotalSeconds>25)stalled=true;
       }
      }
      if((crashed||stalled)&&(DateTime.Now-lastCameraStart).TotalSeconds>8){
       if(stalled){try{capture.Kill();}catch{}Log("녹화 정지 감지: 카메라를 다시 연결합니다.");}
       if(crashed)Log("카메라 연결 끊김: 8초 간격으로 자동 재시도합니다.");
       lastCameraStart=DateTime.Now;
       StartCapture();
      }
      if(DateTime.Now.Date!=lastCameraStart.Date && capture!=null&&!capture.HasExited){
       try{capture.Kill();}catch{}Log("날짜 변경: 녹화 폴더 교체");StartCapture();
      }
     }
    }catch(Exception e){Error("녹화 감시: "+Limit(e.Message,130));}
  }
  if(!loginBusy && (DateTime.Now-lastCloudProbe).TotalSeconds>300)CheckGoogleConnection();
  if(!recording && autoRecordPending && !string.IsNullOrEmpty(cameraName) && !OldVersionRunning())StartRecording();
  UpdatePendingCount();
  if(loggedIn && !uploadBusy && (DateTime.Now-lastUploadAttempt).TotalSeconds>=0.5)UploadPass();
  RemoteTick();MotionRuntimeTick();PruneMotionPrebuffer();RefreshLabels();
 }
 static string HexHash(byte[] content,HashAlgorithm hash){
  using(hash){return BitConverter.ToString(hash.ComputeHash(content)).Replace("-","").ToLowerInvariant();}
 }
 void OnFormClosing(object sender,FormClosingEventArgs e){
  if(recording){
   var answer=MessageBox.Show(this,"녹화 중입니다. 종료하면 녹화가 중단됩니다.\n기존 저장 파일은 보존됩니다.\n프로그램을 종료할까요?","녹화 종료 확인",MessageBoxButtons.YesNo,MessageBoxIcon.Warning);
   if(answer!=DialogResult.Yes){e.Cancel=true;return;}
  }
  StopMotionRuntime();exiting=true;recording=false;cancel.Cancel();
  try{if(capture!=null && !capture.HasExited)capture.Kill();}catch{}
 }
}


