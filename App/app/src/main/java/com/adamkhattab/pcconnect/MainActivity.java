package com.adamkhattab.pcconnect;

import android.content.Context;
import android.content.Intent;
import android.os.AsyncTask;
import android.os.Bundle;
import android.os.Handler;
import android.util.Log;
import android.view.View;
import android.widget.AdapterView;
import android.widget.ArrayAdapter;
import android.widget.Button;
import android.widget.Spinner;
import android.widget.TextView;
import android.widget.Toast;

import androidx.annotation.NonNull;
import androidx.appcompat.app.AppCompatActivity;
import androidx.biometric.BiometricManager;
import androidx.biometric.BiometricPrompt;
import androidx.core.content.ContextCompat;
import androidx.preference.PreferenceManager;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.io.BufferedReader;
import java.io.IOException;
import java.io.InputStream;
import java.io.InputStreamReader;
import java.net.HttpURLConnection;
import java.net.URL;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.Executor;

import okhttp3.Call;
import okhttp3.Callback;
import okhttp3.OkHttpClient;
import okhttp3.Request;
import okhttp3.Response;

public class MainActivity extends AppCompatActivity {

    private static final String TAG = "MainActivity";
    private static final long INTERNET_CHECK_INTERVAL_MS = 5_000;

    private TextView resultTextView;
    private Spinner spinner;
    private String pcName = "";

    private Button
            btnSleep, btnHibernate, btnShutdown,
            btnLock, btnSignout, btnRestart,
            btnLogoutApp, btnReminder, btnViewReminders, btnSettings;

    private final Handler handler = new Handler();
    private Runnable internetChecker;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        setContentView(R.layout.activity_main);

        // build the repeating task now that "this" & handler exist
        internetChecker = () -> {
            new CheckInternetStatusTask(MainActivity.this).execute();
            handler.postDelayed(internetChecker, INTERNET_CHECK_INTERVAL_MS);
        };

        findViews();
        setupSpinner();
        fetchPcNames();
        handler.post(internetChecker);

        // Settings opens, Up arrow handled in SettingsActivity so it won't relaunched MainActivity
        setupButton(btnSettings, v ->
                startActivity(new Intent(MainActivity.this, SettingsActivity.class))
        );

        // Power‑state buttons
        setupActionButton(btnSleep,     "Sleep",     "PC has been placed on sleep");
        setupActionButton(btnHibernate, "Hibernate", "PC has been placed on hibernate");
        setupActionButton(btnShutdown,  "Shutdown",  "PC has been placed on shutdown");
        setupActionButton(btnLock,      "Lock",      "PC has been locked");
        setupActionButton(btnSignout,   "Signout",   "PC has been signed out");
        setupActionButton(btnRestart,   "Restart",   "PC has been restarted");

        // Reminders & logout
        setupButton(btnReminder,      v -> startActivity(new Intent(this, ReminderActivity.class)));
        setupButton(btnViewReminders, v -> startActivity(new Intent(this, ListRemindersActivity.class)));
        setupButton(btnLogoutApp, v -> {
            SharedPrefManager.getInstance(this).clearApiKey();
            startActivity(new Intent(this, LoginActivity.class));
            finish();
        });
    }

    @Override
    protected void onResume() {
        super.onResume();

        // If no API key, send back to login (and finish Main so back won't return here)
        String apiKey = SharedPrefManager.getInstance(this).getApiKey();
        if (apiKey == null || apiKey.isEmpty()) {
            startActivity(new Intent(this, LoginActivity.class));
            finish();
            return;
        }

        // otherwise refresh the PC list
        fetchPcNames();
    }

    @Override
    protected void onDestroy() {
        super.onDestroy();
        handler.removeCallbacks(internetChecker);
    }

    // —————————————————————
    // View‑binding & helpers
    // —————————————————————

    private void findViews() {
        resultTextView    = findViewById(R.id.Result);
        spinner           = findViewById(R.id.PCName);

        btnSleep          = findViewById(R.id.Sleep);
        btnHibernate      = findViewById(R.id.Hibernate);
        btnShutdown       = findViewById(R.id.Shutdown);
        btnLock           = findViewById(R.id.Lock);
        btnSignout        = findViewById(R.id.Signout);
        btnRestart        = findViewById(R.id.Restart);
        btnLogoutApp      = findViewById(R.id.LogOutAPP);
        btnReminder       = findViewById(R.id.Reminder);
        btnViewReminders  = findViewById(R.id.ViewReminders);
        btnSettings       = findViewById(R.id.Settings);
    }

    private void setupSpinner() {
        spinner.setOnItemSelectedListener(new AdapterView.OnItemSelectedListener() {
            @Override public void onItemSelected(AdapterView<?> p, View v, int pos, long id) {
                pcName = (String) spinner.getItemAtPosition(pos);
            }
            @Override public void onNothingSelected(AdapterView<?> p) {}
        });
    }

    private void setupButton(Button btn, View.OnClickListener l) {
        btn.setOnClickListener(l);
    }

    private void setupActionButton(Button btn, String cmd, String msg) {
        btn.setOnClickListener(v -> {
            boolean enabled = PreferenceManager
                    .getDefaultSharedPreferences(this)
                    .getBoolean(cmd, true);

            if (enabled) {
                authenticateAndSend(cmd, msg);
            } else {
                sendStateChange(cmd, msg);
            }
        });
    }

    private void authenticateAndSend(String command, String successMsg) {
        BiometricManager bm = BiometricManager.from(this);
        if (bm.canAuthenticate() != BiometricManager.BIOMETRIC_SUCCESS) {
            sendStateChange(command, successMsg);
            return;
        }

        Executor exec = ContextCompat.getMainExecutor(this);
        BiometricPrompt prompt = new BiometricPrompt(this, exec,
                new BiometricPrompt.AuthenticationCallback() {
                    @Override public void onAuthenticationSucceeded(
                            @NonNull BiometricPrompt.AuthenticationResult res) {
                        sendStateChange(command, successMsg);
                    }
                    @Override public void onAuthenticationFailed() {
                        Toast.makeText(MainActivity.this,
                                "Authentication failed", Toast.LENGTH_SHORT).show();
                    }
                }
        );

        BiometricPrompt.PromptInfo info = new BiometricPrompt.PromptInfo.Builder()
                .setTitle("PCConnect Login")
                .setDescription("Use fingerprint or device PIN")
                .setAllowedAuthenticators(
                        BiometricManager.Authenticators.BIOMETRIC_STRONG |
                                BiometricManager.Authenticators.DEVICE_CREDENTIAL
                )
                .build();

        prompt.authenticate(info);
    }

    private void sendStateChange(String command, String successMsg) {
        NetworkUtils.StateChange(getApplicationContext(), pcName, command);
        resultTextView.setText(successMsg);
        handler.postDelayed(() -> resultTextView.setText(""), 5_000);
    }

    // —————————————————————
    // Fetch PC‑names & Internet check
    // —————————————————————

    private void fetchPcNames() {
        String apiKey = SharedPrefManager.getInstance(this).getApiKey();
        if (apiKey == null || apiKey.isEmpty()) {
            Log.e(TAG, "fetchPcNames: no API key");
            return;
        }

        OkHttpClient client = new OkHttpClient();
        Request req = new Request.Builder()
                .url("https://pcconnect.adamkhattab.co.uk/api/pcconnect/PCNames.php")
                .addHeader("X-API-Key", apiKey)
                .build();

        client.newCall(req).enqueue(new Callback() {
            @Override public void onFailure(Call c, IOException e) {
                Log.e(TAG, "PCNames fetch failed", e);
            }
            @Override public void onResponse(Call c, Response r) throws IOException {
                if (!r.isSuccessful()) {
                    Log.e(TAG, "PCNames bad HTTP: " + r.code());
                    return;
                }
                String body = r.body().string();
                runOnUiThread(() -> {
                    try {
                        JSONObject json = new JSONObject(body);
                        JSONArray arr = json.getJSONArray("PCNames");
                        List<String> list = new ArrayList<>();
                        for (int i = 0; i < arr.length(); i++) {
                            list.add(arr.getString(i));
                        }
                        ArrayAdapter<String> adapter = new ArrayAdapter<>(
                                MainActivity.this,
                                android.R.layout.simple_spinner_item,
                                list
                        );
                        adapter.setDropDownViewResource(
                                android.R.layout.simple_spinner_dropdown_item
                        );
                        spinner.setAdapter(adapter);
                    } catch (JSONException e) {
                        Log.e(TAG, "PCNames JSON error", e);
                    }
                });
            }
        });
    }

    private class CheckInternetStatusTask extends AsyncTask<Void,Void,Boolean> {
        private final Context ctx;
        CheckInternetStatusTask(Context c){ ctx = c; }

        @Override
        protected Boolean doInBackground(Void...v) {
            String apiKey = SharedPrefManager.getInstance(ctx).getApiKey();
            if (apiKey==null||apiKey.isEmpty()||pcName.isEmpty()) return false;
            try {
                HttpURLConnection conn = (HttpURLConnection)
                        new URL("https://pcconnect.adamkhattab.co.uk/api/pcconnect/checkinternet.php")
                                .openConnection();
                conn.setRequestProperty("X-API-Key", apiKey);
                conn.setRequestProperty("PCName", pcName);
                if (conn.getResponseCode()!=HttpURLConnection.HTTP_OK) {
                    conn.disconnect(); return false;
                }
                try (InputStream in=conn.getInputStream();
                     BufferedReader r=new BufferedReader(new InputStreamReader(in))) {
                    return "yes".equalsIgnoreCase(r.readLine());
                } finally { conn.disconnect(); }
            } catch(IOException e){
                Log.e(TAG,"Internet check failed",e);
                return false;
            }
        }

        @Override
        protected void onPostExecute(Boolean online) {
            btnSleep    .setEnabled(online);
            btnHibernate.setEnabled(online);
            btnShutdown .setEnabled(online);
            btnLock     .setEnabled(online);
            btnSignout  .setEnabled(online);
            btnRestart  .setEnabled(online);
        }
    }
}
