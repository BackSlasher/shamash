import org.jetbrains.kotlin.gradle.dsl.JvmTarget
import java.util.Properties

plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.compose)
    alias(libs.plugins.kotlin.serialization)
}

/** Environment variable first (CI), then Gradle property (-P / gradle.properties). */
fun config(name: String): String? =
    providers.environmentVariable(name).orNull?.takeIf { it.isNotBlank() }
        ?: providers.gradleProperty(name).orNull?.takeIf { it.isNotBlank() }

val shamashVersionName: String = config("SHAMASH_VERSION_NAME") ?: "0.1.0"
val shamashVersionCode: Int = config("SHAMASH_VERSION_CODE")?.toInt() ?: 1

// ElevenLabs key: environment ELEVENLABS_KEY first, then elevenlabs.key in android/local.properties.
val elevenLabsKey: String = run {
    val fromEnv = providers.environmentVariable("ELEVENLABS_KEY").orNull?.takeIf { it.isNotBlank() }
    val localProps = rootProject.file("local.properties")
    val fromFile = if (localProps.exists()) {
        Properties().also { p -> localProps.inputStream().use { p.load(it) } }
            .getProperty("elevenlabs.key")?.takeIf { it.isNotBlank() }
    } else null
    fromEnv ?: fromFile ?: throw GradleException(
        "ElevenLabs API key not configured. Provide it either as the ELEVENLABS_KEY " +
            "environment variable (build.sh reads it from <repo>/.env) or as " +
            "elevenlabs.key=... in android/local.properties.",
    )
}

// Release signing contract: all four env vars, keystore path in SHAMASH_KEYSTORE_FILE.
// Without SHAMASH_KEYSTORE_FILE the release APK is left unsigned (never debug-signed).
val releaseKeystore: String? = providers.environmentVariable("SHAMASH_KEYSTORE_FILE").orNull?.takeIf { it.isNotBlank() }

android {
    namespace = "net.backslasher.shamash"
    compileSdk = 37

    defaultConfig {
        applicationId = "net.backslasher.shamash"
        minSdk = 33
        targetSdk = 37
        versionCode = shamashVersionCode
        versionName = shamashVersionName
        buildConfigField("String", "ELEVENLABS_KEY", "\"$elevenLabsKey\"")
    }

    signingConfigs {
        if (releaseKeystore != null) {
            create("release") {
                storeFile = file(releaseKeystore)
                storePassword = providers.environmentVariable("SHAMASH_KEYSTORE_PASSWORD").orNull
                keyAlias = providers.environmentVariable("SHAMASH_KEY_ALIAS").orNull
                keyPassword = providers.environmentVariable("SHAMASH_KEY_PASSWORD").orNull
            }
        }
    }

    buildTypes {
        debug {
            // testOnly lets device owner be removed over adb; install with `adb install -t`.
            manifestPlaceholders["testOnly"] = "true"
        }
        release {
            manifestPlaceholders["testOnly"] = "false"
            isMinifyEnabled = true
            isShrinkResources = true
            proguardFiles(
                getDefaultProguardFile("proguard-android-optimize.txt"),
                "proguard-rules.pro",
            )
            signingConfig = if (releaseKeystore != null) signingConfigs.getByName("release") else null
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    buildFeatures {
        compose = true
        buildConfig = true
    }

    testOptions {
        unitTests {
            isIncludeAndroidResources = true
        }
    }

    packaging {
        resources {
            excludes += "/META-INF/{AL2.0,LGPL2.1}"
        }
    }
}

kotlin {
    compilerOptions {
        jvmTarget.set(JvmTarget.JVM_17)
    }
}

dependencies {
    implementation(libs.androidx.core.ktx)
    implementation(libs.androidx.activity.compose)
    implementation(libs.androidx.lifecycle.runtime.compose)
    implementation(libs.androidx.lifecycle.viewmodel.compose)

    implementation(platform(libs.compose.bom))
    implementation(libs.compose.ui)
    implementation(libs.compose.foundation)
    implementation(libs.compose.ui.tooling.preview)
    debugImplementation(libs.compose.ui.tooling)

    implementation(libs.kotlinx.serialization.json)
    implementation(libs.kotlinx.coroutines.android)
    implementation(libs.okhttp)

    testImplementation(libs.junit)
    testImplementation(libs.robolectric)
    testImplementation(libs.androidx.test.core)
    testImplementation(libs.androidx.test.ext.junit)
    testImplementation(libs.kotlinx.coroutines.test)
}
