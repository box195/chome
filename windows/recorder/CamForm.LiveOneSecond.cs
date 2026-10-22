using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Tasks;

internal sealed partial class CamForm {
 FileSystemWatcher segmentWatcher;
 string activeSegmentPath="";
 DateTime lastVideoEvent=DateTime.MinValue;
 readonly object segmentSync=new object();
 readonly ConcurrentQueue<string> freshQueue=new ConcurrentQueue<string>();
 readonly ConcurrentQueue<string> oldQueue=new ConcurrentQueue<string>();
 readonly ConcurrentDictionary<string,byte> alreadyQueued=new ConcurrentDictionary<string,byte>(StringComparer.OrdinalIgnoreCase);
 DateTime lastArchiveScan=DateTime.MinValue;
 int successfullyUploadedFiles=0;
 int uploadDelaySamples=0;
 double lastArrivalDelaySeconds=0;
 double maxUploadDelaySeconds=0;
 double cumulativeUploadDelaySeconds=0;
 string lastRemoteOk="";

 void StartSegmentWatcher(){
  if(segmentWatcher!=null)return;
  var folder=recordRoot;
  Directory.CreateDirectory(folder);
  segmentWatcher=new FileSystemWatcher(folder,"*.ts");
  segmentWatcher.IncludeSubdirectories=true;
  segmentWatcher.NotifyFilter=NotifyFilters.FileName|NotifyFilters.LastWrite|NotifyFilters.Size;
  segmentWatcher.Created+=(sender,e)=>OnNewSegment(e.FullPath);
  segmentWatcher.Changed+=(sender,e)=>OnChangedSegment(e.FullPath);
  segmentWatcher.Error+=(sender,e)=>Log("영상 파일 감시 재점검 필요: "+Limit(e.GetException().Message,100));
  segmentWatcher.EnableRaisingEvents=true;
  Log("1초 녹화 파일 감시 시작 (완료된 파일만 업로드).");
  ScanArchivePending();
 }
 void OnNewSegment(string path){
  if(!path.EndsWith(".ts",StringComparison.OrdinalIgnoreCase))return;
  if(!path.Contains(Path.DirectorySeparatorChar+"raw"+Path.DirectorySeparatorChar) &&
     !path.Contains(Path.DirectorySeparatorChar+"raw-motion"+Path.DirectorySeparatorChar))return;
  string closed=null;
  lock(segmentSync){
   if(!string.Equals(activeSegmentPath,path,StringComparison.OrdinalIgnoreCase)){
    closed=activeSegmentPath;
    activeSegmentPath=path;
   }
   lastVideoEvent=DateTime.Now;
   lastCaptureFile=Path.GetFileName(path);
  }
  if(!string.IsNullOrEmpty(closed)){
   if(!closed.Contains(Path.DirectorySeparatorChar+"raw-motion"+Path.DirectorySeparatorChar))QueueFresh(closed);
   QueueMotionProbe(closed);
  }
 }
 void OnChangedSegment(string path){
  if(!path.EndsWith(".ts",StringComparison.OrdinalIgnoreCase))return;
  if(!path.Contains(Path.DirectorySeparatorChar+"raw"+Path.DirectorySeparatorChar) &&
     !path.Contains(Path.DirectorySeparatorChar+"raw-motion"+Path.DirectorySeparatorChar))return;
  lock(segmentSync){
   if(string.Equals(activeSegmentPath,path,StringComparison.OrdinalIgnoreCase))
    lastVideoEvent=DateTime.Now;
  }
 }
 void QueueFresh(string path){
  if(alreadyQueued.TryAdd(path,0))freshQueue.Enqueue(path);
 }
 void QueueArchive(string path){
  if(alreadyQueued.TryAdd(path,0))oldQueue.Enqueue(path);
 }
 void ScanArchivePending(){
  if((DateTime.Now-lastArchiveScan).TotalSeconds<35)return;
  lastArchiveScan=DateTime.Now;
  Task.Run(()=>{
   try{
    string root=Path.Combine(recordRoot,"raw");
    if(!Directory.Exists(root))return;
    var files=Directory.GetFiles(root,"*.ts",SearchOption.AllDirectories);
    int pending=0;DateTime oldest=DateTime.MaxValue;
    var list=new List<FileInfo>();
    string active;lock(segmentSync){active=activeSegmentPath;}
    foreach(var f in files){
      var info=new FileInfo(f);
      if(info.Length==0)continue;
      if(string.Equals(info.FullName,active,StringComparison.OrdinalIgnoreCase) && recording)continue;
      if(File.Exists(Marker(info)))continue;
      pending++;
      if(info.LastWriteTime<oldest)oldest=info.LastWriteTime;
      list.Add(info);
    }
    string eventRoot=Path.Combine(recordRoot,"events");
    if(Directory.Exists(eventRoot))foreach(var name in Directory.GetFiles(eventRoot,"event-video.ts",SearchOption.AllDirectories)){
      var item=new FileInfo(name);
      if(item.Length>1000&&File.Exists(Path.Combine(item.DirectoryName,"upload-event.marker"))&&!File.Exists(Marker(item))){list.Add(item);pending++;}
    }
    // New capture receives higher priority; older recordings are drained when bandwidth is free.
    foreach(var item in list.OrderBy(x=>x.LastWriteTimeUtc).Take(12000))QueueArchive(item.FullName);
    pendingUploads=pending;
    oldestQueueMinutes=oldest==DateTime.MaxValue?0:Math.Max(0,(DateTime.Now-oldest).TotalMinutes);
    Log("업로드 대기열 조사: "+pending+"개 (로컬 파일 유지), 최근 영상 우선.");
   }catch(Exception ex){Log("대기열 조사 실패: "+Limit(ex.Message,100));}
  });
 }
 void UpdatePendingCount(){
  ScanArchivePending();
 }
 bool GetNextFile(out string path,bool freshOnly=false){
  path=null;
  if(freshQueue.TryDequeue(out path))return true;
  if(!freshOnly && oldQueue.TryDequeue(out path))return true;
  return false;
 }
 bool TryUploadClip(string path,string token){
  FileInfo f;
  try{f=new FileInfo(path);if(!f.Exists || f.Length<1000)return false;}catch{return false;}
  string active;lock(segmentSync){active=activeSegmentPath;}
  if(string.Equals(active,path,StringComparison.OrdinalIgnoreCase) && recording)return false;
  if(path.Contains(Path.DirectorySeparatorChar+"raw-motion"+Path.DirectorySeparatorChar))return false;
  // Avoid a clip being uploaded while still written after unexpected file events.
  if((DateTime.UtcNow-f.LastWriteTimeUtc).TotalMilliseconds<180) {System.Threading.Thread.Sleep(230); f.Refresh(); if(!f.Exists || f.Length<1000)return false;}
  string marker=Marker(f);
  if(File.Exists(marker))return true;
  var started=Stopwatch.StartNew();
  MyboxApi.Upload(token,myboxFolderId,f.FullName);
  started.Stop();
  // Marker only after upload server success. Failed uploads remain on disk for retry.
  Directory.CreateDirectory(Path.GetDirectoryName(marker));
  File.WriteAllText(marker,Identity(f),Encoding.UTF8);
  completedUploads++;
  successfullyUploadedFiles++;
  lastUpload=f.Name;lastRemoteOk=f.Name;
  lastTransferSeconds=started.Elapsed.TotalSeconds;
  lastTransferMbps=f.Length*8.0/1000000.0/Math.Max(0.001,lastTransferSeconds);
  lastArrivalDelaySeconds=Math.Max(0,(DateTime.UtcNow-f.LastWriteTimeUtc).TotalSeconds);
  maxUploadDelaySeconds=Math.Max(maxUploadDelaySeconds,lastArrivalDelaySeconds);
  cumulativeUploadDelaySeconds+=lastArrivalDelaySeconds;
  uploadDelaySamples++;
  if(completedUploads<=12 || completedUploads%30==0)
   Log("MYBOX 1초 전송 OK: "+f.Name+" | "+lastTransferSeconds.ToString("0.00")+"초 | 지연 "+lastArrivalDelaySeconds.ToString("0.0")+"초");
  return true;
 }
 void UploadPass(){
  if(uploadBusy || !loggedIn || string.IsNullOrEmpty(myboxFolderId) || DateTime.Now<nextUploadAllowed)return;
  uploadBusy=true;lastUploadAttempt=DateTime.Now;
  uploader=Task.Run(()=>{
   try{
    string token=MyboxApi.LoadToken(config);
    if(string.IsNullOrEmpty(token))throw new IOException("MYBOX 토큰이 없습니다.");
    int attempts=0;string path;
    while(attempts++<12 && !exiting && !cancel.IsCancellationRequested && GetNextFile(out path)){
     byte ignored;
     try{
      if(!TryUploadClip(path,token)){
       alreadyQueued.TryRemove(path,out ignored);
       continue;
      }
      alreadyQueued.TryRemove(path,out ignored);
      if(!freshQueue.IsEmpty && attempts%3==0)continue;
     }catch(Exception ex){
      failedUploads++;
      var message=Limit(ex.Message,155);
      Log("MYBOX 업로드 실패, 원본 보존: "+Path.GetFileName(path)+" / "+message);
      alreadyQueued.TryRemove(path,out ignored);
      if(message.Contains("429"))nextUploadAllowed=DateTime.Now.AddMinutes(4);
      else if(message.Contains("507"))nextUploadAllowed=DateTime.Now.AddMinutes(20);
      else if(message.Contains("403"))nextUploadAllowed=DateTime.Now.AddMinutes(5);
      else if(message.Contains("401")){loggedIn=false;nextUploadAllowed=DateTime.Now.AddMinutes(5);}
      else nextUploadAllowed=DateTime.Now.AddSeconds(20);
      break;
     }
    }
   }catch(Exception ex){
    Log("업로드 처리 오류: "+Limit(ex.Message,135));
    nextUploadAllowed=DateTime.Now.AddSeconds(20);
   }finally{uploadBusy=false;RefreshLabels();}
  });
 }
 void StopSegmentWatcher(){
  if(segmentWatcher==null)return;
  try{segmentWatcher.EnableRaisingEvents=false;segmentWatcher.Dispose();}catch{}
  segmentWatcher=null;
 }
}

