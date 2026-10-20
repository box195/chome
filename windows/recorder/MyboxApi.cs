using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;
using System.Security.Cryptography;

internal static class MyboxApi {
 const string Base = "https://open-api.mybox.naver.com/v1/drive/";
 static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 20*1024*1024 };
 static HttpWebRequest Request(string url, string method, string pat, int timeoutMs = 40000) {
  ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
  var req=(HttpWebRequest)WebRequest.Create(url);
  req.Method=method;req.Timeout=timeoutMs;req.ReadWriteTimeout=timeoutMs;
  req.KeepAlive=false;req.UserAgent="USBHomeCam-NAVER/1.0";
  if(!string.IsNullOrEmpty(pat))req.Headers[HttpRequestHeader.Authorization]="Bearer "+pat;
  return req;
 }
 static string ReadResponse(HttpWebResponse response) {
  using(response)using(var stream=response.GetResponseStream())using(var reader=new StreamReader(stream,Encoding.UTF8))return reader.ReadToEnd();
 }
 static string Fail(WebException e){
  var response=e.Response as HttpWebResponse;
  return response==null ? "네트워크 연결 실패 / 시간 초과" : ("네이버 MYBOX HTTP "+(int)response.StatusCode+" ("+response.StatusCode+")");
 }
 static Dictionary<string,object> Object(string s){
  var o=Json.DeserializeObject(s) as Dictionary<string,object>;
  if(o==null)throw new Exception("네이버 MYBOX 응답 형식 오류");return o;
 }
 static string Prop(Dictionary<string,object> value,string key){
  if(!value.ContainsKey(key)||value[key]==null)return "";
  return Convert.ToString(value[key]);
 }
 static Dictionary<string,object> Call(string token,string method,string endpoint,object content=null){
  try{
   var req=Request(Base+endpoint,method,token);
   if(content!=null){
     byte[] body=Encoding.UTF8.GetBytes(Json.Serialize(content));
     req.ContentType="application/json; charset=utf-8";
     req.ContentLength=body.Length;
     using(var stream=req.GetRequestStream())stream.Write(body,0,body.Length);
   }
   using(var response=(HttpWebResponse)req.GetResponse()){
    string json;
    using(var stream=response.GetResponseStream())using(var reader=new StreamReader(stream,Encoding.UTF8))json=reader.ReadToEnd();
    return Object(json);
   }
  }catch(WebException e){throw new IOException(Fail(e));}
 }
 public static string StorageState(string pat){
  var d=Call(pat,"GET","storage");
  string used=Prop(d,"usedBytes"),quota=Prop(d,"quotaBytes");
  long u=0,q=0;long.TryParse(used,out u);long.TryParse(quota,out q);
  if(q<=0)return "네이버 계정 연결됨";
  return "MYBOX 연결됨 · 남은 공간 "+((q-u)/1073741824.0).ToString("0.0")+"GB";
 }
 public static string EnsureFolder(string pat,string folder){
  var root=Call(pat,"GET","resources?count=1000");
  var list=root.ContainsKey("resources")?root["resources"] as System.Collections.IEnumerable:null;
  if(list!=null)foreach(var raw in list){
   var item=raw as Dictionary<string,object>;
   if(item!=null && Prop(item,"name")==folder && Prop(item,"type")=="folder"){
    var id=Prop(item,"resourceId");
    if(!string.IsNullOrWhiteSpace(id))return id;
   }
  }
  try{
   var created=Call(pat,"POST","folders",new {folderName=folder});
   string id=Prop(created,"resourceId");
   if(string.IsNullOrWhiteSpace(id))throw new IOException("MYBOX 폴더 ID가 없습니다.");
   return id;
  }catch(IOException e){
   if(!e.Message.Contains("409"))throw;
   root=Call(pat,"GET","resources?count=1000");
   list=root.ContainsKey("resources")?root["resources"] as System.Collections.IEnumerable:null;
   if(list!=null)foreach(var raw in list){
    var item=raw as Dictionary<string,object>;
    if(item!=null && Prop(item,"name")==folder && Prop(item,"type")=="folder")return Prop(item,"resourceId");
   }
   throw;
  }
 }
 public static void Upload(string pat,string folderId,string path){
  var info=new FileInfo(path);
  if(!info.Exists || info.Length<=0)throw new IOException("업로드할 영상이 없습니다.");
  long size=info.Length;
  var prepare=Call(pat,"POST","files",new {
   fileName=info.Name, fileSize=size, parentId=folderId,
   isOverwrite=true, resume=false
  });
  var uploadUrl=Prop(prepare,"uploadUrl");
  Uri url;
  if(!Uri.TryCreate(uploadUrl,UriKind.Absolute,out url)||url.Scheme!="https")throw new IOException("MYBOX 보안 업로드 URL이 올바르지 않습니다.");
  string boundary="----USBHomeCam"+Guid.NewGuid().ToString("N");
  string header="--"+boundary+"\r\nContent-Disposition: form-data; name=\"Filedata\"; filename=\""+info.Name+"\"\r\nContent-Type: video/mp2t\r\n\r\n";
  byte[] prefix=Encoding.UTF8.GetBytes(header);
  byte[] suffix=Encoding.ASCII.GetBytes("\r\n--"+boundary+"--\r\n");
  try{
   var req=Request(uploadUrl,"POST",null,180000);
   req.AllowWriteStreamBuffering=false;
   req.ContentType="multipart/form-data; boundary="+boundary;
   req.ContentLength=prefix.Length+size+suffix.Length;
   using(var dst=req.GetRequestStream()){
    dst.Write(prefix,0,prefix.Length);
    using(var src=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)){
     var buf=new byte[128*1024];
     int n;while((n=src.Read(buf,0,buf.Length))>0)dst.Write(buf,0,n);
    }
    dst.Write(suffix,0,suffix.Length);
   }
   using(var res=(HttpWebResponse)req.GetResponse()){
    if((int)res.StatusCode<200||(int)res.StatusCode>=300)throw new IOException("네이버 저장 서버가 업로드 완료를 확인하지 않았습니다.");
   }
  }catch(WebException e){throw new IOException(Fail(e));}
  var check=new FileInfo(path);
  if(!check.Exists || check.Length!=size)throw new IOException("전송 중 원본 영상이 변경되었습니다. 다음 회차에 재검증합니다.");
 }
 public static void SaveToken(string path,string token){
  Directory.CreateDirectory(Path.GetDirectoryName(path));
  byte[] bytes=ProtectedData.Protect(Encoding.UTF8.GetBytes(token),null,DataProtectionScope.CurrentUser);
  string tmp=path+".pending";
  File.WriteAllBytes(tmp,bytes);
  if(File.Exists(path))File.Delete(path);
  File.Move(tmp,path);
 }
 public static string LoadToken(string path){
  if(!File.Exists(path))return "";
  try{return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path),null,DataProtectionScope.CurrentUser));}
  catch{return "";}
 }
}

