package com.musichoarder.app.data

import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.put

/** The server no longer has the passkey that was picked: it was removed from its account. */
class UnknownPasskeyException :
    Exception("That passkey was removed from its account. Sign in another way, then add a new one.")

/**
 * The pure half of the WebAuthn Signal API on Android, kept free of Android types so it can be
 * unit-tested: reading the server's refusal, and building the request that tells Credential
 * Manager to drop a passkey the server no longer has. The web does the same with
 * `PublicKeyCredential.signalUnknownCredential` (`#lib/webauthn-client`).
 */
object PasskeySignal {
    private val json = Json { ignoreUnknownKeys = true; isLenient = true }

    /** The `error` code of an API error body (`{"error":"unknown_credential"}`), or null. */
    fun errorCode(body: String): String? = runCatching {
        (json.parseToJsonElement(body) as? JsonObject)?.get("error")?.jsonPrimitive?.contentOrNull
    }.getOrNull()

    /**
     * The JSON for `SignalUnknownCredentialRequest`: the RP id from the server's request options
     * and the credential id from Credential Manager's assertion. Null when either is missing.
     */
    fun unknownCredentialRequestJson(requestJson: String, assertionJson: String): String? {
        val rpId = stringField(requestJson, "rpId") ?: return null
        val credentialId = stringField(assertionJson, "id") ?: return null
        return buildJsonObject {
            put("rpId", rpId)
            put("credentialId", credentialId)
        }.toString()
    }

    private fun stringField(source: String, name: String): String? = runCatching {
        (json.parseToJsonElement(source) as? JsonObject)?.get(name)?.jsonPrimitive?.contentOrNull
    }.getOrNull()?.takeIf { it.isNotBlank() }
}
