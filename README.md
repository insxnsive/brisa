# Brisa site

Static one-page site for [Brisa](https://github.com/insxnsive/brisa): `index.html` in Brazilian Portuguese, `en.html` in English. Hand-written HTML and CSS, no build step, no tracking, no remote requests.

Deploy: `npx wrangler pages deploy . --project-name brisa --branch main` from this folder. It lives at `https://brisa-a0u.pages.dev/`, with the English page at `/en`, so every path is relative. The same files are also on the `site` branch of `insxnsive/brisa`, which is ready for GitHub Pages if that switch is ever turned on.

Local preview: `python -m http.server 8080` in this folder, then open `http://localhost:8080/`.

Assets: the icon comes from the Brisa repository; both screenshots are Brisa 1.0.1 in Portuguese. The only webfont is Schibsted Grotesk, latin subset, under the SIL Open Font License 1.1.
