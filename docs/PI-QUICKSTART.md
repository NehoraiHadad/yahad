# התקנה ראשונה — Raspberry Pi 4, ARM64, Debian 13

> 0.3.1: follow [the owner setup runbook](AUTOMATIC-UPDATES-SETUP.md) for current update/service integration. Install the root-owned kernel-lock helper before installing `deploy/yachad.service`. Prefer `scripts/setup.ts --web` for a fresh empty store. Preserve existing data, master keys and Caddy CA; do not rerun setup on a configured household.


המסלול הזה תואם לפלט שהמשתמש שלח ב־26.10.2026: **Raspberry Pi 4 Model B Rev 1.4, כ־4GB RAM, aarch64/64bit, Debian13 Trixie, כ־21GB פנויים מתוך מחיצה של 29GB**. זוהי עדות מהטרמינל שלו; לא בוצעה כאן התחברות מרחוק למכשיר. עדיין אין מדידת ביצועים או התקנת האפליקציה על ה־Pi. במכשיר אחר מריצים קודם `bash scripts/inspect-host.sh` ומתאימים הוראות.

הכול נשאר ברשת הביתית. מתחילים ב־mock. לא מפעילים WhatsApp אמיתי, לא משנים ראוטר ולא פותחים פורטים החוצה במסגרת ההתקנה הזו.

## 1. חבילות ו־Node24

בטרמינל ה־Pi:

```bash
hostname -I
command -v node
node --version
```

אם כבר מותקן Node24, מדלגים על הורדת Node. אם מותקנת גרסה אחרת, בודקים מה משתמש בה לפני החלפה. בהתקנה הנקייה ללא Node:

```bash
sudo apt update
sudo apt install -y curl ca-certificates xz-utils unzip ffmpeg python3 openssl caddy
mkdir -p ~/yachad-install
cd ~/yachad-install
curl --fail --location --output node-v24.22.1-linux-arm64.tar.xz https://nodejs.org/dist/v24.22.1/node-v24.22.1-linux-arm64.tar.xz
printf '%s  %s\n' dbabf7a1cbf947e6059ffad2c27b80041db97dea1e553b62afdee7f13fca336e node-v24.22.1-linux-arm64.tar.xz | sha256sum --check -
```

רק אחרי `OK` ממשיכים. ה־SHA256 נבדק מול קובץ SHASUMS256 הרשמי בזמן הכנת ההוראות. יש להתקין את הארכיון הזה רק על ARM64, לא ARM32.

```bash
sudo test ! -e /opt/node-v24.22.1-linux-arm64
sudo tar --extract --xz --file node-v24.22.1-linux-arm64.tar.xz --directory /opt --no-same-owner
sudo ln -s /opt/node-v24.22.1-linux-arm64/bin/node /usr/local/bin/node
sudo ln -s /opt/node-v24.22.1-linux-arm64/bin/npm /usr/local/bin/npm
node --version
npm --version
```

אין לדרוס symlinks קיימים: אם הפקודה מדווחת File exists, בודקים את ההתקנה הקיימת במקום להוסיף `-f`. Caddy עשוי לעלות עם דף ברירת מחדל מקומי לאחר apt; עוצרים אותו עד הגדרת LAN/TLS בהמשך: `sudo systemctl stop caddy`.

## 2. העברת המקור ובנייה

הריפו שהמשתמש יצר ציבורי, ולכן אין צורך להכניס ל־Pi סיסמה או token לצורך הורדת הקוד:

```bash
sudo apt install -y git
cd ~
git clone https://github.com/NehoraiHadad/yahad.git
cd yahad
npm ci
npm run typecheck
npm test
npm run test:updater
npm run build
```

אין להעתיק node_modules ממחשב Windows ל־Pi. אם הריפו יהפוך לפרטי בעתיד, צריך הרשאת קריאה מוגבלת או העברת ZIP מקומית. אין לשים token בכתובת URL או בצ׳אט.

## 3. התקנה קבועה

מכאן מניחים שאין התקנת yachad קודמת. אם יש משתמש/גרסה/נתונים קיימים, לא דורסים ולא מריצים setup מחדש; עוברים למסלול עדכון.

```bash
sudo useradd --system --home /var/lib/yachad --shell /usr/sbin/nologin yachad
sudo install -d -o root -g yachad -m 0750 /etc/yachad /opt/yachad /opt/yachad/releases
sudo install -d -o yachad -g yachad -m 0700 /var/lib/yachad
sudo test ! -e /opt/yachad/releases/v0.2.0
sudo cp -a "$PWD" /opt/yachad/releases/v0.2.0
sudo chown -R root:yachad /opt/yachad/releases/v0.2.0
sudo chmod -R o-rwx /opt/yachad/releases/v0.2.0
sudo ln -s /opt/yachad/releases/v0.2.0 /opt/yachad/current
sudo install -o root -g yachad -m 0640 deploy/yachad.env.example /etc/yachad/yachad.env
sudo bash -euc 'test ! -e /etc/yachad/master.key; test ! -L /etc/yachad/master.key; umask 077; openssl rand -base64 32 > /etc/yachad/master.key; chown yachad:yachad /etc/yachad/master.key; chmod 0600 /etc/yachad/master.key'
```

קובעים כתובת IPv4 פרטית יציבה של ה־Pi, למשל בעזרת DHCP reservation שמנהל הרשת מאשר. לא מבצעים port forwarding. הכתובת צריכה להישאר זהה כדי שלא תישבר הגישה מהטאבלט.

```bash
sudo nano /etc/yachad/yachad.env
```

משנים רק `APP_ORIGIN=https://ACTUAL_PRIVATE_LAN_IP` לכתובת האמיתית, ללא `/` בסוף. משאירים `CONNECTOR=mock`, ‏`ALLOW_REAL_WHATSAPP=no`, ‏`HOST=127.0.0.1`, ‏`COOKIE_SECURE=true`.

יוצרים חשבונות, עם הכתובת האמיתית במקום placeholder:

```bash
cd /opt/yachad/current
sudo -u yachad env SETUP_WRITE_ENV=false DATA_DIR=/var/lib/yachad MASTER_KEY_FILE=/etc/yachad/master.key APP_ORIGIN=https://ACTUAL_PRIVATE_LAN_IP COOKIE_SECURE=true node --import tsx scripts/setup.ts
sudo install -d -o root -g root -m 0755 /usr/local/lib/yachad-updater
sudo install -o root -g root -m 0644 deploy/data-lock.py /usr/local/lib/yachad-updater/
sudo install -o root -g root -m 0644 deploy/yachad.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now yachad
sudo systemctl status yachad --no-pager
curl --fail http://127.0.0.1:3000/health/ready
```

הסיסמאות נשאלות מקומית ומוסתרות. בוחרים סיסמה שונה להורה ולילדה, לפחות 4 תווים. לא משתמשים בסיסמת SSH כברירת מחדל ולא מעבירים סודות בצ׳אט.

## 4. HTTPS מקומי

יוצרים `/etc/yachad/caddy.env` עם שורה אחת `YACHAD_LAN_IP=ACTUAL_PRIVATE_LAN_IP` (בלי `https://`), אותה כתובת שהוגדרה ב־APP_ORIGIN:

```bash
sudo nano /etc/yachad/caddy.env
sudo chown root:root /etc/yachad/caddy.env
sudo chmod 0600 /etc/yachad/caddy.env
sudo install -o root -g root -m 0644 deploy/Caddyfile /etc/caddy/Caddyfile
sudo install -d -o root -g root -m 0755 /etc/systemd/system/caddy.service.d
sudo install -o root -g root -m 0644 deploy/caddy-environment.conf /etc/systemd/system/caddy.service.d/yachad.conf
sudo bash -euc 'set -a; . /etc/yachad/caddy.env; set +a; caddy validate --config /etc/caddy/Caddyfile --adapter caddyfile'
sudo systemctl daemon-reload
sudo systemctl enable --now caddy
sudo ss -ltnp
```

מאמתים: Node מאזין ל־127.0.0.1:3000 בלבד; Caddy לכתובת הפרטית:443 בלבד. יש לבדוק גם firewall/שירותים קיימים; אין הנחה שכתובת פרטית לבדה מתקנת port forwarding שהיה מוגדר קודם בראוטר.

## 5. אמון בטאבלט ושימוש

מעבירים **רק** את `/var/lib/caddy/.local/share/caddy/pki/authorities/local/root.crt` דרך מחשב מנהל מהימן. בודקים fingerprint מקומית:

```bash
sudo openssl x509 -in /var/lib/caddy/.local/share/caddy/pki/authorities/local/root.crt -noout -fingerprint -sha256
```

משווים לתעודה שהגיעה לטאבלט. Android: מתקינים כתעודת CA בהגדרות האבטחה בהתאם לגרסה. iPad: מתקינים פרופיל תעודה ומפעילים אמון מלא בהגדרות Certificate Trust. אין להעביר את root.key או מפתחות WhatsApp. אין לדלג על אזהרות TLS.

פותחים `https://ACTUAL_PRIVATE_LAN_IP`, נכנסים כהורה, מפעילים את המתאם המדומה ובוחרים קבוצות. בטאבלט יוצאים ונכנסים עם חשבון הילדה. בודקים מיקרופון: אישור, הקלטה קצרה, האזנה, שליחה, רענון וניגון חוזר; גם סירוב הרשאה ונעילה במכשיר משותף. את שלבי האמון המדויקים נתאים לפי Android/iPad והגרסה בפועל.

## 6. גיבוי ועדכונים

לאחר שההפעלה המקומית תקינה, מגדירים [גיבוי ושחזור](BACKUP.md), פותחים להורה [מצב המערכת](OPERATIONS.md), ומבצעים פעם אחת [התקנת מנגנון Releases](UPDATES.md). נתחיל במצב notify; ניתן להפעיל auto לפי בחירת מנהל ה־Pi. הגדרת הריפו אינה קישור WhatsApp. החיבור האמיתי נשאר שלב נפרד לפי [PI.md](PI.md).

לבסוף בודקים בפועל reboot מאושר, עליית שני השירותים, גישה מהטאבלט, גיבוי ושחזור לספרייה נפרדת. אין להציג את השלבים האלה כבדוקים עד שבוצעו על המכשיר.
