# Private release distribution

## Authentication and storage

- Human pages and installation approvals use the existing Google session and room permissions.
- Bootstrap package downloads continue to use the enrolled device credential.
- Automatic app and helper updates use `/api/updates/` on the device hostname. Each PC generates a
  P-256 key pair and stores its private key under its existing user profile, protected by Windows DPAPI.
- The already-authenticated coordinator reports only the public identity in its app snapshot. The
  control plane binds that identity to the enrolled device. Unregistered, replaced and revoked keys
  cannot download updates. Signed requests bind method, full request target, time and nonce.
- `GITHUB_RELEASE_READ_TOKEN` is a server secret with Contents and Metadata read-only access to the
  SilentStream repository. Clients receive neither this key nor GitHub's temporary asset URLs.
- GitHub holds immutable release artifacts. The Worker validates release identity and asset metadata,
  follows GitHub's asset redirect without forwarding the credential, and streams bytes with `no-store`.

## Rollout gate

1. Deploy the private distribution API and configure the restricted server secret.
2. Publish the transition app while the old public update source remains available.
3. Update new-install package metadata and verify authenticated installer/package streaming.
4. For every enrolled, non-revoked PC, verify the transition app version, registered update identity
   and `lastAuthenticatedFeedUtc` in a fresh authenticated status snapshot. Do not infer app version
   from the coordinator's `agentVersion`.
5. Make the GitHub repository private only when all enrolled PCs pass the gate. An offline PC or
   unavailable app keeps the gate closed; preserve the public bridge until that PC is migrated.
6. Verify anonymous GitHub source/release requests fail, protected website routes have no content,
   M112 replay remains readable, and signed device updates and new Google-approved installs work.

Do not place the server token in Git, deployment variables, an installer, a public status response,
or a client-side script. Renew or revoke it through GitHub and update the Cloudflare secret when needed.
