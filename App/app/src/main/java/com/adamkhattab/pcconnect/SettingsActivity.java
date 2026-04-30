package com.adamkhattab.pcconnect;

import android.os.Bundle;
import androidx.appcompat.app.ActionBar;
import androidx.appcompat.app.AppCompatActivity;
import androidx.biometric.BiometricManager;
import androidx.preference.PreferenceFragmentCompat;
import androidx.preference.SwitchPreferenceCompat;

public class SettingsActivity extends AppCompatActivity {
    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        setContentView(R.layout.settings_activity);

        // show the Up arrow in the action bar
        ActionBar actionBar = getSupportActionBar();
        if (actionBar != null) {
            actionBar.setDisplayHomeAsUpEnabled(true);
        }

        if (savedInstanceState == null) {
            getSupportFragmentManager()
                    .beginTransaction()
                    .replace(R.id.settings, new SettingsFragment())
                    .commit();
        }
    }

    @Override
    public boolean onSupportNavigateUp() {
        finish();   // just close SettingsActivity
        return true;
    }

    public static class SettingsFragment extends PreferenceFragmentCompat {
        @Override
        public void onCreatePreferences(Bundle savedInstanceState, String rootKey) {
            setPreferencesFromResource(R.xml.root_preferences, rootKey);

            // Check biometric availability
            BiometricManager bm = BiometricManager.from(getContext());
            boolean canAuth = (bm.canAuthenticate() == BiometricManager.BIOMETRIC_SUCCESS);

            // List of biometric-dependent preference keys
            String[] keys = {"App", "Sleep", "Hibernate", "Shutdown", "Lock", "Signout", "Restart"};
            for (String key : keys) {
                SwitchPreferenceCompat pref = findPreference(key);
                if (pref != null && !canAuth) {
                    pref.setEnabled(false);
                    pref.setChecked(false); // turn the switch off
                    pref.setSummary("Requires biometric authentication");
                }
            }
        }
    }
}
