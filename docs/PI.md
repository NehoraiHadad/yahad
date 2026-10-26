# הפעלה ביתית על Raspberry Pi

> 0.3.0: [the owner setup runbook](AUTOMATIC-UPDATES-SETUP.md) supersedes the update/service integration below. Install root-owned `deploy/data-lock.py` at `/usr/local/lib/yachad-updater/data-lock.py` before using the new app service. Use the browser bootstrap only for a new empty store; retain existing live data/master-key configuration and Caddy CA.


**סטטוס: תהליך פריסה מוכן, לא בוצע על ה־Pi של המשתמש. אין גישה למכשיר. המשתמש זיהה Raspberry Pi 4 Model B; הזיכרון, מערכת ההפעלה והארכיטקטורה טרם אומתו בפקודות על המכשיר.** אין להריץ שלבי התקנה בלי גישה ואישור. אין להבטיח קיבולת על סמך תוצאות שרת הפיתוח.

## 1. זיהוי לפני התקנה

לאחר העברת המקור למכשיר, מריצים תחילה בלבד:

```bash
bash scripts/inspect-host.sh
```

בודקים את הדגם בפועל, MemTotal, `uname -m`, ‏`getconf LONG_BIT`, ‏`/etc/os-release`, מקום פנוי וגרסאות Node/ffmpeg. ההוראות הבאות מותנות ב־Linux ‏64-bit עם systemd ובחבילת Node 24 רשמית התואמת לארכיטקטורה שנמצאה, למשל aarch64. אם המערכת היא armv7l/32-bit או מערכת לא נתמכת, **עוצרים לפני התקנה** ומכינים מסלול מותאם. החלפת מערכת הפעלה או כתיבה לכרטיס זיכרון אינה חלק מההרשאה הזו.

לאחר הזיהוי ניתן להתקין Node 24 ממקור רשמי בהתאמה ל־CPU/OS, ולאמת את checksum של ההורדה לפי קובץ SHASUMS256 הרשמי. אין כאן `curl | bash` או התקנה אוטומטית משרת פרטי. בודקים שוב `node --version` לפני `npm ci`.

על Debian/Raspberry Pi OS תואם, לאחר אישור ובדיקת מנהל החבילות, נדרשות חבילות `ffmpeg`, ‏`unzip`/`tar`, ‏`ca-certificates` ו־Caddy ממאגר רשמי. Caddy אינו נדרש להפעלת השרת בלולאה מקומית, אך נדרש במסלול ה־TLS הביתי המתואר כאן. אין צורך ב־Docker או בשרת מסד נתונים.

## 2. פריסה והרשאות

הפקודות מיועדות למנהל המכשיר בפועל, לאחר בדיקת הנתיבים ושאין שירות קודם שיידרס:

```bash
sudo useradd --system --home /var/lib/yachad --shell /usr/sbin/nologin yachad
sudo install -d -o root -g yachad -m 0750 /etc/yachad
sudo install -d -o yachad -g yachad -m 0700 /var/lib/yachad
sudo install -d -o root -g yachad -m 0750 /opt/yachad
```

מכינים את המקור עם `npm ci && npm run typecheck && npm test && npm run build` בחשבון רגיל, בספרייה שאינה כוללת נתונים/מפתחות. מעתיקים את העץ המוכן, כולל `node_modules` ו־`dist`, לספריית גרסה ב־`/opt/yachad/releases/`, כאשר `node_modules` הותקן **על אותו OS/ארכיטקטורה**. אין להעתיק node_modules ממחשב x86 ל־ARM. יוצרים קישור `current` לגרסה לאחר בדיקה, ושומרים את הקוד ללא הרשאת כתיבה למשתמש השירות. אין להכניס מפתח לריפו או לתיקיית הגרסה.

מעתיקים `deploy/yachad.env.example` אל `/etc/yachad/yachad.env`, בבעלות root:yachad והרשאת `0640`, ומעדכנים את ה־APP_ORIGIN לכתובת ה־LAN המדויקת שתוגדר ב־Caddy. בשלב הראשון משאירים mock. יוצרים מפתח בספרייה הייעודית **אם אין מפתח קיים**:

```bash
# יצירה ראשונית בלבד: הפקודה מסרבת לדרוס קובץ או symlink קיים.
sudo bash -euc 'test ! -e /etc/yachad/master.key; test ! -L /etc/yachad/master.key; umask 077; openssl rand -base64 32 > /etc/yachad/master.key; chown yachad:yachad /etc/yachad/master.key; chmod 0600 /etc/yachad/master.key'
```

אובדן המפתח ללא גיבוי תקין מונע פענוח הנתונים. אין ליצור מפתח חדש במקום מפתח קיים.

מפעילים setup כמשתמש השירות עם הסביבה של ההתקנה, למשל דרך `sudo -u yachad env ...` עם **נתיבי קבצים בלבד**. אין להעביר סיסמאות בשורת הפקודה. הנתיבים הנדרשים:

כלי setup מדלג על יצירת `.env` בתיקיית קוד מוגנת כשמגדירים `SETUP_WRITE_ENV=false`:

```bash
cd /opt/yachad/current
sudo -u yachad env SETUP_WRITE_ENV=false DATA_DIR=/var/lib/yachad MASTER_KEY_FILE=/etc/yachad/master.key APP_ORIGIN=https://ACTUAL_PRIVATE_LAN_IP COOKIE_SECURE=true node --import tsx scripts/setup.ts
```

בתיקיית קוד מוגנת משאירים `SETUP_WRITE_ENV=false`. בוחרים שני usernames וסיסמאות נפרדות בהזנה מקומית. לא בצ׳אט ולא בקובץ שירות.

מעתיקים את `deploy/yachad.service` אל `/etc/systemd/system/` ומוודאים ש־`/usr/bin/env node` מוצא Node 24 ב־PATH של השירות. לאחר סקירה:

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now yachad
sudo systemctl status yachad
```

הנתונים נשמרים מחוץ לתיקיית הגרסה. נעילת נתונים מונעת שני מופעים או גיבוי בזמן שירות פעיל; אחרי קריסה לוק עשוי להשתחרר רק לאחר כ־10 שניות. systemd ממתין 10 שניות בין ניסיונות הפעלה. לוגי השרת מכילים הודעות תפעול קבועות בלבד; ספריות Signal עלולות להדפיס מפתחות ב־console ולכן הודפסות ה־console הגולמיות מבוטלות בתהליך השרת.

## TLS מקומי וטאבלט

1. בוחרים כתובת IPv4 פרטית יציבה ל־Pi ברשת הביתית. אין לשנות הגדרות ראוטר בלי הרשאה. אפשר להשתמש ב־DHCP reservation אם מנהל הרשת מאשר. אין צורך בשם מתחם ציבורי.
2. יוצרים `/etc/yachad/caddy.env` עם `YACHAD_LAN_IP=...` של הכתובת שנמצאה. אין שם סודות. כתובת זו חייבת להיות זהה ל־APP_ORIGIN, כולל `https://` שם.
3. סוקרים את `deploy/Caddyfile`: הוא משתמש ב־`tls internal`, מכבה redirect אוטומטי מפורט 80 ואת admin API, ומאזין לכתובת הפרטית הזו בלבד. **לא** משתמשים ב־`:443` כללי, בכתובת `0.0.0.0`, בשם מתחם ציבורי או במנהרה. אין גישה דרך IPv6 ציבורי במסלול המתואר.
4. מתקינים את הקובץ כתצורת Caddy ואת `deploy/caddy-environment.conf` כ־drop-in של שירות Caddy. מאמתים באמצעות `caddy validate` עם קובץ הסביבה טעון, לפני restart. אין להדפיס סביבה מלאה לטרמינל.
5. לאחר הפעלת Caddy, התעודה הציבורית של ה־CA נמצאת בדרך כלל ב־`/var/lib/caddy/.local/share/caddy/pki/authorities/local/root.crt` בהתקנת systemd הרשמית. שומרים את כל תיקיית הנתונים של Caddy באופן קבוע כדי שחידוש גרסה לא יחליף CA.
6. מעבירים **רק root.crt הציבורי** לטאבלט בשיטה מקומית מהימנה, ומשווים fingerprint מול המכשיר המנהל. ב־Android מתקינים כתעודת CA למשתמש/לדפדפן בהתאם לגרסת המכשיר. ב־iPad מתקינים את פרופיל התעודה ואז מפעילים אמון מלא ב־Certificate Trust Settings. הכיתובים המדויקים משתנים בין גרסאות; אין לאשר תעודה שלא אומתה.
7. פותחים `https://ACTUAL_PRIVATE_LAN_IP`. אסור להמשיך מעבר לאזהרת TLS כפתרון קבוע. הדפדפן חייב להראות חיבור מאומת, ואז מאשרים הרשאת מיקרופון לאתר לפי הצורך.
8. בודקים הקלטה אמיתית של כמה שניות, עצירה, האזנה מקדימה, שליחה והאזנה לאחר רענון. בודקים גם סירוב הרשאה וביטולה, רקע/שינה ונעילת מכשיר. בדיקה זו לא בוצעה כאן.

מקורות: [Caddy local HTTPS](https://caddyserver.com/docs/automatic-https#local-https), [Caddy תחת systemd](https://caddyserver.com/docs/running#local-https-with-systemd), [getUserMedia secure context](https://developer.mozilla.org/en-US/docs/Web/API/MediaDevices/getUserMedia).

CA פרטי מאפשר הנפקת תעודות שהמכשיר סומך עליהן: מפתחות ה־CA נשארים ב־Pi מוגנים, ואין לחשוף אותם לאפליקציה או לטאבלט. גיבוי האפליקציה אינו מגבה את Caddy. אחרי אובדן ה־CA ניתן ליצור CA חדש ולהתקין מחדש אמון במכשירים, או לגבותו בנפרד בגיבוי מערכתי מוצפן עם בקרת גישה. אין להעתיק את מפתחות ה־CA ל־ZIP המקור.

## גיבוי, עדכון ושחזור

- מבצעים גיבוי מוצפן לאחר עצירת השירות: [BACKUP.md](BACKUP.md). `scripts/backup-daily.sh` הוא כלי ידני למפעיל שמבקש סיסמת גיבוי מקומית; הוא מפעיל את השירות מחדש גם במקרה של כשל. **אין סיסמת גיבוי קבועה בקוד ואין הבטחה לגיבוי אוטומטי ללא פתרון מפתח מקומי שנבחר.**
- מתקינים את timer מחיקת הגיבויים כדי להבטיח תפוגה אחרי 7 ימים גם אם לא נוצר גיבוי חדש. עותקים מחוץ למכשיר צריכים אותה מדיניות.
- לעדכון: עוצרים yachad, יוצרים גיבוי מוצפן, בונים ספריית גרסה חדשה בתנאי מערכת זהים, מריצים בדיקות, מחליפים `current`, ומפעילים. לא מחליפים DATA_DIR, MASTER_KEY_FILE או את נתוני Caddy. שינויים עתידיים בסכמה דורשים migration מתועד וגיבוי לפניו; אין downgrade אוטומטי של DB.
- לאחר שחזור מבטלים כל סשני הדפדפן. בודקים הרשאות, סודות ו־connector לפני גישה מהטאבלט. לא מנסים להפעיל אותו auth בשני מכשירי שרת במקביל.

## אימות פיזי שעדיין נדרש

| בדיקה | איך מאמתים |
| --- | --- |
| boot אמיתי | לאחר reboot מאושר, yachad ו־Caddy פעילים והילדה יכולה להיכנס |
| crash recovery | לאחר בדיקת בטיחות מקומית, ממיתים את תהליך הבדיקה ורואים restart, בלי אובדן נתונים |
| רשת ביתית בלבד | `ss -ltnp`, כתובת bind של Caddy, firewall קיים וללא port forwarding/UPnP |
| RAM/CPU/דיסק | מדידה בזמן קליטה, גלילה, שליחת סרטון והמרת קול, על החומרה המזוהה |
| TLS ומיקרופון | אין אזהרה בטאבלט; הקלטה, שליחה וניגון אמיתיים |
| WhatsApp | מספר ניסוי מאושר; QR, ניתוק רשת, reboot, ביטול קישור והיסטוריה לאחר ניתוק |
| גיבוי | יצירה, שחזור לספרייה נפרדת, אימות מדיה ואישורים, בדיקת תפוגה |

אין כאן המלצה לרכישת חומרה או הבטחת מספר משפחות/קבוצות/הודעות לשנייה. הפריסה קלה בכוונה, אך Baileys, סנכרון היסטוריה ו־ffmpeg עשויים לשנות משמעותית את צריכת המשאבים.

## אבחון ועדכונים בגרסה 0.2.0

פאנל **מצב המערכת** להורה אוסף סיכומים תפעוליים מוצפנים בלי תוכן שיחות: [OPERATIONS.md](OPERATIONS.md). אין benchmark של הכרטיס ברקע ואין הנחת מפרט.

מנגנון עדכונים חתומים מ־GitHub Releases נתמך אחרי הגדרה חד־פעמית: [UPDATES.md](UPDATES.md). בשלב הראשון הוא כבוי; רק חשבון הורה מסוים שנבחר גם לפי משפחה וגם לפי משתמש יכול לבקש התקנה. המשתמש סיפק את ריפו המקור `NehoraiHadad/yahad`; שירות העדכון ו־Release חתום טרם הוגדרו. להתקנה ראשונה בוחרים תיקיית גרסה `releases/v0.2.0` כדי לאפשר החלפה מסודרת בהמשך.
