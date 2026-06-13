# Code signing policy

Reclaim signs and distributes its Windows release binaries so that users can
verify their authenticity and integrity.

## Windows — SignPath Foundation

Free code signing for Windows builds is provided by [SignPath.io](https://signpath.io),
with the certificate issued by the [SignPath Foundation](https://signpath.org).

> Free code signing provided by [SignPath.io](https://signpath.io), certificate by [SignPath Foundation](https://signpath.org).

### What is signed

- The self-contained `Reclaim.exe` published on the
  [GitHub Releases](../../releases) page.

### How signing works

- Release artifacts are built from this repository using GitHub Actions CI.
- **Only CI-built artifacts** are submitted to SignPath for signing — binaries
  are never built or signed on a maintainer's local machine.
- The signing certificate is issued to the **SignPath Foundation**, not to the
  project or its maintainer.
- The private key is **held and protected by SignPath** on a Hardware Security
  Module (HSM). This project never has access to the private key.

### Project roles

This is a single-maintainer project.

- **Author / committer** (commit access; can modify the repository):
  - https://github.com/Nickpick21120
- **Reviewer**: changes proposed by non-committers require review by the author
  before they can be included in a signed build.

## Privacy

Reclaim does not transmit any information. It is an offline desktop utility; the
only network activity it can initiate is opening your browser to a pre-filled
GitHub issue **if you choose** to send a crash report. See the README for
details.

## Verifying a download

After the signing program is active, you can verify a downloaded `Reclaim.exe`:

1. Right-click the file → **Properties** → **Digital Signatures** tab.
2. The signature should list **SignPath Foundation** as the signer.

If the **Digital Signatures** tab is absent, the build you downloaded predates
the signing program (or wasn't an official release build).
