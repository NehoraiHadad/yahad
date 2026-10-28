# חוזה המתאם והמצב האמיתי של Baileys

החוזה ב־`src/connectors/types.ts` כולל lifecycle, רשימת קבוצות, קליטה, שליחה, reply, media, תגובה, edit/delete, presence/read/history ו־Capabilities. המתאם אינו יודע מיהו משתמש HTTP; הוא מקבל hooks לבדיקת הרשאת קליטה משפחתית, גישה ל־auth מוצפן ועדכון הודעות. אימות משתמש/משפחה/ילד/קבוצה נמצא ב־API וב־Store.

## מחקר והחלטה — 26.10.2026

- מקור ה־FAQ שנמסר נבדק: [Baileys FAQ](https://baileys.wiki/faq).
- גרסת npm וה־release הראשי בעת הבדיקה: `7.0.0-rc14`, נעולה ב־package-lock. [release](https://github.com/WhiskeySockets/Baileys/releases/tag/v7.0.0-rc14).
- הפרויקט מתוחזק אך ב־release candidate, עם שינויים שוברים ותקלות קישור פתוחות. חיבור אמיתי לא הושלם כאן ולכן אין טענה שה־QR אכן יעבוד מול חשבון נתון היום.
- מדיניות האבטחה מציינת תמיכה ב־7.x. גרסאות ישנות נפגעו גם מתקלת spoofing של upsert/history. הגרסה שנבחרה אחרי תיקוני `rc12`; אירועי upsert עם requestId אינם נקלטים באפליקציה. [Security](https://github.com/WhiskeySockets/Baileys/security), [advisory](https://github.com/WhiskeySockets/Baileys/security/advisories/GHSA-qvv5-jq5g-4cgg).
- התיעוד אינו ממליץ על `useMultiFileAuthState` לפרודקשן. נבנה AuthenticationState מבוסס SQLite, כולל creds ו־Signal keys, עם BufferJSON, שחזור AppStateSyncKeyData ושמירה אטומית לפני resolve. [Session management](https://baileys.wiki/authentication/session-management).

## יכולות שהמימוש חושף

| פעולה | מימוש Baileys | מה אומת כאן |
| --- | --- | --- |
| QR וחיבור | connection.update; QR להורה בלבד | lifecycle ו־QR עם socket בדיקה; לא סריקה אמיתית |
| auth | creds.update + SignalKeyStore מוצפן | שמירה, Buffer roundtrip, מחיקות keys ופרוטובאף |
| חיבור מחדש | backoff+jitter, restartRequired, terminal logout/badSession/conflict | reconnect ו־logout עם transport מוזרק; לא תעבורה ל־WhatsApp |
| טקסט/mענה | sendMessage + quoted מאותה קבוצה | מיפוי transport; UI ו־API במצב mock |
| תמונה/סרטון | bytes + caption + thumbnail אמיתי בזיכרון | מדיה מקומית, thumbnail, API ודפדפן; לא משלוח אמיתי |
| מסמך | document bytes, MIME מזוהה, filename/caption | מיפוי ו־API; הורדה מאומתת |
| קול | Opus Ogg עם ptt=true ו־duration אמיתי | המרה ב־ffmpeg, משך, אחסון וניגון דוגמה; לא מיקרופון פיזי |
| מדבקה | WebP bytes | מיפוי ו־API וטעינה בדפדפן; ללא עורך מדבקות מובנה |
| תגובה | react + key, messages.reaction | קליטה/מיפוי ו־UI mock; זהות ההורה |
| עריכה | text+edit; protocol MESSAGE_EDIT | נרמול ומדיניות בעלות/זמן; UI/API mock |
| מחיקה | delete+key; messages.delete/protocol REVOKE | נרמול, tombstone ומחיקת מדיה; mock |
| הקלדה | sendPresenceUpdate / presence.update | handler, איפוס והגבלת קבוצה; mock |
| מסירה/קריאה | messages.update status, readMessages | מיפוי statuses ורמת API; mock UI, לא קבלות חיות |
| היסטוריה | messaging-history.set + fetchMessageHistory | סינון ועימוד; לא שחזור חי של חוסר ברשת |

מקורות: [Message actions](https://baileys.wiki/messaging/message-actions), [Media](https://baileys.wiki/messaging/media-messages), [History sync](https://baileys.wiki/advanced/history-sync), [Events](https://baileys.wiki/concepts/events), [Socket config](https://baileys.wiki/concepts/socket-config).

Capabilities הן יכולות הקוד/הספרייה, ולא אישור שהחשבון הפרטי עבר בדיקת קצה לקצה. ה־UI בונה פעולות לפי המתאם הפעיל, והשרת דוחה פעולה שהמתאם אינו תומך בה. שיחות קול/וידאו, סקרים, סטטוסים ותוכן חד־פעמי אינם מוצגים כפעולות עובדות. receipt של ״נקרא״ מוצג רק מהמתאם, ואינו מוצג כהוכחה שכל חברי הקבוצה קראו.

## חיבור רציף והיסטוריה

ה־socket אינו נוצר מחדש לכל בקשת HTTP. ספרייה אחת פועלת לכל משפחה מקושרת. פרטי auth מוצפנים במנוחה ונשארים בשרת בלבד. `cachedGroupMetadata` נשמר בזיכרון ל־5 דקות ומבוטל בשינוי קבוצה/משתתפים. משתמשים בגרסת WhatsApp Web שמגיעה עם הספרייה; אין הורדה אוטומטית של version חדש בכל connect. `markOnlineOnConnect=false`, ‏`syncFullHistory=false` כדי לצמצם עומס התחלתי.

אירועי history/upsert עוברים אותה מדיניות; רק תוכן של קבוצות מוכרות באותה משפחה נשמר עבור ההורה; הרשאות ילדים נבדקות בנפרד בכל גישה. הודעות פרטיות וקבוצות לא מוכרות מסוננות. יש תור מוגבל ומדיה מוגבלת כדי לצמצם עומס, אבל הספרייה יכולה לפענח payload גדול לפני הסינון. בקשה להיסטוריה ישנה היא בקשה לשרת WhatsApp, לא הבטחה לתוצאה. בזמן ניתוק ייתכן שחלק מההודעות לא ישוחזרו כלל. גם אחרי קליטת history מוצגת אינדיקציית partial; אין ״הכול מסונכרן״ מלאכותי.

הפעלה אחרי restart מנסה להשתמש באישורים הקיימים רק אם הקישור פעיל. `restartRequired` מביא reconnect מהיר; ניתוקים אחרים שניתנים לשחזור משתמשים ב־backoff עד דקה. loggedOut/badSession/forbidden/mismatch מבטלים reconnect ודורשים QR. מצב terminal נשמר בלי QR, ואישורים פגומים נמחקים. connectionReplaced מוצג כקונפליקט. QR לא נכתב לקובץ או ללוג.

[WhatsApp linked devices](https://faq.whatsapp.com/378279804439436): שימוש במכשירים מקושרים עשוי להיפסק אם הטלפון הראשי לא בשימוש יותר מ־14 יום. לא בוצעה כאן בדיקת המתנה של 14 יום.

## החלפת מתאם בעתיד

לא מובטחת החלפה ללא שינוי מוצר. ספק אחר עלול לתמוך רק בחלק מהודעות המדיה, בהיסטוריה אחרת או במודל זהות שונה. החוזה צריך להצטמצם/להתרחב עם capabilities, וה־UI והמדיניות עשויים להשתנות. בפרט אין כאן טענה שה־WhatsApp Business API הרשמי מאפשר לאותה חוויית קבוצות וחשבון הורה להגר באופן שקוף.
