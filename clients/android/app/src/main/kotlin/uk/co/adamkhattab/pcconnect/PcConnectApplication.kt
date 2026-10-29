package uk.co.adamkhattab.pcconnect

import android.app.Application
import uk.co.adamkhattab.pcconnect.data.PcConnectApi
import uk.co.adamkhattab.pcconnect.data.TokenStore

/**
 * A hand-rolled service locator rather than a DI framework: this app has four
 * screens and three singletons, and an annotation processor would cost more in
 * build time than it saves in wiring.
 */
class PcConnectApplication : Application() {

    lateinit var tokenStore: TokenStore
        private set

    lateinit var api: PcConnectApi
        private set

    override fun onCreate() {
        super.onCreate()

        tokenStore = TokenStore(this)
        api = PcConnectApi(
            tokens = tokenStore,
            // Build-time default, overridable at runtime and remembered. The app
            // this replaces compiled `AppConfig.localIp = "192.168.0.113"` into
            // a release build (S3-08).
            defaultBaseUrl = BuildConfig.DEFAULT_API_BASE_URL,
            clientVersion = BuildConfig.VERSION_NAME,
        )
    }
}
