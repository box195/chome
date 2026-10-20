using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Diagnostics;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Security.Cryptography;

internal sealed partial class CamForm {
 const int networkBundleSize=2; // Two locally complete 10s chunks = 20s MYBOX upload.
 double lastTransferSeconds=0,lastTransferMbps=0,oldestQueueMinutes=0;
 DateTime lastQueueScan=DateTime.MinValue;
 readonly object uploadIdentityLock=new object();
 string Identity(FileInfo file) {return file.FullName+"|"+file.Length+"|"+file.LastWriteTimeUtc.Ticks;}
 string Marker(FileInfo file) {
  string identity=Identity(file);
  using(var sha=SHA256.Create()){
   string key=BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(identity))).Replace("-","").ToLowerInvariant();
   return Path.Combine(data,"uploaded",key+".ok");
  }
 }
 void LegacyV2UpdatePendingCount() {
  if((DateTime.Now-lastQueueScan).TotalSeconds<30)return;
  lastQueueScan=DateTime.Now;
  Task.Run(()=>{
   try{
    string root=Path.Combine(recordRoot,"raw");
    if(!Directory.Exists(root)){pendingUploads=0;return;}
    int pending=0;
    DateTime earliest=DateTime.MaxValue;
    foreach(var f in Directory.GetFiles(root,"*.ts",SearchOption.AllDirectories)) {
      var item=new FileInfo(f);
      if(!File.Exists(Marker(item))) {
       pending++;
       if(item.LastWriteTime<earliest)earliest=item.LastWriteTime;
      }
    }
    pendingUploads=pending;
    oldestQueueMinutes=earliest==DateTime.MaxValue?0:Math.Max(0,(DateTime.Now-earliest).TotalMinutes);
   }catch(Exception ex){Log("대기열 통계 오류: "+Limit(ex.Message,120));}
  });
 }
 void LegacyV2UploadPass() {
  if(uploadBusy||!loggedIn||string.IsNullOrEmpty(myboxFolderId)||DateTime.Now<nextUploadAllowed)return;
  uploadBusy=true;lastUploadAttempt=DateTime.Now;RefreshLabels();
  uploader=Task.Run(()=>{
   try {
    string root=Path.Combine(recordRoot,"raw");
    if(!Directory.Exists(root))return;
    var files=Directory.GetFiles(root,"*.ts",SearchOption.AllDirectories)
      .Select(s=>new FileInfo(s))
      .OrderBy(f=>f.LastWriteTimeUtc).ToList();
    var newestByDay=files.GroupBy(f=>f.DirectoryName).ToDictionary(g=>g.Key,g=>g.OrderByDescending(f=>f.LastWriteTimeUtc).First().FullName);
    var token=MyboxApi.LoadToken(config);
    if(string.IsNullOrEmpty(token))throw new IOException("MYBOX 개인 액세스 토큰이 없습니다.");
    var ready=new List<FileInfo>();
    foreach(var f in files){
     if(File.Exists(Marker(f)))continue;
     double age=(DateTime.UtcNow-f.LastWriteTimeUtc).TotalSeconds;
     if(age<7)continue;
     if(newestByDay[f.DirectoryName]==f.FullName && age<180)continue;
     ready.Add(f);
    }
    int groups=0;
    while(ready.Count>0 && groups<4 && !exiting && !cancel.IsCancellationRequested){
     var first=ready[0];
     var batch=ready.Where(f=>f.DirectoryName==first.DirectoryName).Take(networkBundleSize).ToList();
     if(batch.Count<networkBundleSize && (DateTime.UtcNow-first.LastWriteTimeUtc).TotalSeconds<180)break;
     string stem=Path.GetFileNameWithoutExtension(first.Name);
     string uploadName=stem+(batch.Count==2?"_bundle20s.ts":"_tail10s.ts");
     string stage=Path.Combine(data,"staging",uploadName);
     Stopwatch sw=Stopwatch.StartNew();
     try{
      CloudBundler.Pack(ffmpeg,batch,stage);
      MyboxApi.Upload(token,myboxFolderId,stage);
      sw.Stop();
      foreach(var f in batch){
       string marker=Marker(f);
       Directory.CreateDirectory(Path.GetDirectoryName(marker));
       File.WriteAllText(marker,Identity(f),Encoding.UTF8);
      }
      completedUploads++;groups++;
      lastUpload=uploadName;
      lastTransferSeconds=sw.Elapsed.TotalSeconds;
      lastTransferMbps= (new FileInfo(stage).Length*8.0/1000000.0)/Math.Max(0.001,lastTransferSeconds);
      Log("MYBOX 업로드 성공: "+uploadName+" | "+lastTransferSeconds.ToString("0.0")+"초 | 실효 "+lastTransferMbps.ToString("0.0")+"Mbps");
      foreach(var b in batch)ready.Remove(b);
      pendingUploads=Math.Max(0,pendingUploads-batch.Count);
     }catch(Exception ex){
      failedUploads++;
      string error=Limit(ex.Message,160);
      Log("MYBOX 업로드 실패, 로컬 원본 보존: "+uploadName+" / "+error);
      if(error.Contains("429"))nextUploadAllowed=DateTime.Now.AddMinutes(4);
      else if(error.Contains("507"))nextUploadAllowed=DateTime.Now.AddMinutes(20);
      else if(error.Contains("403"))nextUploadAllowed=DateTime.Now.AddMinutes(5);
      else if(error.Contains("401")){loggedIn=false;nextUploadAllowed=DateTime.Now.AddMinutes(5);}
      else nextUploadAllowed=DateTime.Now.AddSeconds(45);
      break;
     } finally {try{if(File.Exists(stage))File.Delete(stage);}catch{}}
    }
   }catch(Exception ex) {
     Log("MYBOX 업로드 처리 오류: "+Limit(ex.Message,140));
     nextUploadAllowed=DateTime.Now.AddSeconds(45);
   } finally {uploadBusy=false;RefreshLabels();}
  });
 }
}

