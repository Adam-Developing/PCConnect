plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.android)
    alias(libs.plugins.kotlin.compose)
    alias(libs.plugins.kotlin.serialization)
}

android {
    namespace = "uk.co.adamkhattab.pcconnect"
    compileSdk = 36

    defaultConfig {
        applicationId = "uk.co.adamkhattab.pcconnect"
        minSdk = 26
        targetSdk = 36

        // The Java app this replaces is on versionCode 7xx; v2 starts a new
        // series so the two are never confused in the Play console.
        versionCode = 8_00_00
        versionName = "8.0.0"

        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"

        // The backend is a build-time default that is overridable at runtime
        // (06 §1). It is never a hardcoded absolute constant in six activities,
        // which is what S3-08 was — including a developer's LAN address that
        // shipped in a release build.
        buildConfigField("String", "DEFAULT_API_BASE_URL", "\"https://api.pcconnect.example\"")
    }

    buildTypes {
        debug {
            // The emulator reaches the host machine at 10.0.2.2.
            buildConfigField("String", "DEFAULT_API_BASE_URL", "\"http://10.0.2.2:5080\"")
        }

        release {
            isMinifyEnabled = true
            isShrinkResources = true
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")

            // Signing is configured from the environment in CI; no keystore and
            // no password ever lives in this file (03 §7, S1-15).
            signingConfig = signingConfigs.findByName("release")
        }
    }

    signingConfigs {
        val keystorePath = System.getenv("PCCONNECT_ANDROID_KEYSTORE")
        if (!keystorePath.isNullOrBlank()) {
            create("release") {
                storeFile = file(keystorePath)
                storePassword = System.getenv("PCCONNECT_ANDROID_KEYSTORE_PASSWORD")
                keyAlias = System.getenv("PCCONNECT_ANDROID_KEY_ALIAS")
                keyPassword = System.getenv("PCCONNECT_ANDROID_KEY_PASSWORD")
            }
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    kotlinOptions {
        jvmTarget = "17"
    }

    buildFeatures {
        compose = true
        buildConfig = true
    }

    packaging {
        resources.excludes += setOf("/META-INF/{AL2.0,LGPL2.1}", "META-INF/DEPENDENCIES")
    }

    sourceSets {
        getByName("main").java.srcDirs("src/main/kotlin")
        getByName("test").java.srcDirs("src/test/kotlin")
    }
}

dependencies {
    implementation(libs.androidx.core.ktx)
    implementation(libs.androidx.lifecycle.runtime.ktx)
    implementation(libs.androidx.lifecycle.viewmodel.compose)
    implementation(libs.androidx.lifecycle.runtime.compose)
    implementation(libs.androidx.activity.compose)

    implementation(platform(libs.androidx.compose.bom))
    implementation(libs.androidx.compose.ui)
    implementation(libs.androidx.compose.ui.graphics)
    implementation(libs.androidx.compose.ui.tooling.preview)
    implementation(libs.androidx.compose.material3)
    implementation(libs.androidx.compose.material.icons.core)
    implementation(libs.androidx.navigation.compose)
    implementation(libs.androidx.biometric)
    implementation(libs.androidx.datastore.preferences)

    implementation(libs.okhttp)
    implementation(libs.kotlinx.serialization.json)
    implementation(libs.kotlinx.coroutines.android)

    // The realtime channel. RxJava comes with the SignalR client rather than
    // being a choice of this app's.
    implementation(libs.signalr)
    implementation(libs.gson)
    implementation(libs.rxjava)

    debugImplementation(libs.androidx.compose.ui.tooling)

    testImplementation(libs.junit)
    testImplementation(libs.kotlinx.coroutines.test)

    androidTestImplementation(libs.androidx.junit)
    androidTestImplementation(libs.androidx.espresso.core)
    androidTestImplementation(platform(libs.androidx.compose.bom))
    androidTestImplementation(libs.androidx.compose.ui.test.junit4)
}
