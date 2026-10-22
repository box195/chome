package com.chome.recorder;

import android.content.Context;
import android.content.SharedPreferences;
import android.util.Base64;
import androidx.annotation.NonNull;
import com.google.firebase.database.*;
import org.json.JSONObject;
import java.net.*;
import java.io.*;
import java.nio.ByteBuffer;
import java.nio.charset.StandardCharsets;
import java.security.*;
import java.security.interfaces.ECPublicKey;
import java.security.spec.*;
import java.util.*;
import java.util.concurrent.*;
import javax.crypto.*;
import javax.crypto.spec.*;

/**
 * Sends the existing CHome Web Push subscription directly from the paired Y700.
 * Never embeds or logs VAPID credentials. Reads them only from paired-device RTDB.
 * A rooted or compromised tablet can extract active credentials, so revoke pairing
 * and rotate the VAPID key if a tablet is lost.
 */
final class TabletPushSender {
    private static final long COOLDOWN_MS=120000L;
    private final Context context;
    private final String root;
    private final ExecutorService io=Executors.newSingleThreadExecutor();
    private volatile boolean closed=false, pending=false;
    TabletPushSender(Context context,String ownerUid) {
        this.context=context.getApplicationContext();
        this.root="clients/"+ownerUid+"/";
    }
    void motion(long eventTime) {
        if(closed||pending)return;
        SharedPreferences p=Settings.publicPrefs(context);
        if(eventTime-p.getLong("tabletPushLastAttempt",0L)<COOLDOWN_MS)return;
        pending=true;
        p.edit().putLong("tabletPushLastAttempt",eventTime).apply();
        FirebaseDatabase db=FirebaseDatabase.getInstance();
        db.getReference(root+"devices/y700/pushVapid").get().addOnCompleteListener(config -> {
            if(closed){pending=false;return;}
            if(!config.isSuccessful()||config.getResult()==null||!config.getResult().exists()){
                result("push credentials unavailable");pending=false;return;
            }
            DataSnapshot creds=config.getResult();
            String subject=creds.child("subject").getValue(String.class);
            String publicKey=creds.child("publicKey").getValue(String.class);
            String privateKey=creds.child("privateKey").getValue(String.class);
            if(!validVapid(subject,publicKey,privateKey)){
                result("push configuration invalid");pending=false;return;
            }
            db.getReference(root+"push/subscriptions").get().addOnCompleteListener(subs -> {
                if(closed){pending=false;return;}
                if(!subs.isSuccessful()||subs.getResult()==null){
                    result("push subscriptions unavailable");pending=false;return;
                }
                List<Subscription> targets=new ArrayList<>();
                for(DataSnapshot item:subs.getResult().getChildren()) {
                    if(targets.size()>=20)break;
                    String url=item.child("endpoint").getValue(String.class);
                    String key=item.child("keys/p256dh").getValue(String.class);
                    String auth=item.child("keys/auth").getValue(String.class);
                    if(validEndpoint(url)&&key!=null&&auth!=null)targets.add(new Subscription(url,key,auth));
                }
                if(targets.isEmpty()){result("push subscriptions empty");pending=false;return;}
                io.execute(()-> {
                    int success=0;
                    for(Subscription s:targets){
                        try{
                            if(send(s,subject,publicKey,privateKey,eventTime))success++;
                        }catch(Exception ignored){ /* Never leak endpoints or signing keys. */ }
                    }
                    result(success>0?"push sent "+success+"/"+targets.size():"push delivery failed");
                    pending=false;
                });
            });
        });
    }
    private void result(String status) {
        Settings.publicPrefs(context).edit().putString("tabletPushStatus",status).apply();
    }
    void close(){closed=true;io.shutdown();}
    private static final class Subscription {
        final String url,key,auth;
        Subscription(String u,String k,String a){url=u;key=k;auth=a;}
    }
    private static boolean validVapid(String sub,String pub,String priv){
        try{return sub!=null&&sub.startsWith("https://")&&sub.length()<200
            &&decode(pub).length==65&&decode(priv).length==32;}catch(Exception e){return false;}
    }
    private static boolean validEndpoint(String endpoint){
        try{URL u=new URL(endpoint);return "https".equals(u.getProtocol())
            &&"fcm.googleapis.com".equals(u.getHost());}catch(Exception e){return false;}
    }
    private static byte[] decode(String s){return Base64.decode(s,Base64.URL_SAFE|Base64.NO_WRAP);}
    private static String encode(byte[] b){return Base64.encodeToString(b,Base64.URL_SAFE|Base64.NO_PADDING|Base64.NO_WRAP);}
    private static byte[] concat(byte[]... arrays){
        int n=0;for(byte[] a:arrays)n+=a.length;
        byte[] out=new byte[n];int at=0;
        for(byte[] a:arrays){System.arraycopy(a,0,out,at,a.length);at+=a.length;}
        return out;
    }
    private static byte[] hkdf(byte[] salt,byte[] ikm,byte[] info,int length)throws Exception{
        Mac m=Mac.getInstance("HmacSHA256");
        m.init(new SecretKeySpec(salt,"HmacSHA256"));byte[] prk=m.doFinal(ikm);
        m.init(new SecretKeySpec(prk,"HmacSHA256"));
        return Arrays.copyOf(m.doFinal(concat(info,new byte[]{1})),length);
    }
    private static ECParameterSpec curve()throws Exception {
        AlgorithmParameters params=AlgorithmParameters.getInstance("EC");
        params.init(new ECGenParameterSpec("secp256r1"));
        return params.getParameterSpec(ECParameterSpec.class);
    }
    private static PublicKey readPublic(byte[] p)throws Exception {
        if(p.length!=65||p[0]!=4)throw new GeneralSecurityException("invalid EC point");
        java.security.spec.ECPoint xy=new java.security.spec.ECPoint(
            new java.math.BigInteger(1,Arrays.copyOfRange(p,1,33)),
            new java.math.BigInteger(1,Arrays.copyOfRange(p,33,65)));
        return KeyFactory.getInstance("EC").generatePublic(new ECPublicKeySpec(xy,curve()));
    }
    private static byte[] publicBytes(PublicKey key){
        java.security.spec.ECPoint point=((ECPublicKey)key).getW();
        return concat(new byte[]{4},pad(point.getAffineX().toByteArray()),pad(point.getAffineY().toByteArray()));
    }
    private static byte[] pad(byte[] value){
        byte[] out=new byte[32];System.arraycopy(value,Math.max(0,value.length-32),
            out,Math.max(0,32-value.length),Math.min(32,value.length));return out;
    }
    private static byte[] asn1ToJose(byte[] der)throws Exception{
        int i=0;if((der[i++]&255)!=0x30)throw new GeneralSecurityException("ecdsa der");
        int len=der[i++]&255;if(len>127)i+=(len&127);
        if((der[i++]&255)!=2)throw new GeneralSecurityException("ecdsa r");
        int rlen=der[i++]&255;byte[] r=Arrays.copyOfRange(der,i,i+rlen);i+=rlen;
        if((der[i++]&255)!=2)throw new GeneralSecurityException("ecdsa s");
        int slen=der[i++]&255;byte[] s=Arrays.copyOfRange(der,i,i+slen);
        return concat(pad(r),pad(s));
    }
    private static String bearer(String endpoint,String subject,String pub,String priv)throws Exception{
        URL u=new URL(endpoint);
        String aud=u.getProtocol()+"://"+u.getHost();
        String header=encode("{\"typ\":\"JWT\",\"alg\":\"ES256\"}".getBytes(StandardCharsets.UTF_8));
        String claims=encode(new JSONObject().put("aud",aud)
            .put("exp",System.currentTimeMillis()/1000+12*3600).put("sub",subject)
            .toString().getBytes(StandardCharsets.UTF_8));
        String payload=header+"."+claims;
        PrivateKey key=KeyFactory.getInstance("EC").generatePrivate(new ECPrivateKeySpec(
            new java.math.BigInteger(1,decode(priv)),curve()));
        Signature sig=Signature.getInstance("SHA256withECDSA");
        sig.initSign(key);sig.update(payload.getBytes(StandardCharsets.UTF_8));
        return "vapid t="+payload+"."+encode(asn1ToJose(sig.sign()))+", k="+pub;
    }
    private static byte[] encrypt(Subscription s,byte[] content)throws Exception{
        KeyPairGenerator gen=KeyPairGenerator.getInstance("EC");
        gen.initialize(new ECGenParameterSpec("secp256r1"));
        KeyPair eph=gen.generateKeyPair();
        byte[] server=publicBytes(eph.getPublic());
        byte[] client=decode(s.key);
        KeyAgreement agree=KeyAgreement.getInstance("ECDH");
        agree.init(eph.getPrivate());agree.doPhase(readPublic(client),true);
        byte[] secret=agree.generateSecret();
        byte[] ikm=hkdf(decode(s.auth),secret,concat(
            "WebPush: info\0".getBytes(StandardCharsets.UTF_8),client,server),32);
        byte[] salt=new byte[16];new SecureRandom().nextBytes(salt);
        byte[] cek=hkdf(salt,ikm,"Content-Encoding: aes128gcm\0".getBytes(StandardCharsets.UTF_8),16);
        byte[] nonce=hkdf(salt,ikm,"Content-Encoding: nonce\0".getBytes(StandardCharsets.UTF_8),12);
        Cipher c=Cipher.getInstance("AES/GCM/NoPadding");
        c.init(Cipher.ENCRYPT_MODE,new SecretKeySpec(cek,"AES"),new GCMParameterSpec(128,nonce));
        byte[] cipher=c.doFinal(concat(content,new byte[]{2}));
        ByteBuffer buf=ByteBuffer.allocate(16+4+1+server.length+cipher.length);
        buf.put(salt).putInt(4096).put((byte)server.length).put(server).put(cipher);
        return buf.array();
    }
    private static boolean send(Subscription s,String sub,String pub,String priv,long eventTime)throws Exception{
        if(!validEndpoint(s.url))return false;
        byte[] payload=new JSONObject().put("type","motion").put("id","y700-"+eventTime)
            .toString().getBytes(StandardCharsets.UTF_8);
        byte[] body=encrypt(s,payload);
        HttpURLConnection con=(HttpURLConnection)new URL(s.url).openConnection();
        con.setConnectTimeout(12000);con.setReadTimeout(12000);
        con.setInstanceFollowRedirects(false);con.setRequestMethod("POST");
        con.setRequestProperty("Authorization",bearer(s.url,sub,pub,priv));
        con.setRequestProperty("Content-Encoding","aes128gcm");
        con.setRequestProperty("Content-Type","application/octet-stream");
        con.setRequestProperty("TTL","120");
        con.setRequestProperty("Urgency","high");
        con.setDoOutput(true);
        con.setFixedLengthStreamingMode(body.length);
        try(OutputStream os=con.getOutputStream()){os.write(body);}
        int status=con.getResponseCode();con.disconnect();
        return status>=200&&status<300;
    }
}