package com.adamkhattab.pcconnect.v2.data

import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import java.nio.charset.StandardCharsets
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

/** Encrypts sensitive Room read-model fields with a non-exportable Android Keystore key. */
class LocalPiiCipher {
    private val alias = "pcconnect-v2-local-pii"
    private val keyStore = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }

    fun encryptReminder(reminderId: String, plaintext: String): String {
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.ENCRYPT_MODE, key())
        check(cipher.iv.size == NONCE_BYTES) { "The Android Keystore returned an unsupported GCM nonce size." }
        cipher.updateAAD(aad(reminderId))
        val ciphertext = cipher.doFinal(plaintext.toByteArray(StandardCharsets.UTF_8))
        val payload = ByteArray(cipher.iv.size + ciphertext.size)
        cipher.iv.copyInto(payload)
        ciphertext.copyInto(payload, cipher.iv.size)
        return "v1:" + Base64.encodeToString(payload, Base64.NO_WRAP)
    }

    fun decryptReminder(reminderId: String, encoded: String): String {
        require(encoded.startsWith("v1:")) { "Cached reminder text is not encrypted." }
        val payload = Base64.decode(encoded.removePrefix("v1:"), Base64.NO_WRAP)
        require(payload.size > NONCE_BYTES + TAG_BYTES) { "Cached reminder ciphertext is truncated." }
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.DECRYPT_MODE, key(), GCMParameterSpec(TAG_BITS, payload, 0, NONCE_BYTES))
        cipher.updateAAD(aad(reminderId))
        return String(cipher.doFinal(payload, NONCE_BYTES, payload.size - NONCE_BYTES), StandardCharsets.UTF_8)
    }

    private fun key(): SecretKey = (keyStore.getKey(alias, null) as? SecretKey) ?: KeyGenerator
        .getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore")
        .apply {
            init(
                KeyGenParameterSpec.Builder(alias, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
                    .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                    .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                    .setRandomizedEncryptionRequired(true)
                    .build(),
            )
        }
        .generateKey()

    private fun aad(reminderId: String) = "pcconnect.android.reminder.v1|$reminderId".toByteArray(StandardCharsets.UTF_8)

    private companion object {
        const val NONCE_BYTES = 12
        const val TAG_BYTES = 16
        const val TAG_BITS = TAG_BYTES * 8
    }
}
