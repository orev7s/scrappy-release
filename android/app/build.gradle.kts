plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

android {
    namespace = "com.scrappy.app"
    compileSdk = 35

    defaultConfig {
        applicationId = "com.scrappy.app"
        minSdk = 30          // MediaProjection with a foreground service type, and scrcpy 5.x behaviour
        targetSdk = 35
        versionCode = 11
        versionName = "0.2.1"
    }

    buildTypes {
        release {
            isMinifyEnabled = false
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    kotlinOptions {
        jvmTarget = "17"
    }
}

dependencies {
    implementation("androidx.core:core-ktx:1.13.1")
}
