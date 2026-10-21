package com.chome.recorder;

import android.app.Application;
import com.google.firebase.FirebaseApp;
import com.google.firebase.FirebaseOptions;

public final class CHomeApp extends Application {
    @Override public void onCreate() {
        super.onCreate();
        if(FirebaseApp.getApps(this).isEmpty()) {
            FirebaseOptions options=new FirebaseOptions.Builder()
                .setApplicationId("1:280390101020:android:chomey700")
                .setApiKey("AIzaSyBeA0nsSOJ9HbV7ZLt4XAcgYX6uWfP7ANo")
                .setDatabaseUrl("https://chome-a8970-default-rtdb.firebaseio.com")
                .setProjectId("chome-a8970")
                .build();
            FirebaseApp.initializeApp(this,options);
        }
    }
}