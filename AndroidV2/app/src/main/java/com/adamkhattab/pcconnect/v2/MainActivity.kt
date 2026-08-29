package com.adamkhattab.pcconnect.v2

import android.content.Intent
import android.net.Uri
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.viewModels
import com.adamkhattab.pcconnect.v2.sync.ControllerSyncWorker

class MainActivity : ComponentActivity() {
    private val viewModel: MainViewModel by viewModels {
        MainViewModel.Factory((application as PCConnectApplication).container)
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        ControllerSyncWorker.schedule(this)
        setContent { PCConnectApp(viewModel) }
        handleAccountLink(intent)
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        handleAccountLink(intent)
    }

    private fun handleAccountLink(intent: Intent?) {
        val uri = intent?.data ?: return
        if (uri.scheme != "https" || !uri.host.equals(BuildConfig.RP_HOST, ignoreCase = true)) return
        val token = uri.getQueryParameter("token") ?: fragmentToken(uri) ?: return
        when (uri.path) {
            "/verify-email" -> viewModel.verifyEmail(token)
            "/reset-password" -> viewModel.beginPasswordReset(token)
        }
    }

    private fun fragmentToken(uri: Uri): String? = uri.fragment
        ?.split('&')
        ?.mapNotNull { part -> part.split('=', limit = 2).takeIf { it.size == 2 } }
        ?.firstOrNull { it[0] == "token" }
        ?.get(1)
        ?.let(Uri::decode)
}
