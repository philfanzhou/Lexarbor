namespace Lexarbor.Host.Authentication;

// Covers both authentication Read and endpoint Revoke; neither may bypass browser
// cleanup on logout when storage cannot confirm revocation. Never log provider details.
internal sealed class AdminSessionLogoutMetadata;
