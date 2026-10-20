using System;
using System.IO;
using System.Text;
using System.Security.Cryptography;
using System.Web.Script.Serialization;
class ReadPushSecrets {
 static int Main() {
  try {
   string dir=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"USBHomeCamNAVER","remote");
   Func<string,string,string> read=(file,entropy)=>Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(Path.Combine(dir,file)),Encoding.UTF8.GetBytes(entropy),DataProtectionScope.CurrentUser));
   Console.Write(new JavaScriptSerializer().Serialize(new { refreshToken=read("cloud-credential.dat","CHome-Cloud-v1-Windows-user"), vapid=read("push-vapid.dat","CHome-WebPush-v1"), uid=File.ReadAllText(Path.Combine(dir,"owner-uid.txt")).Trim() }));
   return 0;
  } catch { return 1; }
 }
}
