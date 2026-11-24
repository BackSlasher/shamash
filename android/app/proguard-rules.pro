# Shamash R8 rules.
-keepclassmembers @kotlinx.serialization.Serializable class net.backslasher.shamash.** {
    *** Companion;
    *** INSTANCE;
    kotlinx.serialization.KSerializer serializer(...);
}
-keep,includedescriptorclasses class net.backslasher.shamash.**$$serializer { *; }

# OkHttp optional platform classes.
-dontwarn okhttp3.internal.platform.**
-dontwarn org.conscrypt.**
-dontwarn org.bouncycastle.**
-dontwarn org.openjsse.**

-keepattributes SourceFile,LineNumberTable
-renamesourcefileattribute SourceFile
