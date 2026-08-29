package com.adamkhattab.pcconnect.v2.data

object PlatformCapabilities {
    // Credential Manager passkeys require Android 9 (API 28). Password login
    // remains available throughout the supported API 24+ range.
    fun supportsPasskeys(sdkInt: Int): Boolean = sdkInt >= 28
}
