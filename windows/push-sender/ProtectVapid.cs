using System;
using System.IO;
using System.Text;
using System.Security.Cryptography;
class ProtectVapid {
 static int Main() {
  try {
   string folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"USBHomeCamNAVER","remote");
   Directory.CreateDirectory(folder);
   string path=Path.Combine(folder,"push-vapid.dat");
   if(File.Exists(path))return 2;
   string value=Console.In.ReadToEnd();
   if(value.Length<100||value.Length>4096)return 3;
   File.WriteAllBytes(path,ProtectedData.Protect(Encoding.UTF8.GetBytes(value),Encoding.UTF8.GetBytes("CHome-WebPush-v1"),DataProtectionScope.CurrentUser));return 0;
  } catch { return 1; }
 }
}
