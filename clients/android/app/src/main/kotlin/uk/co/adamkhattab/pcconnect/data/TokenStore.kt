package uk.co.adamkhattab.pcconnect.data

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

/**
 * The refresh token, encrypted under a hardware-backed key that never leaves
 * the Android Keystore.
 *
 * The app this replaces kept a password-equivalent SHA-256 in plain
 * SharedPreferences (S1-04): anything with access to the app's data directory
 * could read a credential that never expired and could not be revoked. What is
 * stored here is a rotating refresh token, encrypted at rest, and the access
 * token is never persisted at all — it lives in memory for fifteen minutes.
 */
class TokenStore(context: Context) {

    private val preferences = context.getSharedPreferences(PREFERENCES, Context.MODE_PRIVATE)

    fun readRefreshToken(): String? = decrypt(preferences.getString(KEY_REFRESH, null))

    fun writeRefreshToken(token: String) {
        preferences.edit().putString(KEY_REFRESH, encrypt(token)).apply()
    }

    fun clear() {
        preferences.edit().clear().apply()
    }

    /** The resolved backend, so a build-time default can be overridden at runtime (06 §1). */
    var baseUrl: String?
        get() = preferences.getString(KEY_BASE_URL, null)
        set(value) = preferences.edit().putString(KEY_BASE_URL, value).apply()

    /** Whether a fingerprint or face check is required before a destructive command. */
    var requireBiometricForDestructive: Boolean
        get() = preferences.getBoolean(KEY_BIOMETRIC, true)
        set(value) = preferences.edit().putBoolean(KEY_BIOMETRIC, value).apply()

    // ── keystore ─────────────────────────────────────────────────────────────

    private fun secretKey(): SecretKey {
        val keyStore = KeyStore.getInstance(ANDROID_KEYSTORE).apply { load(null) }

        (keyStore.getEntry(KEY_ALIAS, null) as? KeyStore.SecretKeyEntry)?.let { return it.secretKey }

        val generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, ANDROID_KEYSTORE)
        generator.init(
            KeyGenParameterSpec.Builder(
                KEY_ALIAS,
                KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT,
            )
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                // Deliberately not user-authentication-bound: the token has to
                // be readable in the background to refresh a session. The
                // human check belongs on the destructive action, not on every
                // read (ADR-0011).
                .setRandomizedEncryptionRequired(true)
                .build(),
        )

        return generator.generateKey()
    }

    private fun encrypt(value: String): String {
        val cipher = Cipher.getInstance(TRANSFORMATION)
        cipher.init(Cipher.ENCRYPT_MODE, secretKey())

        val ciphertext = cipher.doFinal(value.toByteArray(Charsets.UTF_8))
        val iv = cipher.iv

        // [1B iv length][iv][ciphertext+tag], base64 for SharedPreferences.
        val payload = ByteArray(1 + iv.size + ciphertext.size)
        payload[0] = iv.size.toByte()
        iv.copyInto(payload, 1)
        ciphertext.copyInto(payload, 1 + iv.size)

        return Base64.encodeToString(payload, Base64.NO_WRAP)
    }

    private fun decrypt(stored: String?): String? {
        if (stored.isNullOrBlank()) return null

        return runCatching {
            val payload = Base64.decode(stored, Base64.NO_WRAP)
            val ivLength = payload[0].toInt()
            val iv = payload.copyOfRange(1, 1 + ivLength)
            val ciphertext = payload.copyOfRange(1 + ivLength, payload.size)

            val cipher = Cipher.getInstance(TRANSFORMATION)
            cipher.init(Cipher.DECRYPT_MODE, secretKey(), GCMParameterSpec(TAG_BITS, iv))
            String(cipher.doFinal(ciphertext), Charsets.UTF_8)
        }.getOrNull()
        // A token that will not decrypt — the key was invalidated, the data was
        // restored onto another device — is a signed-out session, not a crash.
    }

    private companion object {
        const val PREFERENCES = "pcconnect.session"
        const val KEY_REFRESH = "refreshToken"
        const val KEY_BASE_URL = "baseUrl"
        const val KEY_BIOMETRIC = "requireBiometric"
        const val ANDROID_KEYSTORE = "AndroidKeyStore"
        const val KEY_ALIAS = "pcconnect.session.v2"
        const val TRANSFORMATION = "AES/GCM/NoPadding"
        const val TAG_BITS = 128
    }
}
