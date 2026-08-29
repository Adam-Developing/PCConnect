package com.adamkhattab.pcconnect.v2.data

import com.adamkhattab.pcconnect.v2.BuildConfig
import java.util.concurrent.TimeUnit
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.runBlocking
import kotlinx.serialization.json.Json
import okhttp3.Authenticator
import okhttp3.Interceptor
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import okhttp3.Route
import retrofit2.Retrofit
import com.jakewharton.retrofit2.converter.kotlinx.serialization.asConverterFactory
import okhttp3.MediaType.Companion.toMediaType

internal val PCConnectJson = Json {
    ignoreUnknownKeys = true
    explicitNulls = false
    encodeDefaults = true
}

class AccessTokenInterceptor(private val tokens: TokenManager) : Interceptor {
    override fun intercept(chain: Interceptor.Chain): Response {
        val request = tokens.currentAccessToken()?.let { token ->
            chain.request().newBuilder().header("Authorization", "Bearer $token").build()
        } ?: chain.request()
        return chain.proceed(request)
    }
}

class RefreshAuthenticator(private val tokens: TokenManager) : Authenticator {
    override fun authenticate(route: Route?, response: Response): Request? {
        if (response.priorResponse != null) return null
        val token = runBlocking(Dispatchers.IO) { tokens.refresh(force = true) } ?: return null
        if (response.request.header("Authorization") == "Bearer $token") return null
        return response.request.newBuilder().header("Authorization", "Bearer $token").build()
    }
}

class ApiClient(tokens: TokenManager) {
    val json = PCConnectJson
    private val mediaType = "application/json".toMediaType()
    private val base = Retrofit.Builder()
        .baseUrl(BuildConfig.API_BASE_URL)
        .addConverterFactory(json.asConverterFactory(mediaType))

    val anonymous: PCConnectApi = base.client(baseHttpClient()).build().create(PCConnectApi::class.java)
    val authenticated: PCConnectApi = base.client(
        baseHttpClient().newBuilder()
            .addInterceptor(AccessTokenInterceptor(tokens))
            .authenticator(RefreshAuthenticator(tokens))
            .build(),
    ).build().create(PCConnectApi::class.java)

    private fun baseHttpClient() = OkHttpClient.Builder()
        .connectTimeout(15, TimeUnit.SECONDS)
        .readTimeout(30, TimeUnit.SECONDS)
        .callTimeout(45, TimeUnit.SECONDS)
        .retryOnConnectionFailure(true)
        .build()
}
