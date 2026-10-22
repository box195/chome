package com.chome.recorder;

import android.content.Context;
import android.content.SharedPreferences;
import androidx.security.crypto.EncryptedSharedPreferences;
import androidx.security.crypto.MasterKey;

final class Settings {
    static final String PREFS="chome_settings";
    static final String KEY_OWNER="ownerUid", KEY_FOLDER="myboxFolder";
    static SharedPreferences publicPrefs(Context c) { return c.getSharedPreferences(PREFS,Context.MODE_PRIVATE); }
    static SharedPreferences secretPrefs(Context c) throws Exception {
        MasterKey k=new MasterKey.Builder(c).setKeyScheme(MasterKey.KeyScheme.AES256_GCM).build();
        return EncryptedSharedPreferences.create(c,"chome_secrets",k,
            EncryptedSharedPreferences.PrefKeyEncryptionScheme.AES256_SIV,
            EncryptedSharedPreferences.PrefValueEncryptionScheme.AES256_GCM);
    }
    static String token(Context c){
        try{return secretPrefs(c).getString("myboxPat","");}
        catch(Exception e){return "";}
    }
    static void saveToken(Context c,String token) throws Exception {
        secretPrefs(c).edit().putString("myboxPat",token).commit();
    }
}