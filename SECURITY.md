# Security Policy

## Reporting

Please report security issues privately to the repository maintainer rather
than opening a public issue with working exploit details.

## Credential handling

- GitHub tokens are stored in Windows Credential Manager.
- Tokens are never written to the application settings JSON.
- OAuth Client Secret is not embedded in the application.
- The OAuth Device Flow release build embeds only the public OAuth Client ID.

## GitHub permissions

The current MVP requests `read:user public_repo workflow` because GitHub OAuth
Apps do not provide repository-by-repository selection. The application uses
these permissions only to:

1. identify the signed-in user;
2. create or update the user's public `Pake` fork;
3. enable and dispatch the Pake workflow;
4. read the workflow status and download its artifact.

A future GitHub App integration should replace the OAuth App flow to provide
repository-scoped installation permissions.
