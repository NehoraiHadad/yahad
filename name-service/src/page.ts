// The page a person sees on opening the service's address in a browser. It explains the service and
// points to Yahad and its developer. The logo is the landing page's own published asset, not a copy.

const LANDING = "https://nehoraihadad.github.io/yahad/";
const LOGO = `${LANDING}assets/nehorai.svg`;
const DEVELOPER = "https://maklikim.co.il/l/me";

const HTML = `<!doctype html>
<html lang="he" dir="rtl">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta name="robots" content="noindex">
<title>יחד · שירות כתובות</title>
<style>
:root{color-scheme:light dark;--bg:#f6f7fb;--fg:#14182b;--muted:#4b526b;--card:#fff;--line:#dfe3ee;--link:#2347b8}
@media (prefers-color-scheme:dark){:root{--bg:#050e22;--fg:#eef2fb;--muted:#b3bcd6;--card:#0a1735;--line:#2a3d78;--link:#9db4ff}}
*{box-sizing:border-box}
body{margin:0;min-height:100vh;display:grid;place-items:center;padding:24px 16px;background:var(--bg);color:var(--fg);
font:1rem/1.6 system-ui,-apple-system,"Segoe UI",Arial,sans-serif}
main{max-inline-size:34rem;background:var(--card);border:1px solid var(--line);border-radius:16px;padding:28px 24px}
h1{margin:0 0 12px;font-size:1.5rem}
p{margin:0 0 12px}
.muted{color:var(--muted)}
a{color:var(--link)}
footer{margin-block-start:20px;padding-block-start:16px;border-block-start:1px solid var(--line)}
.credit{display:inline-flex;align-items:center;gap:8px;text-decoration:none;color:var(--muted)}
.credit img{border-radius:6px}
</style>
</head>
<body>
<main>
<h1>שירות הכתובות של יחד</h1>
<p>השירות נותן למחשב הבית של משפחה שמשתמשת ב<a href="${LANDING}">יחד</a> כתובת קבועה ומאובטחת ברשת הביתית, כדי שהטאבלטים בבית ייכנסו בלי אזהרות אבטחה.</p>
<p class="muted">הוא שומר רק מה שצריך כדי לתת את הכתובת: הכתובת הפנימית של המחשב ברשת הבית, רשומות האימות של התעודה ופרטים טכניים לניהול השם. הודעות, תמונות ופרטים אישיים לא עוברים דרכו.</p>
<p><a href="${LANDING}">מה זה יחד?</a></p>
<footer><a class="credit" href="${DEVELOPER}"><img src="${LOGO}" alt="" width="24" height="24">פותח ע״י נהוראי</a></footer>
</main>
</body>
</html>
`;

const HEADERS = {
  "Content-Type": "text/html; charset=utf-8",
  "Cache-Control": "public, max-age=3600",
  "Content-Security-Policy":
    "default-src 'none'; img-src https://nehoraihadad.github.io; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'",
  "X-Content-Type-Options": "nosniff",
};

/** GET returns the page; HEAD the same headers with no body. */
export function homePage(method: "GET" | "HEAD"): Response {
  return new Response(method === "HEAD" ? null : HTML, { status: 200, headers: HEADERS });
}
