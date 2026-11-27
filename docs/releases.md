# Public releases and code signing

The canonical version is Directory.Build.props. A push of a matching vX.Y.Z tag, an explicit release/vX.Y.Z branch, or a manual publication run from dev builds and tests the standalone Windows package. Do not reuse a version after publication; a signed replacement requires a new version. CI publishes the installer, ZIP, release metadata and SHA256SUMS, with GitHub build attestations. Releases can be downloaded without an account. Unsigned versions are explicitly identified as unsigned.

## Authenticode signing (optional until approved)

The pipeline supports SignPath, including the open-source signing service, without storing a certificate/private key in GitHub. In a protected publication configuration, set repository variables SIGNPATH_ENABLED=true, SIGNPATH_ORGANIZATION_ID, SIGNPATH_PROJECT_SLUG, SIGNPATH_SIGNING_POLICY_SLUG, SIGNPATH_PAYLOAD_CONFIGURATION_SLUG, SIGNPATH_SETUP_CONFIGURATION_SLUG, SIGNPATH_CERT_THUMBPRINT; store the restricted signing API token in the SIGNPATH_API_TOKEN secret. The payload configuration must preserve payload/ and installer/ and sign first-party EXE/DLL files, and the setup configuration must sign the final NSIS EXE. The pipeline verifies Windows trust and the expected certificate before it publishes or installs any returned signed artifact. Configure the approved policy and restrict signing to the release workflow. SignPath must approve the project and configure its artifact rules; the workflow does not represent approval. Never store a key or token in source code.

The installer can also be signed by another approved code-signing provider, but its final bytes must be hashed and attested after signing and before public publication. Existing versions must not be overwritten.

## Consuming a release

Verify the official binary at build time with `gh attestation verify <setup.exe> --repo Tojo58/orinasako_storage_agent --signer-workflow Tojo58/orinasako_storage_agent/.github/workflows/ci.yml`. Pin its SHA-256 and version in the consumer installer. A hash fetched from the same download server at installation time is not a substitute for an embedded trusted digest or a signed manifest. The metadata JSON describes the published binary; it is not an RSA-signed update manifest. Consumers may maintain independent update trust roots. No Orinasako credentials or remote services are necessary to install Orinasako Pulse.

## Rename and compatibility

Orinasako Pulse is the product identity from 2.0.0. The GitHub repository retains its historical slug until the owner renames it. The existing wire protocol, persistent directory `StorageAgent` and DPAPI entropy are kept stable for compatibility. The Windows service becomes `OrinasakoPulse`; the installer retires the old registration after successful start. Validate migration on Windows before production.
