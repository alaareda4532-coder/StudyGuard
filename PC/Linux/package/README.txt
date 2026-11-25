StudyGuard for Linux x64

هذه نسخة تجريبية لـ Ubuntu/Debian x64 مع systemd. تحتوي على واجهة عربية داكنة
ووكيل نظام لتطبيق حجب التطبيقات والمواقع. تحتاج تثبيت وكيل النظام مرة واحدة.

التثبيت:
1. فك StudyGuard-Linux-x64.tar.gz كاملًا.
2. افتح مجلد StudyGuard-Linux-x64 في الطرفية.
3. نفّذ: sudo bash ./install.sh
4. افتح StudyGuard من قائمة التطبيقات.

لإزالة البرنامج شغّل: sudo bash ./uninstall.sh

إذا ظهرت مشكلة رسومية على Ubuntu/Debian:
sudo apt install libfontconfig1 libice6 libsm6 libx11-6

التطبيقات: اكتب اسم ملف العملية في Linux (مثل firefox أو telegram-desktop).
يرسل الوكيل SIGTERM أولًا ثم SIGKILL بعد 4 ثوانٍ إذا بقيت العملية مفتوحة.
لا يوقف عمليات سطح المكتب الأساسية.

المواقع: أضف اسم النطاق مثل youtube.com. الوكيل يحدّث /etc/hosts ويشمل
النطاق وwww وm وmobile. قد تتجاوز بعض البرامج ذلك باستخدام DNS مشفر أو نطاق فرعي
غير مدرج؛ لذلك لا يعد بديلًا عن جدار حماية مضبوط على جهازك.

الإعدادات تُحفظ محليًا في ~/.config/studyguard/config.json. لم تُرفع بيانات
الإعدادات إلى أي خدمة. حذف العنصر من الحجب الدائم لا يتاح إلا بعد مرور 15 يومًا.

المطور: علاء رضا أحمد
