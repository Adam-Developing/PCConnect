package uk.co.adamkhattab.pcconnect

import android.content.Intent
import android.os.Bundle
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.biometric.BiometricManager
import androidx.biometric.BiometricPrompt
import androidx.core.content.ContextCompat
import androidx.fragment.app.FragmentActivity
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.viewmodel.initializer
import androidx.lifecycle.viewmodel.viewModelFactory
import uk.co.adamkhattab.pcconnect.data.CommandTypes
import uk.co.adamkhattab.pcconnect.ui.AppViewModel
import uk.co.adamkhattab.pcconnect.ui.PcConnectApp
import uk.co.adamkhattab.pcconnect.ui.PcConnectTheme

// FragmentActivity rather than ComponentActivity: BiometricPrompt needs a
// fragment host to show its system dialog.
class MainActivity : FragmentActivity() {

    private val viewModel: AppViewModel by lazy {
        val application = application as PcConnectApplication

        ViewModelProvider(
            this,
            viewModelFactory { initializer { AppViewModel(application.api, application.tokenStore) } },
        )[AppViewModel::class.java]
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()

        setContent {
            PcConnectTheme {
                PcConnectApp(
                    viewModel = viewModel,
                    onShareDownloadLink = ::shareDownloadLink,
                    onBiometricPrompt = ::promptBiometric,
                    biometricAvailable = canAuthenticate(),
                )
            }
        }
    }

    /**
     * The design's "Send myself the download link".
     *
     * A share sheet rather than a mail the server sends: the phone already
     * knows every way this person messages themselves, and it needs no new
     * endpoint and no address stored anywhere.
     */
    private fun shareDownloadLink() {
        val share = Intent(Intent.ACTION_SEND).apply {
            type = "text/plain"
            putExtra(Intent.EXTRA_SUBJECT, "PCConnect for Windows")
            putExtra(Intent.EXTRA_TEXT, DOWNLOAD_URL)
        }

        startActivity(Intent.createChooser(share, "Send the download link"))
    }

    private fun canAuthenticate(): Boolean =
        BiometricManager.from(this).canAuthenticate(
            BiometricManager.Authenticators.BIOMETRIC_STRONG or BiometricManager.Authenticators.BIOMETRIC_WEAK,
        ) == BiometricManager.BIOMETRIC_SUCCESS

    /**
     * Standard Android fingerprint / biometric prompt.
     */
    private fun promptBiometric(title: String, subtitle: String, onResult: (Boolean) -> Unit) {
        val prompt = BiometricPrompt(
            this,
            ContextCompat.getMainExecutor(this),
            object : BiometricPrompt.AuthenticationCallback() {
                override fun onAuthenticationSucceeded(result: BiometricPrompt.AuthenticationResult) = onResult(true)

                override fun onAuthenticationError(errorCode: Int, errString: CharSequence) = onResult(false)
            },
        )

        prompt.authenticate(
            BiometricPrompt.PromptInfo.Builder()
                .setTitle(title)
                .setSubtitle(subtitle)
                .setNegativeButtonText("Use password")
                .setAllowedAuthenticators(
                    BiometricManager.Authenticators.BIOMETRIC_STRONG or BiometricManager.Authenticators.BIOMETRIC_WEAK,
                )
                .build(),
        )
    }

    private companion object {
        const val DOWNLOAD_URL = "https://pcconnect.adamdeveloping.co.uk/download"
    }
}
