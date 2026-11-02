# יחד / Yahad

ממשק משפחתי ל־WhatsApp, עם חשבון הורה, הרשאות לילדים ושמירת מידע מקומית מוצפנת.
זהו פרויקט קוד פתוח: אפשר לעבוד ישירות מהמקור ולבנות בעצמכם. קובץ התקנה מוכן הוא אפשרות נוספת.

## Start from source

Install Git and **Node.js 24.x with npm**, then:

```sh
git clone https://github.com/NehoraiHadad/yahad.git
cd yahad
npm ci --ignore-scripts
npm run typecheck
npm run build
```

On Windows, also build the private file-permission helper before running the
application or Store tests; see [development setup](docs/DEVELOPMENT.md).
A source build does not register Windows services or change firewall rules.

`npm run dev` starts a **disposable mock family** on port 4173. It replaces its
previous QA data and creates automatic test sessions. The preview currently binds
all interfaces: use an isolated development environment. It refuses real WhatsApp.
For a normal service, use the [installation guide](docs/INSTALL.md); never expose
the QA preview as a family service.

## Windows installer status

The native Windows installer and updater are under active development. Build tools
are included, but isolated tests do not establish successful installation on a
family computer. Runtime startup on the ASUS test machine is still being diagnosed.
See [validation status](docs/VALIDATION.md).

To build a standalone installer, follow [the Windows build guide](installer/README.md).
It records source identities and payload hashes and bundles its runtimes; end users
do not need developer SDKs.

The current distribution is unsigned; Windows can show an unknown publisher.
Installer signing is separate from updater trust and integrity checks. Building an
unsigned installer does not disable those checks. Third-party binary redistribution
still requires the [license review](docs/DISTRIBUTION-LICENSE-REVIEW.md).

## Project map

| Directory | Purpose |
| --- | --- |
| `src/` | Server, connector, encrypted storage and application behavior |
| `web/` | Parent and child interface |
| `installer/` | Windows installer, service hosts, updater and package builders |
| `deploy/` | Self-hosted deployment and existing Linux updater |
| `scripts/` | Development, backup and release tools |
| `tests/`, `fixtures/` | Automated tests and synthetic source fixtures |
| `docs/` | Guides and architecture references |
| `landing/` | Separate landing page and static publication |
| `evidence/` | Written historical validation reports |

[Development and tests](docs/DEVELOPMENT.md) · [Documentation index](docs/README.md) ·
[Contributing](CONTRIBUTING.md) · [Architecture](docs/ARCHITECTURE.md) · [License](LICENSE)
