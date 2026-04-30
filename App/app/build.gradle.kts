plugins {
    id("com.android.application")
}
allprojects {


    android {
        namespace = "com.adamkhattab.pcconnect"
        compileSdk = 35

        defaultConfig {
            applicationId = "com.adamkhattab.pcconnect"
            minSdk = 24
            targetSdk = 35
            versionCode = 702
            versionName = "7.2"

            testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
        }

        buildTypes {
            release {
                isMinifyEnabled = false
                proguardFiles(
                        getDefaultProguardFile("proguard-android-optimize.txt"),
                        "proguard-rules.pro"
                )
            }
        }
        compileOptions {
            sourceCompatibility = JavaVersion.VERSION_1_8
            targetCompatibility = JavaVersion.VERSION_1_8
        }
    }

    dependencies {

        implementation("androidx.appcompat:appcompat:1.7.0")
        implementation("com.google.android.material:material:1.12.0")
        implementation("androidx.constraintlayout:constraintlayout:2.2.1")
        implementation("mysql:mysql-connector-java:8.0.27")
        implementation("androidx.preference:preference:1.2.1")
        testImplementation("junit:junit:4.13.2")
        androidTestImplementation("androidx.test.ext:junit:1.2.1")
        androidTestImplementation("androidx.test.espresso:espresso-core:3.6.1")
        implementation("com.squareup.okhttp3:okhttp:4.9.1")
        implementation ("androidx.biometric:biometric:1.1.0")
        implementation("androidx.preference:preference-ktx:1.2.1")
        implementation("androidx.preference:preference:1.2.1")



    }

}