# DJI SDK keep rules
-keep class dji.** { *; }
-keep class com.dji.** { *; }
-dontwarn dji.**
-dontwarn com.dji.**
-keepattributes *Annotation*, InnerClasses, EnclosingMethod


# Helper
-keep class com.cySdkyc.clx.** { *; }
-dontwarn com.cySdkyc.clx.**