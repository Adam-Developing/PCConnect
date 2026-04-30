package com.adamkhattab.pcconnect;

import android.content.Context;
import android.content.SharedPreferences;

public class SharedPrefManager {
    private static final String PREF_NAME = "MyPrefs";
    private static final String API_KEY = "apiKey";

    private static SharedPrefManager mInstance;
    private final SharedPreferences mSharedPreferences;

    private SharedPrefManager(Context context) {
        mSharedPreferences = context.getSharedPreferences(PREF_NAME, Context.MODE_PRIVATE);
    }

    public static synchronized SharedPrefManager getInstance(Context context) {
        if (mInstance == null) {
            mInstance = new SharedPrefManager(context);
        }
        return mInstance;
    }

    public void setApiKey(String apiKey) {
        SharedPreferences.Editor editor = mSharedPreferences.edit();
        editor.putString(API_KEY, apiKey);
        editor.apply();
    }
    public void clearApiKey() {
        SharedPreferences.Editor editor = mSharedPreferences.edit();
        editor.remove(API_KEY);
        editor.apply();
    }

    public String getApiKey() {
        return mSharedPreferences.getString(API_KEY, null);
    }
}
