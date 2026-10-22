package com.chome.recorder;

import android.content.Context;
import android.os.Environment;
import androidx.annotation.NonNull;
import androidx.work.*;
import org.json.JSONObject;
import java.io.*;
import java.net.*;
import java.nio.charset.StandardCharsets;
import java.util.concurrent.TimeUnit;

public final class MyboxUploadWorker extends Worker {
    public MyboxUploadWorker(@NonNull Context context,@NonNull WorkerParameters params) {
        super(context,params);
    }
    static File videoFolder(Context c) {
        File base=c.getExternalFilesDir(Environment.DIRECTORY_MOVIES);
        return new File(base==null?c.getFilesDir():base,"CHome");
    }
    static void schedule(Context c) {
        Constraints constraints=new Constraints.Builder().setRequiredNetworkType(NetworkType.CONNECTED).build();
        WorkManager manager=WorkManager.getInstance(c);
        manager.enqueueUniqueWork("mybox-upload-now",ExistingWorkPolicy.KEEP,
            new OneTimeWorkRequest.Builder(MyboxUploadWorker.class).setConstraints(constraints)
                .setBackoffCriteria(BackoffPolicy.EXPONENTIAL,1,TimeUnit.MINUTES).build());
        manager.enqueueUniquePeriodicWork("mybox-upload-periodic",ExistingPeriodicWorkPolicy.KEEP,
            new PeriodicWorkRequest.Builder(MyboxUploadWorker.class,15,TimeUnit.MINUTES)
                .setConstraints(constraints).build());
    }
    @NonNull @Override public Result doWork() {
        String pat=Settings.token(getApplicationContext());
        if(pat.isEmpty())return Result.success(); // No secrets configured yet: do not leak or delete local clips.
        File folder=videoFolder(getApplicationContext());
        File[] files=folder.listFiles((f,n)->n.endsWith(".mp4"));
        if(files==null)return Result.success();
        java.util.Arrays.sort(files,(a,b)->Long.compare(a.lastModified(),b.lastModified()));
        int done=0;
        try{
            for(File file:files){
                if(isStopped())return Result.retry();
                if(new File(file.getAbsolutePath()+".uploaded").exists())continue;
                uploadOne(file,pat,Settings.publicPrefs(getApplicationContext()).getString(Settings.KEY_FOLDER,""));
                File marker=new File(file.getAbsolutePath()+".uploaded");
                try(FileOutputStream out=new FileOutputStream(marker)) {
                    out.write("verified_response\n".getBytes(StandardCharsets.UTF_8));out.getFD().sync();
                }
                if(++done>=20)return Result.retry();
            }
            return Result.success();
        }catch(Exception ex){
            // Do not log PAT, Authorization headers, upload URLs or response bodies.
            return Result.retry();
        }
    }
    private void uploadOne(File f,String pat,String parent) throws Exception {
        JSONObject body=new JSONObject();
        body.put("fileName","Y700_"+f.getName());
        body.put("fileSize",f.length());
        body.put("isOverwrite",false);
        if(parent!=null&&!parent.trim().isEmpty())body.put("parentId",parent.trim());
        HttpURLConnection pre=(HttpURLConnection)new URL("https://open-api.mybox.naver.com/v1/drive/files").openConnection();
        pre.setRequestMethod("POST");
        pre.setConnectTimeout(15000);pre.setReadTimeout(20000);
        pre.setRequestProperty("Authorization","Bearer "+pat);
        pre.setRequestProperty("Content-Type","application/json;charset=utf-8");
        pre.setDoOutput(true);
        byte[] payload=body.toString().getBytes(StandardCharsets.UTF_8);
        try(OutputStream o=pre.getOutputStream()){o.write(payload);}
        int status=pre.getResponseCode();
        if(status!=201&&status!=200){pre.disconnect();throw new IOException("upload prepare http "+status);}
        String response;
        try(InputStream in=pre.getInputStream()){response=readAll(in,65536);}
        finally{pre.disconnect();}
        String uploadUrl=new JSONObject(response).getString("uploadUrl");
        URL endpoint=new URL(uploadUrl);
        if(!"https".equalsIgnoreCase(endpoint.getProtocol()))throw new IOException("invalid upload scheme");
        String host=endpoint.getHost().toLowerCase(java.util.Locale.ROOT);
        if(!host.endsWith(".naver.com")&&!host.equals("naver.com")&&!host.endsWith(".navercorp.com"))
            throw new IOException("unknown upload server");
        String boundary="CHomeY700"+java.util.UUID.randomUUID().toString().replace("-","");
        HttpURLConnection req=(HttpURLConnection)endpoint.openConnection();
        req.setRequestMethod("POST");req.setDoOutput(true);req.setConnectTimeout(20000);req.setReadTimeout(120000);
        req.setRequestProperty("Content-Type","multipart/form-data; boundary="+boundary);
        req.setChunkedStreamingMode(64*1024);
        byte[] begin=("--"+boundary+"\r\nContent-Disposition: form-data; name=\"Filedata\"; filename=\""
            +f.getName()+"\"\r\nContent-Type: video/mp4\r\n\r\n").getBytes(StandardCharsets.UTF_8);
        byte[] end=("\r\n--"+boundary+"--\r\n").getBytes(StandardCharsets.UTF_8);
        try(OutputStream out=req.getOutputStream();InputStream in=new FileInputStream(f)){
            out.write(begin);
            byte[] buffer=new byte[65536];int n;
            while((n=in.read(buffer))!=-1){if(isStopped())throw new IOException("upload interrupted");out.write(buffer,0,n);}
            out.write(end);
        }
        int result=req.getResponseCode();req.disconnect();
        if(result<200||result>=300)throw new IOException("upload http "+result);
    }
    static String readAll(InputStream in,int limit) throws IOException {
        ByteArrayOutputStream out=new ByteArrayOutputStream();
        byte[] buffer=new byte[4096];int n;
        while((n=in.read(buffer))!=-1){if(out.size()+n>limit)throw new IOException("response too long");out.write(buffer,0,n);}
        return out.toString("UTF-8");
    }
}