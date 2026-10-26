# עדכוני Releases מהבית

המנגנון מוכן בקוד, אך **לא פורסם Release חתום ולא הופעל על ה־Pi**. זה חלק מההתקנה הראשונית שנבצע אחרי זיהוי המכשיר. אין צורך לפתוח פורט נכנס: רק HTTPS יוצא ל־GitHub ול־npm. בהתקנת עדכון יש הורדת dependencies לפי lockfile; אין הבטחת עדכון ללא רשת או תאימות מלאה של ספק WhatsApp.

## התנהגות

- ברירת מחדל `enabled:false`. אחרי הגדרה, `mode:notify` בודק Release יציב אחרון כל 6 שעות ומאפשר להורה המנהל ללחוץ **בדיקה ב־GitHub** או **התקנת העדכון**. השירות אוסף בקשות בתוך כחמש דקות. `mode:auto` מתקין גם ללא לחיצה כשהבדיקה מוצאת גרסה חדשה. החלפת מצב היא הגדרת מנהל Pi בקובץ root.
- רק ההורה שנבחר בתצורה כ־`familyId` וגם `userId` יכול לבקש עדכון. הורה ממשפחה אחרת והילדה אינם יכולים. הבקשה אינה מקבלת URL, ריפו, גרסה, פקודת shell או נתיב מהדפדפן.
- המקור הוא `GET /repos/OWNER/REPO/releases/latest`, בלי drafts/prereleases. תגית `vX.Y.Z`, והגרסה חייבת לעלות; אין downgrade אוטומטי.
- שלושה assets נדרשים: `yachad-app.zip`, ‏`yachad-release.json`, ‏`yachad-release.sig`. חתימת Ed25519 על בתיו המדויקים של ה־manifest נבדקת מול מפתח ציבורי שהותקן מראש, ואז SHA256 וגודל הארכיון נבדקים. checksum לבדו אינו אימות מקור.
- ZIP מוגבל ל־64MiB דחוס, 128MiB מחולץ ו־2,000 entries. דחיית path traversal, נתיבים מוחלטים, symlinks בארכיון וקבצי מערכת/סודות. assets יורדים לכתובות GitHub HTTPS מותרות בלבד; Authorization נשלח רק ל־api.github.com ומוסר בהפניה. token נשאר בקובץ root 0600, לא בדפדפן או בלוג.
- הכנה נעשית כשהגרסה הישנה עוד פועלת: חילוץ, `npm ci --omit=dev --ignore-scripts` ובדיקת smoke עם נתונים זמניים. קוד החבילה ו־npm רצים כמשתמש build נפרד שאינו קורא את נתוני המשפחה. כל תלות עדיין חלק מסיכון שרשרת האספקה; חתימת release אינה ביקורת קוד לתלויות.
- לאחר מכן: עצירת השירות, גיבוי מוצפן מוצלח, החלפת current אטומית, הפעלה ובדיקת `/health/ready` ב־loopback מול מספר הגרסה. Caddy חוסם `/health/*` מגישת הטאבלט. קישור WhatsApp אינו תנאי readiness, כי ניתוק חיצוני אינו בהכרח כשל גרסה.
- בכשל אחרי העצירה: ניסיון להחזיר את קישור הקוד הקודם ולהפעיל אותו. גם `ExecStopPost` ומצב תחזוקה מתמשך מאפשרים ניסיון התאוששות אחרי timeout/קריסת worker. אם השחזור נכשל, מוצג כשל ודורשת התערבות מקומית. **לא מובטח recovery מכל תקלה/הפסקת חשמל.**
- אין migration אוטומטי: מותר כרגע רק databaseSchema=1. publisher אחראי לשחרר רק שינויים תואמי נתונים; שינוי סמנטי שאינו משתקף במספר סכמה אינו ניתן לזיהוי כללי. rollback מחליף קוד ואינו משחזר אוטומטית DB ישן או מוחק הודעות חדשות. שינוי סכמה דורש פריסה מתוכננת. גרסה שנכשלה אינה מותקנת שוב אוטומטית שוב ושוב; לחיצת התקנה מפורשת מאפשרת ניסיון נוסף.
- אחרי הצלחה נשמרות גרסת הקוד הפעילה והקודמת. גיבויי טרום־עדכון: AES-GCM, עד 7 עותקים ו־7 ימים; מצרפים גם timer מחיקה יומי. זמן ההשבתה כולל גיבוי של כל הנתונים ולכן אינו בהכרח קצר בכרטיס איטי.

## התקנה חד־פעמית על Pi מזוהה

הנחות: ההתקנה לפי PI.md כבר בוצעה, Node24/ffmpeg מותקנים, user `yachad` קיים, הקוד הראשוני ב־`/opt/yachad/releases/v0.2.0` ו־`current` מצביע אליו. אין להריץ לפני זיהוי RAM/OS/CPU בפועל. הנתיבים הבאים הם ברירת מחדל ניתנת לשינוי בקובץ וביחידות systemd יחד.

```bash
sudo useradd --system --home /var/lib/yachad-update-build --shell /usr/sbin/nologin yachad-update
sudo install -d -o root -g yachad -m 0750 /var/lib/yachad-updates
sudo install -d -o yachad-update -g yachad-update -m 0700 /var/lib/yachad-update-build
sudo install -d -o root -g root -m 0755 /usr/local/lib/yachad-updater
sudo install -d -o yachad -g yachad -m 0700 /var/backups/yachad-updates
sudo install -o root -g root -m 0644 deploy/updater.py /usr/local/lib/yachad-updater/updater.py
sudo install -o root -g root -m 0600 deploy/updates.json.example /etc/yachad/updates.json
```

`/opt/yachad/releases` צריך להיות root-owned, ולמשתמש yachad הרשאת קריאה בקוד בלבד. משתמש build אינו חבר בקבוצת yachad. `updater.py` מותקן מחוץ לספריית הגרסה ואינו מוחלף אוטומטית בידי release; שינויים במנגנון המיוחס דורשים עדכון מנהל מקומי.

יוצרים סיסמת גיבוי אקראית **מקומית בלבד**, בלי להעביר לצ׳אט, ולא דורסים קובץ קיים:

```bash
sudo bash -euc 'test ! -e /etc/yachad/update-backup-passphrase; test ! -L /etc/yachad/update-backup-passphrase; umask 077; openssl rand -base64 48 > /etc/yachad/update-backup-passphrase'
```

שומרים עותק של הסיסמה במנהל סיסמאות/גיבוי מנהל מוגן, מחוץ ל־Pi. בלעדיה לא ניתן לשחזר גיבויי העדכון אם ה־Pi אבד. מפתח הנתונים כלול בתוך הגיבוי המוצפן, אך סיסמת הגיבוי אינה כלולה בו. שחזור ב־`npm run backup -- restore ARCHIVE NEW_DIRECTORY`, בהזנה מקומית של אותה סיסמה; ראו BACKUP.md. סיסמה אקראית זו אינה סיסמת ההורה.

כדי לזהות את ההורה המורשה, מציגים רק מזהים מנהליים מקומית:

```bash
sudo -u yachad python3 - <<'PY'
import sqlite3
db=sqlite3.connect('file:/var/lib/yachad/app.sqlite?mode=ro',uri=True)
for row in db.execute("SELECT family_id,id,username FROM users WHERE role='parent'"):
 print(row)
PY
```

בקובץ `/etc/yachad/updates.json` מעדכנים repository, שני מזהי operator, ‏mode ונתיבי node/npm לפי `command -v node` ו־`command -v npm`. מגדירים `enabled:true` רק אחרי התקנת המפתח הציבורי. הריפו `NehoraiHadad/yahad` ציבורי ולכן אפשר להשאיר tokenFile=null. ריפו פרטי: יוצרים באופן מקומי fine-grained token עם Contents:read לריפו הזה בלבד, שומרים root600 בנתיב tokenFile; לא מעבירים אותו בשורת פקודה, בצ׳אט, בריפו או ב־env של האפליקציה. ריפו ציבורי לא מחייב token.

מתקינים את **המפתח הציבורי בלבד** שהופק בשלב הפרסום, root-owned ולא כתיב למשתמש שירות:

```bash
sudo install -o root -g root -m 0644 release-signing.pem.pub /etc/yachad/release-signing.pub
sudo install -o root -g root -m 0644 deploy/yachad-update.service /etc/systemd/system/
sudo install -o root -g root -m 0644 deploy/yachad-update.timer /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now yachad-update.timer
sudo systemctl start yachad-update.service
sudo systemctl status yachad-update.service
```

מפתח החתימה הפרטי נשאר אצל מפרסם הגרסאות, מחוץ ל־Pi. יש להתקין אותו בצורה מהימנה; החלפת public key אינה פעולה מרחוק דרך UI. אחרי הריצה הראשונה נוצר status.json קריא ל־yachad בלבד ויופיעו הכפתורים להורה הנבחר. אם בוחרים נתיבים אחרים מעדכנים גם `ReadWritePaths` בשירות העדכון ו־`UPDATE_STATUS_FILE` בסביבת האפליקציה.

timer מחיקת גיבויי עדכון יומיים, כולל ימים שבהם לא הותקן עדכון:

```bash
sudo install -o root -g root -m 0644 deploy/yachad-update-backup-expiry.service /etc/systemd/system/
sudo install -o root -g root -m 0644 deploy/yachad-update-backup-expiry.timer /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now yachad-update-backup-expiry.timer
```

## יצירה ופרסום של גרסה — רק אחרי אישור לריפו

יש להעלות את המקור לריפו שנבחר ולפרסם release רק לאחר אישור המשתמש. המשתמש אישר העלאת מקור לריפו `NehoraiHadad/yahad`; פרסום Release חתום טרם בוצע. פרסום יכול להיות ידני בממשק GitHub או pipeline נפרד בעתיד; אין כאן workflow שמעלה קוד/סודות אוטומטית או צורך דקות GitHub Actions.

במחשב הפיתוח עם Node24 ו־Python3, בסביבת Linux או WSL שמכבדת הרשאות 0600, פעם אחת:

```bash
node scripts/release.mjs keygen /PRIVATE_PATH/release-signing.pem
```

לכל גרסה מעלים `package.json` וגם package-lock לגרסה סמנטית חדשה, בודקים ובונים:

```bash
npm ci
npm run typecheck
npm test
npm run test:updater
npm run build
node scripts/release.mjs package /PRIVATE_OUTPUT/v0.2.0 /PRIVATE_PATH/release-signing.pem
```

מעלים את שלושת הקבצים שנוצרו ל־Release יציב עם tag תואם `v0.2.0`. לא מעלים `.env`, נתוני המשפחה, node_modules, חומר WhatsApp או private PEM. חבילת המקור הרגילה `yachad-family-chat.zip` אינה asset העדכון החתום; משתמשים בפקודת package לעיל. בארכיון העדכון יש frontend שנבנה מראש ו־lockfile; התלויות מותקנות ב־Pi לארכיטקטורה שלו.

## אימות שבוצע וגבולותיו

נבדקו חתימת OpenSSL אמיתית, דחיית שינוי חתימה/ארכיון/path traversal, הרשאות API והפרדת operator בין משפחות, סדר גיבוי לפני הפעלה, כשל גיבוי, rollback אחרי health כושל והתאוששות ממצב תחזוקה. בדיקות השליטה בשירות/רשת/build משתמשות בהזרקה ולא מפעילות systemd אמיתי. בנוסף נבדקו אריזת release אמיתית ו־smoke בחבילת production שהותקנה ללא install scripts בסביבת הפיתוח; פרטי התוצאה ב־VALIDATION.md.

עדיין לא אומתו בפועל: חיבור לריפו אמיתי או פרטי, timer/root service על Pi, הרשאות runuser ב־systemd תחת מערכת ההפעלה שלו, כיבוי חשמל באמצע עדכון, ARM, rollback מול חיבור WhatsApp חי או סבב release מלא מהמחשב שלך עד הטאבלט. אין הבטחה שהמכשיר יתקין כל גרסה עתידית ללא התאמה.

מקורות: [GitHub release assets](https://docs.github.com/en/rest/releases/assets), [systemd.service](https://www.freedesktop.org/software/systemd/man/latest/systemd.service.html).
