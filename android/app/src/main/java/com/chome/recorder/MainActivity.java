package com.chome.recorder;

import android.Manifest;
import android.app.Activity;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.content.ClipData;
import android.content.ClipboardManager;
import android.content.Context;
import android.view.View;
import android.widget.*;
import androidx.core.content.ContextCompat;
import com.google.firebase.auth.FirebaseAuth;
import com.google.firebase.auth.FirebaseUser;

public final class MainActivity extends Activity {
    private final Handler main=new Handler(Looper.getMainLooper());
    private TextView account, current, help, pushState;
    private EditText owner, pat,folder;
    private String deviceUid="";
    private boolean startAfterPermission=false;
    private final Runnable refresh=new Runnable() {
        @Override public void run() {
            if(current!=null)current.setText("서비스 상태: "+Settings.publicPrefs(MainActivity.this).getString("monitorState","중지됨"));
            if(pushState!=null)pushState.setText("웹푸시 발송: "+Settings.publicPrefs(MainActivity.this).getString("tabletPushStatus","설정 대기"));
            main.postDelayed(this,2000);
        }
    };
    @Override public void onCreate(Bundle state) {
        super.onCreate(state);
        ScrollView scroll=new ScrollView(this);
        LinearLayout base=new LinearLayout(this);
        base.setOrientation(LinearLayout.VERTICAL);
        int p=(int)(18*getResources().getDisplayMetrics().density);
        base.setPadding(p,p,p,p);scroll.addView(base);setContentView(scroll);
        TextView title=label("CHome Y700 녹화기");title.setTextSize(24);base.addView(title);
        base.addView(label("PC 없이 Y700 카메라 · 로컬 녹화 · NAVER MYBOX 백업"));
        base.addView(label("준비: (1) 아래 기기 UID를 폰 CHome에 연결 (2) MYBOX PAT 입력 (3) 이 화면에서 감시 서비스 시작"));
        account=label("기기 인증을 준비하는 중");account.setTextIsSelectable(true);base.addView(account);
        Button copy=button("내 기기 UID 복사",base);
        copy.setOnClickListener(v->{
            ((ClipboardManager)getSystemService(Context.CLIPBOARD_SERVICE)).setPrimaryClip(ClipData.newPlainText("CHome device UID",deviceUid));
            Toast.makeText(this,"기기 UID 복사됨",Toast.LENGTH_SHORT).show();
        });
        owner=input("CHome 웹앱에 표시된 소유자 UID",base);
        owner.setText(Settings.publicPrefs(this).getString(Settings.KEY_OWNER,""));
        pat=input("MYBOX 개인 액세스 토큰 (기존 값은 표시 안 함)",base);
        folder=input("MYBOX 업로드 폴더 ID (비우면 루트)",base);
        folder.setText(Settings.publicPrefs(this).getString(Settings.KEY_FOLDER,""));
        Button save=button("연결 정보 저장",base);
        save.setOnClickListener(v->saveSettings());
        Button start=button("이 태블릿 감시 서비스 켜기",base);
        start.setOnClickListener(v->startCamera());
        Button stop=button("이 태블릿 감시 서비스 완전 종료",base);
        stop.setOnClickListener(v->{
            stopService(new Intent(this,MonitorService.class));
            Settings.publicPrefs(this).edit().putString("monitorState","종료 요청됨").apply();
        });
        current=label("서비스 상태: 확인 중");base.addView(current);
        pushState=label("웹푸시 발송: 설정 대기");base.addView(pushState);
        Button battery=button("백그라운드 배터리 제한 해제 설정 열기",base);
        battery.setOnClickListener(v->{
            Intent screen=new Intent(android.provider.Settings.ACTION_APPLICATION_DETAILS_SETTINGS,
                android.net.Uri.parse("package:"+getPackageName()));startActivity(screen);
        });
        help=label("폰 CHome에서 Y700 시작/중지를 눌러 움직임 또는 상시 녹화 모드를 변경할 수 있습니다.\n"
            +"화면을 끈 상태의 감시는 태블릿 OS 설정에 따라 다를 수 있습니다. 첫 설치 후 실제 장시간 테스트가 필요합니다.\n"
            +"감시 서비스가 완전히 꺼져 있거나 앱이 강제 종료되면 원격으로 카메라를 다시 켤 수 없습니다.\n"
            +"MYBOX PAT는 이 태블릿의 암호화 저장소에만 저장하며 Firebase로 전송하지 않습니다.");
        base.addView(help);
        FirebaseAuth.getInstance().signInAnonymously().addOnSuccessListener(result->{
            FirebaseUser u=result.getUser();
            if(u!=null){deviceUid=u.getUid();account.setText("Y700 기기 UID: "+deviceUid);}
        }).addOnFailureListener(e->account.setText("기기 Firebase 인증 실패: "+e.getMessage()+
            "\nFirebase Authentication에서 익명 인증 사용 설정이 필요할 수 있습니다."));
        main.post(refresh);
    }
    private TextView label(String s){TextView v=new TextView(this);v.setText(s);v.setTextSize(15);v.setPadding(0,7,0,7);return v;}
    private EditText input(String hint,LinearLayout parent){
        EditText e=new EditText(this);e.setSingleLine(true);e.setTextSize(15);e.setHint(hint);parent.addView(e);return e;
    }
    private Button button(String title,LinearLayout parent){Button b=new Button(this);b.setText(title);parent.addView(b);return b;}
    private boolean saveSettings(){
        String own=owner.getText().toString().trim();
        if(!own.matches("[A-Za-z0-9_-]{15,128}")){Toast.makeText(this,"소유자 UID가 올바르지 않습니다",Toast.LENGTH_LONG).show();return false;}
        Settings.publicPrefs(this).edit().putString(Settings.KEY_OWNER,own)
            .putString(Settings.KEY_FOLDER,folder.getText().toString().trim()).apply();
        String v=pat.getText().toString().trim();
        if(!v.isEmpty()){
            try{Settings.saveToken(this,v);pat.setText("");}
            catch(Exception e){Toast.makeText(this,"토큰 암호화 저장 실패",Toast.LENGTH_LONG).show();return false;}
        }
        Toast.makeText(this,"설정 저장 완료",Toast.LENGTH_SHORT).show();return true;
    }
    private void startCamera(){
        if(!saveSettings())return;
        if(deviceUid.isEmpty()){Toast.makeText(this,"기기 Firebase 인증이 먼저 필요합니다",Toast.LENGTH_LONG).show();return;}
        if(ContextCompat.checkSelfPermission(this,Manifest.permission.CAMERA)!=PackageManager.PERMISSION_GRANTED){
            startAfterPermission=true;requestPermissions(new String[]{Manifest.permission.CAMERA},111);return;
        }
        launchService();
    }
    private void launchService(){
        try{
            Intent i=new Intent(this,MonitorService.class);
            ContextCompat.startForegroundService(this,i);
        }catch(Exception e){current.setText("서비스 시작 실패: "+e.getMessage());}
        if(Build.VERSION.SDK_INT>=33 && ContextCompat.checkSelfPermission(this,Manifest.permission.POST_NOTIFICATIONS)!=PackageManager.PERMISSION_GRANTED)
            requestPermissions(new String[]{Manifest.permission.POST_NOTIFICATIONS},112);
    }
    @Override public void onRequestPermissionsResult(int request,String[] names,int[] grants){
        super.onRequestPermissionsResult(request,names,grants);
        if(request==111&&startAfterPermission){
            startAfterPermission=false;
            if(grants.length>0&&grants[0]==PackageManager.PERMISSION_GRANTED)launchService();
            else current.setText("카메라 권한이 거부되어 감시할 수 없습니다.");
        }
    }
    @Override public void onDestroy(){main.removeCallbacks(refresh);super.onDestroy();}
}