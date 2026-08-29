plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
    id("org.jetbrains.kotlin.plugin.compose")
    id("org.jetbrains.kotlin.plugin.serialization")
    id("com.google.devtools.ksp")
    id("androidx.room")
}

room { schemaDirectory("$projectDir/schemas") }

val releaseSigningEnvironment = mapOf(
    "storeFile" to providers.environmentVariable("PCCONNECT_ANDROID_KEYSTORE"),
    "storePassword" to providers.environmentVariable("PCCONNECT_ANDROID_STORE_PASSWORD"),
    "keyAlias" to providers.environmentVariable("PCCONNECT_ANDROID_KEY_ALIAS"),
    "keyPassword" to providers.environmentVariable("PCCONNECT_ANDROID_KEY_PASSWORD")
)
val hasReleaseSigning = releaseSigningEnvironment.values.all { it.isPresent }
val apiBaseUrl = providers.environmentVariable("PCCONNECT_ANDROID_API_BASE_URL")
    .orElse("https://api.pcconnect.adamdeveloping.co.uk/api/v2/")
    .get()
check(apiBaseUrl.startsWith("https://") && apiBaseUrl.endsWith("/api/v2/")) {
    "PCCONNECT_ANDROID_API_BASE_URL must be an HTTPS URL ending in /api/v2/."
}
val debugApiBaseUrl = providers.environmentVariable("PCCONNECT_ANDROID_DEBUG_API_BASE_URL")
    .orElse(apiBaseUrl)
    .get()
val debugUsesHttps = debugApiBaseUrl.startsWith("https://")
val debugUsesLoopbackHttp = Regex("^http://(?:localhost|127\\.0\\.0\\.1)(?::[0-9]{1,5})?/api/v2/$")
    .matches(debugApiBaseUrl)
check((debugUsesHttps && debugApiBaseUrl.endsWith("/api/v2/")) || debugUsesLoopbackHttp) {
    "PCCONNECT_ANDROID_DEBUG_API_BASE_URL must use HTTPS or loopback HTTP and end in /api/v2/."
}
val rpHost = providers.environmentVariable("PCCONNECT_ANDROID_RP_HOST")
    .orElse("pcconnect.adamdeveloping.co.uk")
    .get()
check(Regex("^[A-Za-z0-9](?:[A-Za-z0-9.-]*[A-Za-z0-9])?$").matches(rpHost)) {
    "PCCONNECT_ANDROID_RP_HOST must be a DNS hostname."
}

android {
    namespace = "com.adamkhattab.pcconnect.v2"
    compileSdk = 36

    defaultConfig {
        applicationId = "com.adamkhattab.pcconnect"
        minSdk = 24
        targetSdk = 36
        versionCode = 800
        versionName = "8.0.0"
        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
        buildConfigField("String", "API_BASE_URL", "\"${apiBaseUrl.replace("\\", "\\\\").replace("\"", "\\\"")}\"")
        buildConfigField("String", "RP_HOST", "\"${rpHost.replace("\"", "\\\"")}\"")
        manifestPlaceholders["pcconnectRpHost"] = rpHost
    }

    buildFeatures { compose = true; buildConfig = true }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
        isCoreLibraryDesugaringEnabled = true
    }
    kotlinOptions { jvmTarget = "17" }
    packaging { resources.excludes += "/META-INF/{AL2.0,LGPL2.1}" }
    lint {
        warningsAsErrors = true
        // API 37-era AndroidX releases require AGP 9.1; this project deliberately
        // remains on the architecture-approved API 36 / AGP 8.10 toolchain.
        disable += "GradleDependency"
    }

    signingConfigs {
        if (hasReleaseSigning) {
            create("release") {
                storeFile = file(releaseSigningEnvironment.getValue("storeFile").get())
                storePassword = releaseSigningEnvironment.getValue("storePassword").get()
                keyAlias = releaseSigningEnvironment.getValue("keyAlias").get()
                keyPassword = releaseSigningEnvironment.getValue("keyPassword").get()
                enableV1Signing = false
                enableV2Signing = true
                enableV3Signing = true
                enableV4Signing = true
            }
        }
    }

    buildTypes {
        debug {
            applicationIdSuffix = ".debug"
            buildConfigField("String", "API_BASE_URL", "\"${debugApiBaseUrl.replace("\\", "\\\\").replace("\"", "\\\"")}\"")
        }
        release {
            isMinifyEnabled = true
            isShrinkResources = true
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
            if (hasReleaseSigning) signingConfig = signingConfigs.getByName("release")
        }
    }
}

tasks.configureEach {
    if (name.contains("Release", ignoreCase = true)) {
        doFirst {
            check(hasReleaseSigning) {
                "Release builds require PCCONNECT_ANDROID_KEYSTORE, PCCONNECT_ANDROID_STORE_PASSWORD, " +
                    "PCCONNECT_ANDROID_KEY_ALIAS and PCCONNECT_ANDROID_KEY_PASSWORD."
            }
        }
    }
}

dependencies {
    val composeBom = platform("androidx.compose:compose-bom:2025.04.01")
    implementation(composeBom)
    androidTestImplementation(composeBom)
    implementation("androidx.core:core-ktx:1.16.0")
    implementation("androidx.activity:activity-compose:1.13.0")
    implementation("androidx.compose.material3:material3")
    implementation("androidx.compose.ui:ui")
    implementation("androidx.compose.ui:ui-tooling-preview")
    debugImplementation("androidx.compose.ui:ui-tooling")
    implementation("androidx.lifecycle:lifecycle-runtime-compose:2.9.0")
    implementation("androidx.lifecycle:lifecycle-viewmodel-compose:2.9.0")
    implementation("androidx.navigation:navigation-compose:2.9.0")
    implementation("androidx.room:room-runtime:2.7.1")
    implementation("androidx.room:room-ktx:2.7.1")
    ksp("androidx.room:room-compiler:2.7.1")
    implementation("androidx.datastore:datastore-preferences:1.2.1")
    implementation("androidx.work:work-runtime-ktx:2.11.2")
    implementation("androidx.credentials:credentials:1.6.0")
    implementation("androidx.credentials:credentials-play-services-auth:1.6.0")
    implementation("com.google.android.libraries.identity.googleid:googleid:1.2.0")
    implementation("com.squareup.retrofit2:retrofit:2.11.0")
    implementation("com.jakewharton.retrofit:retrofit2-kotlinx-serialization-converter:1.0.0")
    implementation("com.squareup.okhttp3:okhttp:4.12.0")
    implementation("org.jetbrains.kotlinx:kotlinx-serialization-json:1.8.1")
    implementation("com.microsoft.signalr:signalr:10.0.0")
    implementation("io.reactivex.rxjava3:rxjava:3.1.10")
    coreLibraryDesugaring("com.android.tools:desugar_jdk_libs:2.1.5")
    testImplementation("junit:junit:4.13.2")
    testImplementation("org.jetbrains.kotlinx:kotlinx-coroutines-test:1.10.2")
    androidTestImplementation("androidx.test.ext:junit:1.3.0")
    androidTestImplementation("androidx.test.espresso:espresso-core:3.7.0")
    androidTestImplementation("androidx.compose.ui:ui-test-junit4")
}
