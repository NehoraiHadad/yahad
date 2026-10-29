# Proposed distribution, not published

The existing NehoraiHadad/yahad repository was read through the authorized GitHub
connector: visibility public; current connection has repository write permission.
A standard GitHub Release asset is a concrete existing HTTPS delivery option.
The offline package is below the documented 2 GiB per-asset limit. Public release
assets can be downloaded without a user GitHub account or token. There is no need
to buy a hosting service for this proposed path; GitHub documents no total release
size or bandwidth limit. No upload, release/tag creation, settings change, purchase
or Pages deployment has been performed or authorized here.

After integration/review, an authorized publisher must choose a NEW release/tag,
upload the exact complete offline EXE, and retain it. Do not alter the existing
0.5.3 quota release/tag or use `latest` as the bootstrap source. Use the actual
tag-specific asset URL returned by GitHub, then rebuild the small launcher with
that fixed HTTPS URL and the exact committed size/hash/source pin. The predictable
URL pattern alone is not evidence that an asset exists. No GitHub PAT, account
credential or expiring signed URL is embedded. The existing same-host/GitHub-asset
redirect policy remains narrow; real hosted-product download must be tested before
enabling the landing-page button. The optional complete EXE supports offline use.

The native launcher and full package remain unsigned development artifacts.
Complete signing/distribution-license/source-notice review before public delivery;
no signing keys, updater key enrollment or production account credentials are
created. GitHub repository write access is required to publish assets; private
assets would require download authorization and are unsuitable for anonymous
family installs. Do not make any private repository public to implement this plan.

Sources: https://docs.github.com/en/repositories/releasing-projects-on-github/about-releases
and https://docs.github.com/en/rest/releases/assets#get-a-release-asset
