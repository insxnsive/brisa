# Brisa site

Static one-page site for [Brisa](https://github.com/insxnsive/brisa): `index.html` in Brazilian Portuguese, `en.html` in English. Hand-written HTML and CSS, no build step, no tracking, no remote requests.

Deploy: push these files to the `site` branch of `insxnsive/brisa`; GitHub Pages serves that branch at `https://insxnsive.github.io/brisa/`, with the English page at `/en.html`. `npx wrangler pages deploy . --project-name brisa --branch main` keeps a mirror on Cloudflare Pages at `https://brisa-a0u.pages.dev/`, and every path in the HTML is relative.

Local preview: `python -m http.server 8080` in this folder, then open `http://localhost:8080/`.

Assets: the icon comes from the Brisa repository; both screenshots are Brisa 1.0.1 in Portuguese. The only webfont is Schibsted Grotesk, latin subset, under the SIL Open Font License 1.1.
