# Publishing Reclaim on GitHub — a first-timer's guide

This walks you through putting Reclaim on GitHub and producing a downloadable
`Reclaim.exe` that anyone can grab. You do **not** need to build the exe on your
own (corporate) machine — GitHub builds it for you in the cloud.

There are two ways to do the initial upload. **Option A (GitHub Desktop)** is the
easiest if you've never used Git. **Option B (command line)** is faster if you're
comfortable in a terminal.

---

## One-time setup

1. **Make a free GitHub account** at https://github.com if you don't have one.
2. **Create a new repository**:
   - Click the **+** (top-right) → **New repository**.
   - Name it `reclaim` (or anything you like).
   - Choose **Public** (required for free Actions builds and open-source sharing).
   - Do **not** tick "Add a README" — your project already has files.
   - Click **Create repository**. Leave the page open; you'll need the URL it shows.

---

## Option A — GitHub Desktop (recommended for beginners)

1. Download and install **GitHub Desktop**: https://desktop.github.com
2. Sign in with your GitHub account (File → Options → Accounts).
3. **File → Add local repository**, and pick the `reclaim` folder on your Desktop.
   - If it says "this isn't a Git repository," click **create a repository** when
     prompted — point it at the same `reclaim` folder.
4. You'll see all the project files listed as changes. In the bottom-left, type a
   summary like `Initial commit` and click **Commit to main**.
5. Click **Publish repository** (top bar). Untick "Keep this code private" if you
   want it public. Click **Publish repository**.

That's it — your code is on GitHub. Skip to **Making a release** below.

---

## Option B — command line (Git)

From inside the `reclaim` folder:

```
git init
git add .
git commit -m "Initial commit"
git branch -M main
git remote add origin https://github.com/YOUR-USERNAME/reclaim.git
git push -u origin main
```

(Replace `YOUR-USERNAME` with your GitHub username. The exact remote URL is shown
on your new repo's page.)

---

## Making a release (this builds the .exe automatically)

Reclaim includes a GitHub Actions workflow (`.github/workflows/build.yml`) that
builds the self-contained `Reclaim.exe` and attaches it to a release whenever you
push a **version tag**. A tag looks like `v0.10.3`.

### Using GitHub Desktop
There's no direct "tag" button, so the easiest path is the website:

1. Go to your repo on github.com.
2. Click **Releases** (right-hand side) → **Draft a new release**.
3. Click **Choose a tag**, type `v0.10.3`, and click **Create new tag on publish**.
4. Give it a title (e.g. `Reclaim 0.10.3`) and a short description.
5. Click **Publish release**.

### Using the command line
```
git tag v0.10.3
git push origin v0.10.3
```

### What happens next
- Go to the **Actions** tab in your repo. You'll see a build running.
- It compiles the exe in the cloud (takes a couple of minutes) and runs the tests.
- When it finishes, go to **Releases** — `Reclaim.exe` will be attached to the
  release, ready for anyone to download.

---

## What your users will see

Because the app isn't code-signed (see the README/notes), the first time someone
runs `Reclaim.exe`, Windows SmartScreen may show **"Windows protected your PC."**
They click **More info → Run anyway**. This is normal for unsigned apps. You may
want to mention this in your release notes so people aren't alarmed.

---

## Updating later

Whenever you want to ship a new version:
1. Commit your changes (GitHub Desktop: commit + push; or `git add . && git commit
   -m "..." && git push`).
2. Make a new release with a higher version tag (e.g. `v0.11.0`).
3. The workflow rebuilds and attaches the new exe automatically.

---

## Troubleshooting

- **The Actions build failed**: open the **Actions** tab, click the failed run, and
  read the red step. The most common cause is a compile error — the same thing
  you'd see locally. Fix, commit, push, and tag again.
- **No exe on the release**: make sure your tag starts with `v` (e.g. `v0.10.3`),
  since the workflow only attaches the exe for tags matching `v*`.
- **You want to test a build without releasing**: in the **Actions** tab, choose
  the workflow and click **Run workflow** — it builds and saves the exe as an
  "artifact" you can download, without creating a public release.
