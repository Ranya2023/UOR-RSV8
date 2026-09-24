plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

// The CI workflow decodes keystore.b64 into this file before building, so
// every build is signed with the SAME key and new versions install over
// old ones without uninstalling. Local builds without it fall back to debug.
val releaseKeystore = rootProject.file("nextslide.jks")

android {
    namespace = "edu.uor.remco"
    compileSdk = 34

    defaultConfig {
        applicationId = "edu.uor.remco"
        minSdk = 26
        targetSdk = 34
        versionCode = (System.getenv("GITHUB_RUN_NUMBER") ?: "1").toInt()
        versionName = "1.0." + (System.getenv("GITHUB_RUN_NUMBER") ?: "0")
    }

    signingConfigs {
        if (releaseKeystore.exists()) {
            create("uor") {
                storeFile = releaseKeystore
                storePassword = "nextslide-uor"
                keyAlias = "nextslide"
                keyPassword = "nextslide-uor"
            }
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            signingConfig = if (releaseKeystore.exists())
                signingConfigs.getByName("uor") else signingConfigs.getByName("debug")
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions {
        jvmTarget = "17"
    }
    lint {
        checkReleaseBuilds = false
        abortOnError = false
    }
}

dependencies {
}
