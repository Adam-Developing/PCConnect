package com.adamkhattab.pcconnect;

import android.content.Intent;
import android.content.SharedPreferences;
import android.net.Uri;
import android.os.Bundle;
import android.util.Log;
import android.view.View;
import android.widget.Button;
import android.widget.EditText;
import android.widget.Toast;

import androidx.annotation.NonNull;
import androidx.appcompat.app.AppCompatActivity;
import androidx.biometric.BiometricManager;
import androidx.biometric.BiometricPrompt;
import androidx.core.content.ContextCompat;
import androidx.preference.PreferenceManager;

import java.io.IOException;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.util.concurrent.Executor;

import okhttp3.Call;
import okhttp3.Callback;
import okhttp3.FormBody;
import okhttp3.OkHttpClient;
import okhttp3.Request;
import okhttp3.RequestBody;
import okhttp3.Response;

public class LoginActivity extends AppCompatActivity {

    private EditText editTextUsername;
    private EditText editTextPassword;
    private Button buttonLogin;
    private Button buttonSignUp;
    private Button buttonForgotPassword;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        setContentView(R.layout.activity_login);

        // find views
        editTextUsername     = findViewById(R.id.editTextUsername);
        editTextPassword     = findViewById(R.id.editTextPassword);
        buttonLogin          = findViewById(R.id.buttonLogin);
        buttonSignUp         = findViewById(R.id.buttonSignUp);
        buttonForgotPassword = findViewById(R.id.buttonForgotPassword);

        // load preferences
        SharedPreferences prefs    = PreferenceManager.getDefaultSharedPreferences(this);
        String           apiKey    = SharedPrefManager.getInstance(this).getApiKey();
        boolean          useBiometric = prefs.getBoolean("App", true);

        if (apiKey != null && !apiKey.isEmpty()) {
            // already logged in: only show the Login button
            editTextUsername.setVisibility(View.GONE);
            editTextPassword.setVisibility(View.GONE);
            buttonSignUp.setVisibility(View.GONE);
            buttonForgotPassword.setVisibility(View.GONE);

            if (!useBiometric) {
                // skip biometric entirely
                goToMain();
                return;
            }

            // biometric-on-press
            buttonLogin.setText("Unlock with fingerprint");
            buttonLogin.setOnClickListener(v -> promptBiometric());
            return;
        }

        // no API‑key → show normal form
        buttonLogin.setOnClickListener(v -> performUsernamePasswordLogin());
        buttonSignUp.setOnClickListener(v ->
                startActivity(new Intent(Intent.ACTION_VIEW,
                        Uri.parse("https://pcconnect.adamkhattab.co.uk"))));
        buttonForgotPassword.setOnClickListener(v ->
                startActivity(new Intent(Intent.ACTION_VIEW,
                        Uri.parse("https://pcconnect.adamkhattab.co.uk/password-reset"))));
    }

    private void promptBiometric() {
        BiometricManager bm = BiometricManager.from(this);
        if (bm.canAuthenticate() != BiometricManager.BIOMETRIC_SUCCESS) {
            // fallback if no hardware or no enrolment
            goToMain();
            return;
        }

        Executor exec = ContextCompat.getMainExecutor(this);
        BiometricPrompt prompt = new BiometricPrompt(this, exec,
                new BiometricPrompt.AuthenticationCallback() {
                    @Override
                    public void onAuthenticationSucceeded(
                            @NonNull BiometricPrompt.AuthenticationResult result) {
                        Toast.makeText(getApplicationContext(),
                                "Authentication succeeded", Toast.LENGTH_SHORT).show();
                        goToMain();
                    }
                    @Override
                    public void onAuthenticationFailed() {
                        Toast.makeText(getApplicationContext(),
                                "Authentication failed", Toast.LENGTH_SHORT).show();
                    }
                }
        );

        BiometricPrompt.PromptInfo info = new BiometricPrompt.PromptInfo.Builder()
                .setTitle("PCConnect Unlock")
                .setDescription("Use your fingerprint or PIN")
                .setAllowedAuthenticators(
                        BiometricManager.Authenticators.BIOMETRIC_STRONG |
                                BiometricManager.Authenticators.DEVICE_CREDENTIAL
                )
                .build();

        prompt.authenticate(info);
    }

    private void performUsernamePasswordLogin() {
        String username = editTextUsername.getText().toString().trim();
        String password = sha256Hash(editTextPassword.getText().toString());
        if (username.isEmpty() || password == null) {
            Toast.makeText(this,
                    "Please enter both username and password",
                    Toast.LENGTH_SHORT).show();
            return;
        }

        OkHttpClient client = new OkHttpClient();
        RequestBody body = new FormBody.Builder()
                .add("loginUsername", username)
                .add("loginPassword", password)
                .build();

        Request request = new Request.Builder()
                .url("https://pcconnect.adamkhattab.co.uk/api/login.php")
                .post(body)
                .build();

        client.newCall(request).enqueue(new Callback() {
            @Override public void onFailure(Call c, IOException e) {
                Log.e("LoginActivity","Login network error",e);
                runOnUiThread(() ->
                        Toast.makeText(LoginActivity.this,
                                "Network error, please try again",
                                Toast.LENGTH_SHORT).show()
                );
            }
            @Override public void onResponse(Call c, Response r) throws IOException {
                String resp = r.body().string();
                runOnUiThread(() -> {
                    if (!"Invalid username or password.".equals(resp)) {
                        // save API‑key and go in
                        SharedPrefManager.getInstance(LoginActivity.this)
                                .setApiKey(resp);
                        Toast.makeText(LoginActivity.this,
                                "Login successful", Toast.LENGTH_SHORT).show();
                        goToMain();
                    } else {
                        Toast.makeText(LoginActivity.this,
                                "Invalid username or password",
                                Toast.LENGTH_SHORT).show();
                    }
                });
            }
        });
    }

    private void goToMain() {
        startActivity(new Intent(LoginActivity.this, MainActivity.class));
        finish();
    }

    public static String sha256Hash(String input) {
        try {
            MessageDigest md = MessageDigest.getInstance("SHA-256");
            byte[]         bytes = md.digest(input.getBytes());
            StringBuilder sb    = new StringBuilder();
            for (byte b : bytes) {
                sb.append(String.format("%02x", b & 0xff));
            }
            return sb.toString();
        } catch (NoSuchAlgorithmException e) {
            Log.e("LoginActivity","SHA‑256 unavailable",e);
            return null;
        }
    }
}
