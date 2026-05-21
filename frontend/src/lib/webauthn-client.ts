// WebAuthn (passkey) browser helpers. The API speaks fido2-net-lib's JSON shape, which encodes
// every binary field as base64url and matches the standard WebAuthn camelCase contract. These
// helpers convert that shape to/from the `ArrayBuffer`s the `navigator.credentials` API needs.

export function isPasskeySupported(): boolean {
  return (
    typeof window !== 'undefined' &&
    typeof window.PublicKeyCredential !== 'undefined' &&
    typeof navigator !== 'undefined' &&
    !!navigator.credentials
  );
}

function base64urlToBuffer(value: string): ArrayBuffer {
  const padded = value.replace(/-/g, '+').replace(/_/g, '/');
  const base64 = padded + '=='.slice(0, (4 - (padded.length % 4)) % 4);
  const binary = atob(base64);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
  return bytes.buffer;
}

function bufferToBase64url(buffer: ArrayBuffer): string {
  const bytes = new Uint8Array(buffer);
  let binary = '';
  for (let i = 0; i < bytes.length; i++) binary += String.fromCharCode(bytes[i]);
  return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

interface ServerDescriptor {
  type: PublicKeyCredentialType;
  id: string;
  transports?: AuthenticatorTransport[];
}

/** Maps fido2-net-lib's creation-options JSON into a `PublicKeyCredentialCreationOptions`. */
function toCreationOptions(json: Record<string, unknown>): PublicKeyCredentialCreationOptions {
  const user = json.user as { id: string; name: string; displayName: string };
  const exclude = (json.excludeCredentials as ServerDescriptor[] | undefined) ?? [];
  return {
    ...(json as object),
    challenge: base64urlToBuffer(json.challenge as string),
    user: { ...user, id: base64urlToBuffer(user.id) },
    excludeCredentials: exclude.map((c) => ({
      type: c.type,
      id: base64urlToBuffer(c.id),
      transports: c.transports
    }))
  } as PublicKeyCredentialCreationOptions;
}

/** Maps fido2-net-lib's request-options JSON into a `PublicKeyCredentialRequestOptions`. */
function toRequestOptions(json: Record<string, unknown>): PublicKeyCredentialRequestOptions {
  const allow = (json.allowCredentials as ServerDescriptor[] | undefined) ?? [];
  return {
    ...(json as object),
    challenge: base64urlToBuffer(json.challenge as string),
    allowCredentials: allow.map((c) => ({
      type: c.type,
      id: base64urlToBuffer(c.id),
      transports: c.transports
    }))
  } as PublicKeyCredentialRequestOptions;
}

/** Serializes a registration credential into the JSON shape the API expects. */
function attestationToJson(credential: PublicKeyCredential) {
  const response = credential.response as AuthenticatorAttestationResponse;
  const transports =
    typeof response.getTransports === 'function' ? response.getTransports() : undefined;
  return {
    id: credential.id,
    rawId: bufferToBase64url(credential.rawId),
    type: credential.type,
    clientExtensionResults: credential.getClientExtensionResults(),
    response: {
      attestationObject: bufferToBase64url(response.attestationObject),
      clientDataJSON: bufferToBase64url(response.clientDataJSON),
      transports
    }
  };
}

/** Serializes an authentication assertion into the JSON shape the API expects. */
function assertionToJson(credential: PublicKeyCredential) {
  const response = credential.response as AuthenticatorAssertionResponse;
  return {
    id: credential.id,
    rawId: bufferToBase64url(credential.rawId),
    type: credential.type,
    clientExtensionResults: credential.getClientExtensionResults(),
    response: {
      authenticatorData: bufferToBase64url(response.authenticatorData),
      clientDataJSON: bufferToBase64url(response.clientDataJSON),
      signature: bufferToBase64url(response.signature),
      userHandle: response.userHandle ? bufferToBase64url(response.userHandle) : null
    }
  };
}

/** Runs the registration ceremony from server options; returns the API-bound attestation JSON. */
export async function createPasskey(options: Record<string, unknown>) {
  const credential = (await navigator.credentials.create({
    publicKey: toCreationOptions(options)
  })) as PublicKeyCredential | null;
  if (!credential) throw new Error('Passkey creation was cancelled.');
  return attestationToJson(credential);
}

/** Runs the authentication ceremony from server options; returns the API-bound assertion JSON. */
export async function getPasskeyAssertion(options: Record<string, unknown>) {
  const credential = (await navigator.credentials.get({
    publicKey: toRequestOptions(options)
  })) as PublicKeyCredential | null;
  if (!credential) throw new Error('Passkey sign-in was cancelled.');
  return assertionToJson(credential);
}
