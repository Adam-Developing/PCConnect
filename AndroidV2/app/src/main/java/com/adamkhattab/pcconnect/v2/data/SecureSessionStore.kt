package com.adamkhattab.pcconnect.v2.data

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.stringPreferencesKey
import androidx.datastore.preferences.preferencesDataStore
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.map

private val Context.sessionDataStore by preferencesDataStore("secure_session")

interface SessionStore {
    suspend fun readRefreshToken(): String?
    suspend fun writeRefreshToken(value: String)
    suspend fun clear()
}

class SecureSessionStore(private val context: Context) : SessionStore {
    private val ciphertextKey = stringPreferencesKey("refresh_ciphertext_v1")
    private val ivKey = stringPreferencesKey("refresh_iv_v1")
    private val alias = KeyAlias

    override suspend fun readRefreshToken(): String? {
        val (ciphertext, iv) = context.sessionDataStore.data.map { preferences ->
            preferences[ciphertextKey] to preferences[ivKey]
        }.first()
        if (ciphertext == null || iv == null) return null
        return runCatching {
            val cipher = Cipher.getInstance("AES/GCM/NoPadding")
            cipher.init(Cipher.DECRYPT_MODE, key(), GCMParameterSpec(128, android.util.Base64.decode(iv, android.util.Base64.NO_WRAP)))
            cipher.doFinal(android.util.Base64.decode(ciphertext, android.util.Base64.NO_WRAP)).decodeToString()
        }.getOrElse {
            clear()
            null
        }
    }

    override suspend fun writeRefreshToken(value: String) {
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.ENCRYPT_MODE, key())
        val encrypted = cipher.doFinal(value.encodeToByteArray())
        context.sessionDataStore.edit { preferences ->
            preferences[ciphertextKey] = android.util.Base64.encodeToString(encrypted, android.util.Base64.NO_WRAP)
            preferences[ivKey] = android.util.Base64.encodeToString(cipher.iv, android.util.Base64.NO_WRAP)
        }
    }

    override suspend fun clear() {
        context.sessionDataStore.edit { it.clear() }
    }

    private fun key(): SecretKey {
        val store = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        (store.getKey(alias, null) as? SecretKey)?.let { return it }
        return KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore").run {
            init(
                KeyGenParameterSpec.Builder(alias, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
                    .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                    .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                    .setRandomizedEncryptionRequired(true)
                    .build(),
            )
            generateKey()
        }
    }

    companion object {
        internal const val KeyAlias = "pcconnect_refresh_token_v1"
    }
}
