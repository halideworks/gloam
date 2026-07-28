# Deploying the Gloam site

The site in `site/` is fully static: no build step, no bundler, nothing
server-side.

It is served from **Cloudflare Pages**, deployed by
`.github/workflows/deploy-site.yml` on any push to `main` that touches
`site/**`. The erebus setup below came first and is kept as a documented
fallback: the machine still has the files and the Caddy block, so pointing DNS
back at the tunnel restores it without any work in this repo.

## Cloudflare Pages

Project `gloam` in the account whose id is the `CLOUDFLARE_ACCOUNT_ID` repo
variable. Preview host is `gloam-d4b.pages.dev`, since the bare `gloam`
subdomain was already taken account-wide.

Two files in `site/` do what the Caddy block used to. Pages consumes both at
deploy time and does not publish them, which is worth remembering because it
means a mistake in either is invisible except in the response headers:

- `_headers` carries the CSP, HSTS, and the two content types that matter:
  `text/markdown` for `whitepaper.md`, and `application/manifest+json`.
- `_redirects` carries the `.html` to clean-URL 301s. Unlike the Caddy version
  this cannot be a regex, so **a new page needs a new line here**.

The workflow curls the live site after deploying and fails the run if the
markdown content type, the CSP, HSTS, or any of the three routes has gone
missing. Those are the failures that do not show up in a rendered page.

To deploy by hand, which does not need the repo secret:

```sh
npx wrangler pages deploy site --project-name=gloam --branch=main
```

### Required repository configuration

- Variable `CLOUDFLARE_ACCOUNT_ID`.
- Secret `CLOUDFLARE_API_TOKEN`, a token with **Cloudflare Pages: Edit**. The
  account-wide OAuth token that `wrangler login` produces cannot edit DNS, so
  the custom domain is attached from the Cloudflare dashboard rather than from
  here.

### Bump the `?v=` on style.css when the CSS changes

There is no build step, so the filename never changes on its own. Each page
links `style.css?v=<hash>`; the hash is the first 8 characters of the file's
MD5:

```sh
md5sum site/style.css | cut -c1-8
```

Both hosts serve the stylesheet `must-revalidate`, so this is not strictly
required, but changing the URL is what guarantees an immediate update at the
Cloudflare edge without a purge. Skipping it once already caused visitors to
pair new HTML with a stale stylesheet, which rendered the whole site in the
light theme.

## The erebus fallback

Kept because it is still a working host for this site, not because anything
routes to it.

### What is already on erebus

Worth knowing before touching anything, since this is a shared host:

- Caddy runs in Docker as the container `caddy`
  (`iarekylew00t/caddy-cloudflare:latest`), already bound to :80 and :443.
- Its config is `/nvme-mirror/apps/caddy/Caddyfile` on the host, mounted to
  `/etc/caddy/Caddyfile`. **Every other site on this box lives in that same
  file**, which is why step 3 validates before reloading.
- Static sites live at `/nvme-mirror/static/<name>` on the host, mounted
  read-only at `/srv/sites/<name>`. Existing: `itssoover`, `stifle`, `dee`,
  `sigil`. Gloam follows the same convention as `gloam`.
- The global config sets `acme_dns cloudflare {env.CF_API_TOKEN}`, so the
  neighbouring sites get certificates over DNS-01.

Gloam does **not** use that certificate path. It is served through a Cloudflare
tunnel, so TLS terminates at the edge and the site block is declared as
`http://` to opt out of Caddy's automatic HTTPS for that one site. See the
comments in `caddy-site-block.txt`.

### Steps

1. Copy the site to the static root:

   ```sh
   rsync -av --delete site/ erebus:/nvme-mirror/static/gloam/
   ```

   Nothing in `site/` is excluded; every file there is meant to be served.

2. Append the site block:

   ```sh
   cat deploy/caddy-site-block.txt | ssh erebus 'cat >> /nvme-mirror/apps/caddy/Caddyfile'
   ```

3. Validate, then reload without dropping connections:

   ```sh
   ssh erebus 'docker exec caddy caddy validate --config /etc/caddy/Caddyfile'
   ssh erebus 'docker exec caddy caddy reload --config /etc/caddy/Caddyfile'
   ```

   Validate first. A bad Caddyfile makes `reload` fail and leaves the running
   config in place, which is safe, but the other sites share this file.

4. Confirm it serves locally, before any DNS or tunnel exists. The `Host`
   header is what selects the site block:

   ```sh
   ssh erebus 'curl -sI -H "Host: getgloam.org" http://localhost/ | head -5'
   ssh erebus 'curl -sI -H "Host: getgloam.org" http://localhost/whitepaper.md | grep -i content-type'
   ```

   Expect `200` on the first and `text/markdown; charset=utf-8` on the second.

5. Tunnel (done in the Cloudflare dashboard, not from here). Point both public
   hostnames at the origin over plain HTTP:

   | Public hostname    | Service                 |
   | ------------------ | ----------------------- |
   | `getgloam.org`     | `http://localhost:80`   |
   | `www.getgloam.org` | `http://localhost:80`   |

   Cloudflare creates the proxied DNS records for the tunnel automatically.
   Nothing needs to be opened inbound on erebus.

6. Verify from outside once the tunnel is up:

   ```sh
   curl -sI https://getgloam.org/ | head -20
   curl -sI https://getgloam.org/whitepaper.md | grep -i content-type
   curl -s  https://getgloam.org/llms.txt | head -5
   curl -sI https://www.getgloam.org/ | grep -i location    # 301 to apex
   curl -sI https://getgloam.org/nope | head -1             # 404
   curl -sI https://getgloam.org/guides | head -1           # 200, from guides.html
   curl -sI https://getgloam.org/guides.html | grep -i location   # 301 to /guides
   curl -sI https://getgloam.org/index.html | grep -i location    # 301 to /
   ```

### The Caddyfile bind-mount trap

**Never replace the Caddyfile with `mv`.** It is a single-file bind mount
(`/nvme-mirror/apps/caddy/Caddyfile` → `/etc/caddy/Caddyfile`). Docker pins that
mount to the file's inode when the container starts, so `mv` swaps the host path
to a new inode while the container keeps reading the old one. The host file then
looks correct, `grep` on the host agrees, and Caddy silently keeps serving the
previous config. Edit in place instead:

```sh
# good: same inode
cat new-caddyfile > /nvme-mirror/apps/caddy/Caddyfile
printf '\n%s\n' "$block" >> /nvme-mirror/apps/caddy/Caddyfile

# bad: new inode, container never sees it
mv new-caddyfile /nvme-mirror/apps/caddy/Caddyfile
```

If it has already been `mv`d, write through the container to reach the mounted
inode without restarting anything:

```sh
cat /nvme-mirror/apps/caddy/Caddyfile | docker exec -i caddy sh -c 'cat > /etc/caddy/Caddyfile'
```

The two inodes stay divergent until the container restarts, so
`docker restart caddy` at a quiet moment re-pins them. That briefly drops every
site in this Caddyfile, which is why it is not part of the deploy steps.

### Notes

- Client IPs will log as `127.0.0.1`, because the tunnel connects over
  loopback. Fixing that needs a global `servers { trusted_proxies ... }` block
  plus reading `CF-Connecting-IP`, which would affect every site in this
  Caddyfile, so it is deliberately left alone.
- Cloudflare compresses to the client at the edge, so `encode` here mostly
  saves loopback bytes. Harmless, and correct if the tunnel is ever removed.

### Updating the files on erebus

```sh
rsync -av --delete site/ erebus:/nvme-mirror/static/gloam/
```

No Caddy reload is needed for content changes. Only re-run steps 2 and 3 if the
site block itself changes. When it does, the block is already in the Caddyfile,
so step 2 is a replacement rather than an append: edit the existing
`http://getgloam.org` block in place (see the `mv` trap below), then validate
and reload as in step 3.

## Extensionless page URLs

Pages are served without `.html`: `/guides` reads `guides.html` off disk. The
extension is still the filename, only never the URL. Three pieces have to agree,
and all three live in this repo:

- On Pages, this is the default behaviour, and `site/_redirects` states the
  301s explicitly rather than inheriting them.
- On erebus, `try_files {path} {path}.html` in the site block is what makes
  `/guides` resolve at all, and two `redir`s send `/guides.html` and
  `/index.html` to the clean URL. Without those both spellings answer 200,
  which splits a page's canonical URL from its indexed one.
- The links, canonicals, `og:url`s, `sitemap.xml` and `llms.txt` in `site/`,
  which all name the extensionless form.

On erebus, adding a page is just dropping `<name>.html` into `site/`, since
nothing in the Caddy block enumerates pages. On Pages the page will serve, but
its `.html` spelling needs a line in `_redirects` to be a real 301.

Note that `{re.name}` is not a placeholder Caddy fills in: the capture group
needs its index, `{re.name.1}`. Without it every `.html` URL redirects to `/`.

## Regenerating derived files

`site/whitepaper.md` is generated, not hand-written. After editing
`site/whitepaper.html`:

```sh
python scripts/build-whitepaper-md.py
```

The before/after comparison images are likewise generated by
`scripts/render-comparison-images.py`; provenance and method are in
`site/assets/CREDITS.txt`.
