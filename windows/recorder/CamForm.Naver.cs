using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Drawing;
using System.Windows.Forms;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Collections.Generic;

internal sealed partial class CamForm {
 string cloudDescription="네이버 토큰 미등록";
 string myboxFolderId="";
 DateTime nextUploadAllowed=DateTime.MinValue, lastPendingUpdate=DateTime.MinValue;
 int pendingUploads=0;
 bool OldVersionRunning(){
  try{return Process.GetProcessesByName("USBHomeCam").Any(p=>!p.HasExited);}catch{return false;}
 }
 void CheckGoogleConnection(){
  if(cloudCheckBusy||loginBusy||exiting)return;
  cloudCheckBusy=true;lastCloudProbe=DateTime.Now;
  Task.Run(()=>{
   try{
    var token=MyboxApi.LoadToken(config);
    if(string.IsNullOrEmpty(token)){
     BeginInvoke((Action)delegate {loggedIn=false;cloudDescription="토큰 등록 필요";RefreshLabels();});
     return;
    }
    string status=MyboxApi.StorageState(token);
    string folder=myboxFolderId;
    if(string.IsNullOrEmpty(folder))folder=MyboxApi.EnsureFolder(token,"USBHomeCam_Backup");
    BeginInvoke((Action)delegate {
     myboxFolderId=folder;loggedIn=true;cloudDescription=status;
     Log("네이버 MYBOX 인증 및 업로드 폴더 확인됨.");
     RefreshLabels();
    });
   }catch(Exception ex){
    try{BeginInvoke((Action)delegate{
     loggedIn=false;cloudDescription="연결 실패 / 재시도";
     Log("네이버 MYBOX 연결 문제: "+Limit(ex.Message,130));RefreshLabels();
    });}catch{}
   }finally{cloudCheckBusy=false;}
  });
 }
 void ConnectGoogle(){
  if(loginBusy)return;
  using(var d=new Form {
   Text="네이버 MYBOX 개인 액세스 토큰 등록",
   Size=new Size(620,265),MinimumSize=new Size(620,265),
   StartPosition=FormStartPosition.CenterParent,
   FormBorderStyle=FormBorderStyle.FixedDialog,
   Font=new Font("Malgun Gothic",9),MinimizeBox=false,MaximizeBox=false
  }){
   var message=new Label {Left=18,Top=13,Width=580,Height=72,
    Text="MYBOX 웹에서 로그인 → 설정 → 계정 및 개인 액세스 토큰 관리 → 토큰 생성.\r\n토큰의 최대 유효기간은 180일입니다. 만료 시 다시 등록해야 합니다.\r\n네이버 계정 비밀번호는 입력하지 마세요."};
   var entry=new TextBox {Left=18,Top=93,Width=567,UseSystemPasswordChar=true};
   var help=new Button {Text="발급 안내 열기",Left=18,Top=142,Width=163,Height=38};
   help.Click+=delegate {Process.Start("https://developers.mybox.naver.com/getting-started");};
   var ok=new Button {Text="토큰 검증 및 저장",Left=401,Top=142,Width=184,Height=38,DialogResult=DialogResult.OK};
   d.Controls.Add(message);d.Controls.Add(entry);d.Controls.Add(help);d.Controls.Add(ok);
   d.AcceptButton=ok;
   if(d.ShowDialog(this)!=DialogResult.OK)return;
   var pat=entry.Text.Trim();entry.Clear();
   if(pat.Length<20){Error("MYBOX 개인 액세스 토큰이 필요합니다.");return;}
   loginBusy=true;Log("MYBOX 토큰 확인 중. 토큰은 로그에 남기지 않습니다.");RefreshLabels();
   authTask=Task.Run(()=>{
    try{
     string status=MyboxApi.StorageState(pat);
     string folder=MyboxApi.EnsureFolder(pat,"USBHomeCam_Backup");
     MyboxApi.SaveToken(config,pat);
     BeginInvoke((Action)delegate{
      myboxFolderId=folder;loggedIn=true;loginBusy=false;cloudDescription=status;
      Log("MYBOX 토큰 저장 및 연결 성공 (Windows 사용자 암호화 저장).");RefreshLabels();
     });
    }catch(Exception ex){try{BeginInvoke((Action)delegate{
     loginBusy=false;Error("MYBOX 토큰 검증 실패: "+Limit(ex.Message,145));
    });}catch{}}
   });
  }
 }
 void LegacyUpdatePendingCount(){
  if((DateTime.Now-lastPendingUpdate).TotalSeconds<25)return;
  lastPendingUpdate=DateTime.Now;
  Task.Run(()=>{
   try{
    var raw=Path.Combine(recordRoot,"raw");
    var count=Directory.Exists(raw)?Directory.GetFiles(raw,"*.ts",SearchOption.AllDirectories).Length:0;
    var markers=Path.Combine(data,"uploaded");
    var uploaded=Directory.Exists(markers)?Directory.GetFiles(markers,"*.ok",SearchOption.TopDirectoryOnly).Length:0;
    pendingUploads=Math.Max(0,count-uploaded);
   }catch{}
  });
 }
 void LegacyUploadPass(){
  if(uploadBusy||!loggedIn||string.IsNullOrEmpty(myboxFolderId)||DateTime.Now<nextUploadAllowed)return;
  uploadBusy=true;lastUploadAttempt=DateTime.Now;RefreshLabels();
  uploader=Task.Run(()=>{
   try{
    string root=Path.Combine(recordRoot,"raw");
    if(!Directory.Exists(root))return;
    var files=Directory.GetFiles(root,"*.ts",SearchOption.AllDirectories)
     .Select(p=>new FileInfo(p)).OrderBy(f=>f.LastWriteTimeUtc).ToList();
    var newestByDir=files.GroupBy(f=>f.DirectoryName).ToDictionary(
       g=>g.Key,g=>g.OrderByDescending(f=>f.LastWriteTimeUtc).First().FullName);
    var token=MyboxApi.LoadToken(config);
    if(string.IsNullOrEmpty(token))throw new IOException("MYBOX 토큰이 유효하지 않습니다.");
    int done=0;
    foreach(var file in files){
     if(exiting || cancel.IsCancellationRequested || done>=6)break;
     if((DateTime.UtcNow-file.LastWriteTimeUtc).TotalSeconds<stableSeconds)continue;
     // Never upload the active newest file. After interruption, retry it only once old.
     if(newestByDir[file.DirectoryName]==file.FullName && (DateTime.UtcNow-file.LastWriteTimeUtc).TotalSeconds<180)continue;
     string identity=file.FullName+"|"+file.Length+"|"+file.LastWriteTimeUtc.Ticks;
     string marker=Path.Combine(data,"uploaded",HexHash(Encoding.UTF8.GetBytes(identity),System.Security.Cryptography.SHA256.Create())+".ok");
     if(File.Exists(marker))continue;
     try{
      MyboxApi.Upload(token,myboxFolderId,file.FullName);
      Directory.CreateDirectory(Path.GetDirectoryName(marker));
      File.WriteAllText(marker,identity,Encoding.UTF8);
      completedUploads++;lastUpload=file.Name;done++;
      Log("MYBOX 업로드 HTTP 성공: "+file.Name);
     }catch(Exception ex){
      failedUploads++;
      string message=Limit(ex.Message,140);
      Log("MYBOX 업로드 실패 (로컬 원본 보존): "+file.Name+" / "+message);
      if(message.Contains("429"))nextUploadAllowed=DateTime.Now.AddMinutes(4);
      else if(message.Contains("507"))nextUploadAllowed=DateTime.Now.AddMinutes(20);
      else if(message.Contains("403"))nextUploadAllowed=DateTime.Now.AddMinutes(5);
      else if(message.Contains("401")){loggedIn=false;nextUploadAllowed=DateTime.Now.AddMinutes(5);}
      else nextUploadAllowed=DateTime.Now.AddSeconds(45);
      break;
     }
    }
   }catch(Exception ex){Log("MYBOX 전송 감시 문제: "+Limit(ex.Message,140));nextUploadAllowed=DateTime.Now.AddSeconds(45);}
   finally{uploadBusy=false;RefreshLabels();}
  });
 }
}

