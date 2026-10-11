# Security

## Reporting a vulnerability

Report it privately, through GitHub's private vulnerability reporting: the repository's **Security** tab, then
**Report a vulnerability**, or [this link](https://github.com/serionist/WriteableFileProviders/security/advisories/new).
Only the maintainers see the report. Never put a vulnerability in an issue, a pull request or a discussion, which anyone
can read.

Include what you can of:

- the package and version, and the provider involved;
- what an attacker can do, and what they need first (configuration, access, a crafted path or URL);
- the smallest code that shows it, without your own keys, tokens or endpoints.

A maintainer answers in the advisory. The fix is developed there, released as a patch of every supported line it
affects, and the advisory is then published with the fixed versions, crediting you unless you ask otherwise.

What counts, for example:

- a path that reaches outside the provider's root folder, or a delete or move that touches more than it named;
- a signed URL or gateway token that lives longer, or allows more, than the caller asked;
- credentials that reach a log, an exception message or a serialized configuration they should not;
- a forged or replayed gateway token that the gateway accepts.

A vulnerability in a cloud SDK itself goes to that SDK's maintainers; tell us too if the way these packages use it is
affected.

## Supported versions

| Version | Supported |
|---------|-----------|
| The latest major (2027.x today) | Every fix, in its next minor or patch release |
| The previous major | Security fixes for 6 months after the next major ships (none yet: 2027 is the first) |
| 2026.10.10.x (.NET 8) | Security fixes only, until a date the maintainers will announce here |
| Older | No: move to 2027.x, or to 2026.10.10.x on .NET 8 |

From 2027.0.0, every package of a release has the same version, and the major is a sequence number, not a date: the
next breaking release is 2028.0.0, whenever it ships. The packages in one application must all come from the same
major: a 2026.10.10.x provider does not load against Common 2027. The [versioning policy](docs/guide/versioning.md)
has the whole of it.
